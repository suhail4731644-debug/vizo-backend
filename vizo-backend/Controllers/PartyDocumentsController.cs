using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// Opening a shop account from its paperwork: the CNIC, the shop's business
/// card and the affidavit.
///
/// ─────────────────────── WHAT THIS DOES, AND WHAT IT DOES NOT ──────────────
///
/// It READS photographs and offers what it read. It does not decide anything.
/// Every field comes back to the screen in an editable box, the salesperson
/// corrects what is wrong, and what they save is what the database gets. The
/// model is a typist with good eyes, not a source of truth -- the same rule
/// the rest of this project applies to Gemini, stated the other way round.
///
/// ─────────────────────── WHERE EACH FIELD COMES FROM ───────────────────────
///
/// The owner was specific, and the prompt says exactly this:
///
///   Legal name      the CNIC's own name, WITHOUT the father's name
///   Display name    "cnic name - shop name - shop location", the shop name and
///                   the location both taken from the BUSINESS CARD
///   Industry        the shop's full name with its location
///   Phone / Alt     from the CNIC; both, when the card carries two
///   Email           from the business card, any domain
///   Address         from the BUSINESS CARD, never the CNIC -- this is the
///                   place goods are delivered to, not where the man sleeps
///   City            from the CNIC, matched against the Pakistani cities this
///                   database holds (they are stored "Karachi - Pakistan", so
///                   the match is on what comes before the " - ")
///   Category        Retailer, unless the card says otherwise
///
/// ENGLISH ONLY. A CNIC carries every field twice, once in Urdu; the Urdu is
/// ignored rather than transliterated, because a transliteration is a guess
/// wearing a fact's clothes.
///
/// ─────────────────────── WHEN IT CANNOT READ ───────────────────────────────
///
/// A photograph that is blurred, half out of frame or shot into the light
/// comes back as a problem naming that picture, and the screen asks for
/// another one. It does not half-fill the form: a name that is 80% right is
/// worse than an empty box, because nobody re-reads a box that is already
/// filled.
/// </summary>
[Route("api/parties/documents")]
[ApiController]
[Authorize(Policy = "Staff")]
public class PartyDocumentsController : ApiControllerBase
{
    private readonly GeminiClient _ai;
    private readonly IHttpClientFactory _http;

    public PartyDocumentsController(AppDbContext db, IConfiguration cfg,
        ILogger<PartyDocumentsController> logger, IWebHostEnvironment env,
        GeminiClient ai, IHttpClientFactory http)
        : base(db, cfg, logger, env)
    {
        _ai = ai;
        _http = http;
    }

    /// <summary>
    /// Whether reading a photograph is even possible on this installation.
    ///
    /// The screen asks first, because the whole first page of the new-customer
    /// flow is worth skipping when nothing can read an image: it offers the
    /// plain form instead of taking six pictures and then apologising.
    /// </summary>
    [HttpGet("reader")]
    public IActionResult Reader() => Ok(new
    {
        configured = _ai.IsConfigured,
        message = _ai.IsConfigured
            ? "Documents can be read."
            : "No reader is configured, so the details have to be typed in. "
            + "Set Gemini:ApiKey on the API to switch it on."
    });

    /// <summary>
    /// Reads an uploaded set and returns what should go in the form.
    ///
    /// The pictures are already on Cloudinary by the time this is called --
    /// the browser uploads them one at a time through /api/upload/image, so a
    /// slow line does not hold six photographs hostage to one request.
    /// </summary>
    [HttpPost("read")]
    public async Task<IActionResult> Read([FromBody] ReadRequest body, CancellationToken ct)
    {
        try
        {
            if (!_ai.IsConfigured)
                return Ok(new
                {
                    configured = false,
                    problems = Array.Empty<object>(),
                    fields = (object?)null,
                    message = "No reader is configured. Type the details in and save the pictures with them."
                });

            /* The order here is the order the prompt names them in. */
            var wanted = new List<(string Key, string? Url)>
            {
                ("cnicFront", body.CnicFrontUrl),
                ("cnicBack", body.CnicBackUrl),
                ("cardFront", body.CardFrontUrl),
                ("cardBack", body.CardBackUrl),
            };

            var images = new List<(string MimeType, byte[] Bytes)>();
            var named = new List<string>();
            var problems = new List<object>();
            var fetched = new Dictionary<string, byte[]>();

            foreach (var (key, url) in wanted)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;

                var bytes = await FetchJpeg(url!, ct);
                if (bytes is null)
                {
                    problems.Add(new { image = key, reason = "unreachable", message = "That picture could not be opened. Take it again." });
                    continue;
                }
                images.Add(("image/jpeg", bytes));
                named.Add(key);
                fetched[key] = bytes;
            }

            /* THE SAME PICTURE IN BOTH SLOTS.

               The owner's rule: front and back of a CNIC that are "very much
               the same" are an error. The browser already refuses the obvious
               case before uploading (a fingerprint comparison), but this
               endpoint cannot assume the browser did -- and identical bytes are
               the one case that needs no model and no guessing at all, so it is
               settled here, for the CNIC and the card, without spending a call.
               Hashing the JPEG Cloudinary hands back rather than the upload
               means two copies of one file match even if they were uploaded
               separately. */
            var same = false;
            foreach (var (front, back, what, explain) in new[]
            {
                ("cnicFront", "cnicBack", "CNIC",
                    "The back is the side with the address -- turn the card over and take it."),
                ("cardFront", "cardBack", "business card",
                    "Take the other side, or mark the back as not available."),
            })
            {
                if (fetched.TryGetValue(front, out var a) && fetched.TryGetValue(back, out var b)
                    && SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b)))
                {
                    same = true;
                    problems.Add(new
                    {
                        image = back,
                        reason = "same-picture",
                        message = $"The {what} front and back pictures are very much the same -- it is one picture twice. {explain}"
                    });
                }
            }

            if (same)
                return Ok(new
                {
                    configured = true,
                    problems,
                    fields = (object?)null,
                    message = "The front and back pictures are the same."
                });

            if (images.Count == 0)
                return Ok(new
                {
                    configured = true,
                    problems,
                    fields = (object?)null,
                    message = "There was nothing readable to work from."
                });

            /* The cities this database actually has, by name, so the model
               picks one of ours instead of inventing a spelling. They are
               stored "Karachi - Pakistan"; the part before the dash is what a
               CNIC prints. */
            var cities = await _db.Cities.AsNoTracking()
                .Where(c => c.Province.Country == "PK")
                .Select(c => new { c.CityId, c.CityName })
                .ToListAsync(ct);

            /* THE READER NEVER HEARS THE WORDS "FRONT" AND "BACK".

               It used to be told the pictures were cnicFront and cnicBack, and
               it believed the labels: sides put in the wrong slots came back
               "front" for the slot named front, whatever was on the card, and the
               swap went unnoticed. The pictures go under neutral names now, the
               reader says which side each one is from what is printed on it, and
               the names are turned back into slots below. */
            var neutral = new Dictionary<string, string>
            {
                ["cnicFront"] = "cnicPictureOne", ["cnicBack"] = "cnicPictureTwo",
                ["cardFront"] = "cardPictureOne", ["cardBack"] = "cardPictureTwo",
            };
            var slotOf = neutral.ToDictionary(kv => kv.Value, kv => kv.Key);

            var answer = await _ai.ReadImagesAsync(
                Prompt(named.Select(n => neutral[n]).ToList(), cities.Select(c => Short(c.CityName))), images, ct);

            if (string.IsNullOrWhiteSpace(answer))
                return Ok(new
                {
                    configured = true,
                    problems = new[] { new { image = "all", reason = "reader-failed", message = "The reader did not answer. Try again, or type the details in." } },
                    fields = (object?)null,
                    message = "The reader did not answer."
                });

            var shaped = Shape(answer!, cities.Select(c => (c.CityId, c.CityName)).ToList(), problems, slotOf);
            if (shaped is null)
            {
                /* THE ANSWER WAS NOT JSON THIS COULD READ.

                   It happens: a model truncates its own reply at the token
                   limit, or wraps it in ``` fences, or writes a sentence
                   instead. Until this was tested against a blurred card it
                   took the whole endpoint down with a JsonReaderException and
                   a stack trace on the screen. The salesperson is told to try
                   again or type it in -- which is what they would do anyway,
                   and the photographs are already safely uploaded. */
                _logger.LogWarning("Gemini answered with something that is not usable JSON: {Answer}",
                    answer!.Length <= 500 ? answer : answer[..500] + "…");

                return Ok(new
                {
                    configured = true,
                    problems = new[]
                    {
                        new
                        {
                            image = "all",
                            reason = "unreadable-answer",
                            message = "The documents could not be read clearly. Take the pictures again in better light, or type the details in."
                        }
                    },
                    fields = (object?)null,
                    message = "The reader's answer could not be understood."
                });
            }

            return Ok(shaped);
        }
        catch (Exception ex)
        {
            return Fail(ex, "read the customer's documents");
        }
    }

    /// <summary>
    /// Binds whatever photographs a customer has into one PDF, pushes it to
    /// the documents Cloudinary account and records the link on the party.
    ///
    /// Called after the account is created and again whenever a picture is
    /// replaced. Safe to call when there is nothing to bind -- it says so and
    /// changes nothing.
    /// </summary>
    [HttpPost("/api/parties/{id:int}/documents/pdf")]
    public async Task<IActionResult> BuildPdf(int id, CancellationToken ct)
    {
        try
        {
            var party = await _db.Parties.FirstOrDefaultAsync(p => p.UserId == id, ct);
            if (party is null) return NotFound(new { message = $"No party with id {id}." });

            var sheets = new List<(string Caption, string? Url)>
            {
                ("CNIC -- front", party.CnicFrontUrl),
                ("CNIC -- back", party.CnicBackUrl),
                ("Business card -- front", party.CardFrontUrl),
                ("Business card -- back", party.CardBackUrl),
                ("Affidavit -- page 1", party.AffidavitFrontUrl),
                ("Affidavit -- page 2", party.AffidavitBackUrl),
            };

            if (sheets.All(s => string.IsNullOrWhiteSpace(s.Url)))
                return Ok(new { built = false, message = "This customer has no documents on file yet." });

            var pages = new List<LegalDocsPdf.Sheet>();
            foreach (var (caption, url) in sheets)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    pages.Add(new LegalDocsPdf.Sheet(caption, "not provided", null));
                    continue;
                }
                var bytes = await FetchJpeg(url!, ct);
                pages.Add(new LegalDocsPdf.Sheet(caption,
                    bytes is null ? "could not be fetched" : null, bytes));
            }

            var details = await _db.Parties.AsNoTracking()
                .Where(p => p.UserId == id)
                .Select(p => new
                {
                    p.PartyCode, p.LegalName,
                    display = p.DisplayName ?? p.LegalName,
                    category = p.Category.CategoryName,
                    phone = p.User.Phone,
                    p.AltPhone,
                    email = p.User.Email,
                    p.AddressLine,
                    city = p.City.CityName,
                    p.Cnic, p.Ntn,
                    rep = p.SalesPersonUser != null ? p.SalesPersonUser.User.FullName : null,
                    openedBy = p.CreatedByUser != null ? p.CreatedByUser.FullName : null,
                    openedOn = p.User.CreatedAt
                })
                .FirstAsync(ct);

            var bytesPdf = LegalDocsPdf.Build(
                await DocumentBuilder.LetterHead(_db),
                new LegalDocsPdf.Owner(
                    details.PartyCode, details.LegalName, details.display, details.category,
                    details.phone, details.AltPhone, details.email, details.AddressLine, details.city,
                    details.Cnic, details.Ntn, details.rep, details.openedBy, details.openedOn),
                pages);

            /* Same store as every other PDF, and the same "does it actually
               serve" check -- Cloudinary blocks PDF delivery by default on
               accounts made since 2023 (trap 11). */
            var stored = await PdfStore.UploadAsync(_cfg,
                bytesPdf, $"{details.PartyCode}-legal-documents.pdf", "customer-documents");

            party.LegalDocsPdfUrl = stored.Url;
            party.LegalDocsPdfId = stored.PublicId;
            await _db.SaveChangesAsync(ct);

            await Log("CUSTOMER_DOCS_BUILT", "Party", details.PartyCode,
                $"{pages.Count(p => p.Jpeg is not null)} document page(s) bound into one PDF", 1);

            return Ok(new
            {
                built = true,
                url = stored.Url,
                deliverable = stored.Deliverable,
                pages = pages.Count(p => p.Jpeg is not null),
                message = "The legal documents PDF is ready."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"build the documents PDF for party {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE BITS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fetches a Cloudinary picture AS A BASELINE JPEG, whatever the phone
    /// took. `f_jpg` does the conversion on their side, `q_82,w_1600,c_limit`
    /// keeps a 12-megapixel photograph from becoming a 9 MB request for
    /// something that will be read once and printed onto half a page.
    ///
    /// Returns null rather than throwing: one unreachable picture must not
    /// take the whole set down.
    /// </summary>
    private async Task<byte[]?> FetchJpeg(string url, CancellationToken ct)
    {
        try
        {
            var transformed = url.Contains("/upload/")
                ? url.Replace("/upload/", "/upload/f_jpg,q_82,w_1600,c_limit/")
                : url;

            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var res = await client.GetAsync(transformed, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Could not fetch {Url}: {Status}", transformed, (int)res.StatusCode);
                return null;
            }
            return await res.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch {Url}", url);
            return null;
        }
    }

    /// <summary>"Karachi - Pakistan" -> "Karachi".</summary>
    private static string Short(string cityName)
    {
        var at = cityName.IndexOf(" - ", StringComparison.Ordinal);
        return at < 0 ? cityName.Trim() : cityName[..at].Trim();
    }

    /// <summary>The instruction the reader works to. See the class summary.</summary>
    private static string Prompt(IReadOnlyList<string> images, IEnumerable<string> cities) =>
        "You are reading photographs of Pakistani business documents so that a shop account can be opened.\n" +
        "The images attached are, in order: " + string.Join(", ", images) + ".\n" +
        "  cnicPictureOne / cnicPictureTwo -- photographs of the shopkeeper's CNIC (national identity card)\n" +
        "  cardPictureOne / cardPictureTwo -- photographs of the SHOP's own business card\n" +
        "\"One\" and \"Two\" are only the order the pictures were sent in. They are NOT labelled front and back: " +
        "either picture may be either side, and both may even be the same side. Decide from what is printed, never from the name.\n\n" +
        "READ ONLY WHAT IS PRINTED. Never guess a missing letter, never complete a name, never invent a number.\n" +
        "ENGLISH ONLY: a CNIC prints every field in Urdu as well. Ignore the Urdu completely -- do not transliterate it.\n" +
        "Skip placeholder text such as 'xyz'.\n\n" +
        "Answer with this JSON and nothing else:\n" +
        "{\n" +
        "  \"images\": [ { \"name\": \"cnicPictureOne\", \"readable\": true|false, \"reason\": \"blurred|glare|cropped|wrong-document|null\", \"side\": \"front|back|unknown\" } ],\n" +
        "  \"cnicName\": \"the card holder's own name, WITHOUT the father's name\",\n" +
        "  \"fatherName\": \"\",\n" +
        "  \"cnicNumber\": \"00000-0000000-0\",\n" +
        "  \"cnicCity\": \"the city on the CNIC address, one word if possible\",\n" +
        "  \"phones\": [\"03001234567\"],\n" +
        "  \"shopName\": \"the trading name printed on the business card\",\n" +
        "  \"shopLocation\": \"the market or area on the business card, e.g. Saddar\",\n" +
        "  \"shopAddress\": \"the full address from the BUSINESS CARD, not the CNIC\",\n" +
        "  \"email\": \"from the business card if there is one\",\n" +
        "  \"ntn\": \"\",\n" +
        "  \"category\": \"RETAILER|WHOLESALER|AGENT\"\n" +
        "}\n\n" +
        "RULES:\n" +
        "- \"side\" is for the two cnicPicture images only, and is about WHAT IS ON THE PICTURE. " +
        "A CNIC's FRONT shows the holder's photograph, the name, father's name, gender and identity number. " +
        "Its BACK shows the address, the barcode or QR code and the dates of issue and expiry. " +
        "Say \"front\" or \"back\" only when you can see that, otherwise \"unknown\". Use \"unknown\" for every card picture.\n" +
        "- Phone numbers as printed, either 03XXXXXXXXX or +923XXXXXXXXX. The CNIC's numbers come first, then the card's.\n" +
        "- cnicCity must be one of these, or empty if none of them fits: " + string.Join(", ", cities) + "\n" +
        "- If a picture is too blurred, too bright or cut off to read, set readable:false with the reason AND leave the fields it would have filled empty.\n" +
        "- Use \"\" for anything you cannot see. Never use null inside a string field.\n" +
        "- category is RETAILER by default. Choose WHOLESALER only when the card says wholesale and does NOT also say retail, " +
        "and AGENT only when it says agent, agency or distributor. A card that says \"Wholesale & Retail\" or \"Retail & Wholesale\" is RETAILER.";

    /// <summary>
    /// Turns the reader's JSON into the shape the form fills itself from --
    /// and applies the owner's rules about which field is built from what.
    /// </summary>
    private object? Shape(string json, IReadOnlyList<(int CityId, string CityName)> cities, List<object> problems,
        IReadOnlyDictionary<string, string> slotOf)
    {
        /* Models wrap JSON in ``` fences, prefix it with a sentence, or stop
           mid-string when they hit the token limit. Take the outermost braces
           and try; null means "not usable", and the caller says so politely
           rather than throwing a parser exception at the screen. */
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }

        using var parsed = doc;
        var root = doc.RootElement;

        string Str(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!.Trim()
                : "";

        /* Which pictures the reader could not read. The screen turns each one
           into "that picture is not clear -- take it again". */
        var flagged = new HashSet<string>();
        var sides = new Dictionary<string, string>();
        if (root.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in imgs.EnumerateArray())
            {
                var readable = !img.TryGetProperty("readable", out var r) || r.ValueKind != JsonValueKind.False;
                var said = img.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                /* Back to the slot the screen knows it by. */
                var name = slotOf.TryGetValue(said, out var slot) ? slot : said;

                if (readable)
                {
                    if (img.TryGetProperty("side", out var sd) && sd.ValueKind == JsonValueKind.String)
                        sides[name] = (sd.GetString() ?? "").Trim().ToLowerInvariant();
                    continue;
                }

                var reason = img.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
                flagged.Add(name);
                problems.Add(new
                {
                    image = name,
                    reason,
                    message = reason switch
                    {
                        "glare" => "Too much light on that picture to read it. Move away from the light and take it again.",
                        "cropped" => "Part of that document is outside the picture. Take it again with the whole card in frame.",
                        "wrong-document" => "That does not look like the document asked for. Check and take it again.",
                        _ => "That picture is not clear enough to read. Take it again."
                    }
                });
            }
        }

        /* THE WRONG SIDE IN A SLOT.

           A fingerprint (in the browser) and a byte comparison (above) catch a
           picture used twice. Neither can tell that two DIFFERENT photographs
           of the same face of the card were taken, or that the sides were put
           in the wrong way round -- only reading what is printed can. So the
           reader says which side each CNIC picture is, and a picture whose side
           does not match its slot is refused with the slot's own name, which is
           what sends the screen back to that tile. Left alone when the reader
           said "unknown": a doubt is not an error. */
        if (!flagged.Contains("cnicBack") && sides.TryGetValue("cnicBack", out var seenBack) && seenBack == "front")
        {
            flagged.Add("cnicBack");
            problems.Add(new
            {
                image = "cnicBack",
                reason = "wrong-side",
                /* "Very much the same" is the owner's wording for two pictures of
                   one side. When the sides are merely the wrong way round it would
                   be untrue, so it is said only when both slots show the front. */
                message = sides.TryGetValue("cnicFront", out var otherSide) && otherSide == "front"
                    ? "That is the FRONT of the CNIC again -- the front and back pictures are very much the same. The back is the side with the address; turn the card over and take it."
                    : "That is the FRONT of the CNIC. The back is the side with the address -- take the other side."
            });
        }
        if (!flagged.Contains("cnicFront") && sides.TryGetValue("cnicFront", out var seenFront) && seenFront == "back")
        {
            flagged.Add("cnicFront");
            problems.Add(new
            {
                image = "cnicFront",
                reason = "wrong-side",
                message = sides.TryGetValue("cnicBack", out var otherFace) && otherFace == "back"
                    ? "That is the BACK of the CNIC again -- the front and back pictures are very much the same. The front is the side with the photograph and the name; take that side."
                    : "That is the BACK of the CNIC. The front is the side with the photograph and the name -- take the other side."
            });
        }

        var cnicName = Str("cnicName");
        var shopName = Str("shopName");
        var shopLocation = Str("shopLocation");
        var address = Str("shopAddress");
        var email = Str("email");
        var cnicCity = Str("cnicCity");

        var phones = new List<string>();
        if (root.TryGetProperty("phones", out var ph) && ph.ValueKind == JsonValueKind.Array)
            foreach (var p in ph.EnumerateArray())
                if (p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                    phones.Add(p.GetString()!.Trim());

        /* THE DISPLAY NAME, exactly as the owner spelled it out:
           cnic name - shop name - shop location (the location from the CARD). */
        var display = string.Join(" - ",
            new[] { cnicName, shopName, shopLocation }.Where(s => s.Length > 0));

        /* The industry is the shop with its location -- what the business
           actually is, in the words on its own card. */
        var industry = string.Join(" ", new[] { shopName, shopLocation }.Where(s => s.Length > 0));

        /* The city, matched on the part before the " - " because that is how
           this database spells them ("Karachi - Pakistan"). */
        var city = cities.FirstOrDefault(c =>
            Short(c.CityName).Equals(cnicCity, StringComparison.OrdinalIgnoreCase));

        return new
        {
            configured = true,
            problems,
            fields = new
            {
                legalName = cnicName,
                displayName = display,
                industry,
                phone = phones.ElementAtOrDefault(0),
                altPhone = phones.ElementAtOrDefault(1),
                email,
                addressLine = address,
                cityId = city.CityId == 0 ? (int?)null : city.CityId,
                cityName = city.CityId == 0 ? null : city.CityName,
                cnic = Str("cnicNumber"),
                ntn = Str("ntn"),
                categoryKey = Str("category") is { Length: > 0 } c ? c.ToUpperInvariant() : "RETAILER",
                shopName,
                shopLocation
            },
            message = problems.Count == 0
                ? "Read from the documents. Check every box before saving."
                : "Some pictures could not be read. Take those again, or type the details in."
        };
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record ReadRequest(
        string? CnicFrontUrl, string? CnicBackUrl,
        string? CardFrontUrl, string? CardBackUrl);
}
