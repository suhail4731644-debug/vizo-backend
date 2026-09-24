using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace vizo_backend.Services;

/// <summary>
/// The one place this application talks to an AI model.
///
/// ─────────────────────────── THE RULE THAT GOVERNS ALL OF IT ───────────────
///
/// THE MODEL NEVER CALCULATES ANYTHING.
///
/// Every number -- how far sales fell, which customer stopped buying, what ran
/// out of stock -- is computed by SQL first and handed to the model as finished
/// JSON. The model's only job is to read those numbers and say, in a sentence a
/// shopkeeper would use, what they mean and what to do about it.
///
/// Ask a model "why did sales drop" without the numbers and it will invent an
/// answer, and the answer will sound completely convincing. Somebody will then
/// act on it. Numbers first, always.
///
/// ─────────────────────────── AND THREE MORE ────────────────────────────────
///
/// 1. The key never leaves the server. It is read from configuration (user
///    secrets locally, environment variables in production) and every call
///    originates here. Nothing AI-shaped is ever proxied to the browser with a
///    key attached.
///
/// 2. Everything this returns is a guess. Callers must label it on screen as
///    AI-written, never post an AI number to the ledger, and never let it
///    trigger an action by itself.
///
/// 3. If the model is down, the app is not. Every method returns null instead
///    of throwing, the failure is logged, and the caller shows its numbers
///    without the commentary -- the same fail-open rule DocumentArchive has
///    followed since the Cloudinary work. A sale must never fail because
///    Google had a bad afternoon.
/// </summary>
public class GeminiClient
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<GeminiClient> _logger;

    public GeminiClient(IHttpClientFactory http, IConfiguration cfg, ILogger<GeminiClient> logger)
    {
        _http = http;
        _cfg = cfg;
        _logger = logger;
    }

    private string? ApiKey => _cfg["Gemini:ApiKey"];
    private string Model => _cfg["Gemini:Model"] ?? "gemini-flash-latest";

    /// <summary>
    /// The models to fall back to when the first one is busy, in order.
    ///
    /// Google answers 503 "experiencing high demand" on the flash models often
    /// enough to matter: three attempts at the SAME model failed outright
    /// while this was being tested, and a minute later it read a business card
    /// perfectly. A salesperson standing in a shop with a customer's CNIC in
    /// their hand should not be the one who finds that out, so a busy model
    /// hands over to the next rather than giving up.
    ///
    /// Override with Gemini:FallbackModels as a comma-separated list.
    /// </summary>
    private IReadOnlyList<string> Models
    {
        get
        {
            var list = new List<string> { Model };
            var configured = _cfg["Gemini:FallbackModels"];
            foreach (var m in (configured ?? "gemini-flash-lite-latest,gemini-3.1-flash-lite")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!list.Contains(m, StringComparer.OrdinalIgnoreCase)) list.Add(m);
            }
            return list;
        }
    }
    private int TimeoutSeconds => int.TryParse(_cfg["Gemini:TimeoutSeconds"], out var t) ? t : 30;

    /// <summary>
    /// Whether an AI call can even be attempted. Screens call this to decide
    /// whether to offer the button at all -- a button that always fails is
    /// worse than no button.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.Equals(_cfg["Gemini:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ask the model to read <paramref name="factsJson"/> and answer.
    ///
    /// <paramref name="instruction"/> says what kind of answer is wanted;
    /// <paramref name="factsJson"/> is the SQL-computed truth it must work
    /// from. Returns null on any failure -- see rule 3 above.
    /// </summary>
    public async Task<string?> ExplainAsync(
        string instruction,
        string factsJson,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("Gemini is not configured; skipping the call.");
            return null;
        }

        /* The system instruction is deliberately blunt and repeated in the
           user turn. Models drift towards being helpful, and "helpful" here
           means inventing a figure that was not in the data. */
        var prompt =
            instruction.Trim() + "\n\n" +
            "RULES YOU MUST FOLLOW:\n" +
            "- Use ONLY the numbers in the DATA below. Never calculate a new one, never estimate, never guess.\n" +
            "- If the data does not answer something, say so plainly instead of filling the gap.\n" +
            "- Write the way a Pakistani shopkeeper speaks: short sentences, Roman Urdu mixed with English where that is natural.\n" +
            "- No preamble, no sign-off, no markdown headings. Just the answer.\n\n" +
            "DATA:\n" + factsJson;

        var body = new
        {
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } }
            },
            generationConfig = new
            {
                temperature = 0.2,      // low: this is analysis, not creative writing
                maxOutputTokens = 1024,
                topP = 0.9
            }
        };

        try
        {
            return await PostAsync(JsonSerializer.Serialize(body), TimeSpan.FromSeconds(TimeoutSeconds), "text", ct);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Gemini timed out after {Seconds}s.", TimeoutSeconds);
            return null;
        }
        catch (Exception ex)
        {
            /* Logged and swallowed on purpose. The caller has real numbers to
               show; losing the commentary is a much smaller loss than losing
               the screen. */
            _logger.LogWarning(ex, "Gemini call failed.");
            return null;
        }
    }

    /// <summary>
    /// Reads PHOTOGRAPHS and returns whatever JSON the instruction asked for.
    ///
    /// This is the one place the "the model never calculates" rule bends, and
    /// it is worth saying exactly how far. The model is not being asked to
    /// work anything out or to judge anything: it is being asked to READ what
    /// is printed on a CNIC and a shop card and type it back as JSON. Every
    /// field it returns is shown to the salesperson in an editable box before
    /// a customer is created, so nothing it gets wrong reaches the database
    /// without a person looking at it.
    ///
    /// Two things it is told to do that matter:
    ///   - ENGLISH ONLY. A CNIC carries the same name in Urdu and in English;
    ///     the Urdu is ignored rather than transliterated.
    ///   - SAY WHEN IT CANNOT READ. A blurred photograph or one shot into the
    ///     light must come back as readable:false with a reason, so the screen
    ///     can ask for another picture instead of inventing half a name.
    ///
    /// Returns null on any failure, like everything else here -- the caller
    /// then offers the form empty and the salesperson types it in.
    /// </summary>
    public async Task<string?> ReadImagesAsync(
        string instruction,
        IReadOnlyList<(string MimeType, byte[] Bytes)> images,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("Gemini is not configured; not reading the documents.");
            return null;
        }
        if (images.Count == 0) return null;

        /* One user turn: the instruction, then the pictures in the order the
           caller listed them. The instruction names them in that order. */
        var parts = new List<object> { new { text = instruction.Trim() } };
        foreach (var (mime, bytes) in images)
        {
            parts.Add(new
            {
                inline_data = new
                {
                    mime_type = string.IsNullOrWhiteSpace(mime) ? "image/jpeg" : mime,
                    data = Convert.ToBase64String(bytes)
                }
            });
        }

        var body = new
        {
            contents = new[] { new { role = "user", parts = parts.ToArray() } },
            generationConfig = new
            {
                temperature = 0,                 // transcription, not writing
                /* Room to spare. The reply is small, but a model that runs out
                   of tokens mid-string returns broken JSON, and broken JSON
                   from a blurred photograph is how this endpoint first fell
                   over. */
                maxOutputTokens = 4096,
                /* Ask for JSON and get JSON -- without this the model wraps it
                   in ```json fences half the time and the parse is a guess. */
                responseMimeType = "application/json"
            }
        };

        try
        {
            /* Pictures are slower than text: a CNIC pair can take fifteen
               seconds on a bad line, and timing out at the usual thirty means
               the salesperson types it all in for nothing. */
            return await PostAsync(JsonSerializer.Serialize(body),
                TimeSpan.FromSeconds(Math.Max(TimeoutSeconds, 60)), "vision", ct);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Gemini vision timed out.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini vision call failed.");
            return null;
        }
    }

    /// <summary>
    /// Sends one request to Gemini and gets an answer out of SOME model, or null.
    /// Shared by the text explanations and the document reader.
    ///
    /// It used to live only in the document reader. Every AI REPORT -- the sales
    /// drop, who to chase for money, dead stock, customers at risk, the demand
    /// forecast, margin watch, the month-end summary, "ask a question" and the
    /// nightly insight -- made ONE attempt on ONE model, so the first time
    /// gemini-flash-latest said "busy" or "quota exceeded" all of them went
    /// silent at once: aiAvailable true, explanation null, no error anywhere the
    /// user could see. Found by testing all of them in a row and watching every
    /// one come back empty in half a second.
    ///
    /// TWO GOES AT EACH MODEL, THEN THE NEXT MODEL:
    ///   400 / 401 / 403  ours to fix or the key's -- stop, asking again is only slower
    ///   404              Google retired that name -- next model
    ///   429              over its quota. Waiting a second will not refill a daily
    ///                    allowance -- next model at once
    ///   5xx              "high demand" -- once more after a second, then next model
    /// The key goes in a header, not the query string: a URL ends up in proxy logs
    /// and browser history in a way a header does not.
    /// </summary>
    private async Task<string?> PostAsync(string payload, TimeSpan timeout, string what, CancellationToken ct)
    {
        using var client = _http.CreateClient();
        client.Timeout = timeout;

        foreach (var model in Models)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-goog-api-key", ApiKey);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var res = await client.SendAsync(req, ct);
                var raw = await res.Content.ReadAsStringAsync(ct);

                if (res.IsSuccessStatusCode)
                {
                    if (!string.Equals(model, Model, StringComparison.OrdinalIgnoreCase))
                        _logger.LogInformation("Gemini {What} answered on the fallback model {Model}.", what, model);
                    return ReadFirstText(raw);
                }

                var status = (int)res.StatusCode;
                _logger.LogWarning("Gemini {What} {Model} returned {Status} (attempt {Attempt}): {Body}",
                    what, model, status, attempt, Trim(raw));

                if (status is 400 or 401 or 403) return null;    // ours to fix, not theirs
                if (status is 404 or 429) break;                 // gone, or out of quota: the next model
                if (attempt == 2) break;                         // busy twice: the next model
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }

        _logger.LogWarning("Gemini {What}: every model was busy, over its quota or missing.", what);
        return null;
    }

    /// <summary>
    /// Pull the answer text out of the response envelope.
    /// Returns null rather than throwing on any shape it does not recognise --
    /// a model that changes its response shape must not take a screen down.
    /// </summary>
    private string? ReadFirstText(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
                || candidates.GetArrayLength() == 0)
            {
                _logger.LogWarning("Gemini returned no candidates: {Body}", Trim(raw));
                return null;
            }

            var parts = candidates[0].GetProperty("content").GetProperty("parts");
            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                    sb.Append(text.GetString());
            }

            var answer = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(answer) ? null : answer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the Gemini response.");
            return null;
        }
    }

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
