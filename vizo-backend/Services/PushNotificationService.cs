using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using Microsoft.AspNetCore.SignalR;
using WebPush;

namespace vizo_backend.Services;

/// <summary>
/// The one way anything in this application tells a person something happened.
///
/// It does two things on every call, in this order:
///
///   1. WRITES A Notification ROW. Always. This is the bell in the header, and
///      it is the record -- somebody who was asleep, offline, or had push
///      switched off must still find out. Until this class existed the
///      Notification table had a controller that could read it and not one line
///      anywhere that wrote to it, so the bell showed six seed rows and always
///      would.
///
///   2. TRIES TO PUSH. Best effort. A browser that has allowed notifications
///      gets it on the lock screen; one that has not, does not; a push service
///      that is down loses that one delivery and nothing else.
///
/// ─────────────────────────── THE RULE THAT MATTERS ─────────────────────────
///
/// A FAILURE HERE MUST NEVER FAIL THE REQUEST THAT CAUSED IT.
///
/// By the time this runs the order is taken, the stock has moved and the money
/// is in the drawer. Failing the request because a push service was briefly
/// unreachable would tell the operator the sale did not happen, and they would
/// ring it up twice. Everything below logs and swallows -- the same rule
/// DocumentArchive has followed since the Cloudinary work.
///
/// Call it at the END of a controller action, after the database work has
/// already succeeded.
/// </summary>
public class PushNotificationService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly ILogger<PushNotificationService> _logger;
    private readonly IHubContext<NotificationHub> _hub;
    private readonly WebPushClient _client = new();

    public PushNotificationService(AppDbContext db, IConfiguration cfg,
        ILogger<PushNotificationService> logger, IHubContext<NotificationHub> hub)
    {
        _db = db;
        _cfg = cfg;
        _logger = logger;
        _hub = hub;
    }

    /* Severity ids as they actually are in "SeverityLevel":
         1 info · 2 success · 3 warning · 4 danger · 5 muted
       Routine events are info; anything flagged severe is danger, which is what
       the bell colours red and what earns a vibration. */
    private const int SeverityInfo = 1;
    private const int SeverityDanger = 4;

    /// <summary>
    /// Now, with Kind=Unspecified.
    ///
    /// Every timestamp column in this schema is "timestamp without time zone",
    /// and Npgsql flatly refuses to write a DateTime whose Kind is Utc into
    /// one. ApiControllerBase.Now() exists for exactly this and is protected,
    /// so a service cannot reach it -- hence the copy. Calling UtcNow directly
    /// here threw on every single notification, and because this class swallows
    /// its own failures it did so silently.
    /// </summary>
    private static DateTime Now() => BusinessClock.Now();

    private VapidDetails? Vapid()
    {
        var subject = _cfg["VapidSettings:Subject"];
        var pub = _cfg["VapidSettings:PublicKey"];
        var priv = _cfg["VapidSettings:PrivateKey"];

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(pub) || string.IsNullOrWhiteSpace(priv))
            return null;

        return new VapidDetails(subject, pub, priv);
    }

    /// <summary>Whether a push can even be attempted. The bell works regardless.</summary>
    public bool PushConfigured => Vapid() is not null;

    // ══════════════════════════════════════════════════════════════════
    //  THE ENTRY POINTS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tell one person. Writes their bell row, then tries their browsers.
    /// </summary>
    /// <param name="kind">
    /// What sort of event this is, e.g. "ORDER_CREATED". Used for the per-user
    /// on/off switch, so it must be stable -- renaming one silently turns it
    /// back on for everybody who had switched it off.
    /// </param>
    /// <param name="purpose">
    /// The short "what happened" that follows "VIZO — " in the title, e.g.
    /// "Order created by Ahmed".
    /// </param>
    public async Task NotifyAsync(
        int userId, string kind, string purpose, string body,
        string? url = null, bool severe = false)
    {
        if (userId <= 0) return;

        try
        {
            var pref = await _db.NotificationPreferences.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == userId && p.Kind == kind);

            /* No row means on. Only the things somebody has deliberately
               switched off are ever stored. */
            var bellOn = pref?.BellEnabled ?? true;
            var pushOn = pref?.PushEnabled ?? true;

            var title = Title(purpose);

            /* WHO THIS WAS SENT TO, in the message itself. The same event goes
               to four or five people at once and the wording never said which
               copy you were holding -- so somebody reading "Order invoiced"
               could not tell whether it had been addressed to them, to the
               order desk, or to the owner. Full name and the role in words,
               because "zain" and "order-dept" are not what anybody calls
               themselves out loud. */
            body = await AddressedTo(userId, body);

            /* A path becomes a real link. See Services/AppLinks.cs -- a bare
               "/sales/orders/42" is meaningless to the service worker that
               opens a push, and meaningless again the moment somebody forwards
               the text to a colleague. */
            url = AppLinks.Absolute(_cfg, url);

            if (bellOn)
            {
                var row = new Notification
                {
                    UserId = userId,
                    SeverityId = severe ? SeverityDanger : SeverityInfo,
                    Icon = "bell",
                    Title = Truncate(title, 120),
                    /* 300, not 400. The column is VARCHAR(300); the old figure
                       was wrong and a long body threw on the way in -- silently,
                       because this class swallows its own failures. */
                    Body = Truncate(body, 300),
                    /* Where the bell sends you when the row is clicked. Every
                       call site already passes this; until now only Web Push
                       ever saw it. */
                    Url = TruncateOrNull(url, 300),
                    CreatedAt = Now(),
                    IsRead = false
                };
                _db.Notifications.Add(row);
                await _db.SaveChangesAsync();

                /* Straight down the wire to whoever has the screen open, so the
                   bell moves now rather than at the next page load. Sent after
                   the save so the id is real and the browser can mark it read.
                   See Services/NotificationHub.cs. */
                await LiveAsync(userId, row, url);
            }

            if (pushOn) await PushAsync(userId, title, body, url, severe);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not notify user {UserId} about {Kind}.", userId, kind);
        }
    }

    /// <summary>
    /// Tell everyone holding a role, e.g. "super-admin". Skips
    /// <paramref name="exceptUserId"/> -- nobody needs telling about the thing
    /// they just did themselves.
    /// </summary>
    public async Task NotifyRoleAsync(
        string roleKey, string kind, string purpose, string body,
        string? url = null, bool severe = false, int? exceptUserId = null)
    {
        try
        {
            var userIds = await _db.Users.AsNoTracking()
                .Where(u => u.IsActive && u.Role.RoleKey == roleKey)
                .Select(u => u.UserId)
                .ToListAsync();

            foreach (var id in userIds)
            {
                if (exceptUserId is not null && id == exceptUserId) continue;
                await NotifyAsync(id, kind, purpose, body, url, severe);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not notify role {Role} about {Kind}.", roleKey, kind);
        }
    }

    /// <summary>
    /// Tell several roles at once, without telling anyone twice.
    ///
    /// Somebody can hold only one role in this schema, but the call sites
    /// routinely name overlapping audiences ("Admin and whoever raised it"),
    /// and de-duplicating here rather than at each of the forty call sites is
    /// the difference between one notification and two.
    /// </summary>
    public async Task NotifyRolesAsync(
        IEnumerable<string> roleKeys, string kind, string purpose, string body,
        string? url = null, bool severe = false, int? exceptUserId = null,
        IEnumerable<int>? alsoUserIds = null)
    {
        try
        {
            var keys = roleKeys.Distinct().ToList();

            var ids = await _db.Users.AsNoTracking()
                .Where(u => u.IsActive && keys.Contains(u.Role.RoleKey))
                .Select(u => u.UserId)
                .ToListAsync();

            if (alsoUserIds is not null) ids.AddRange(alsoUserIds);

            foreach (var id in ids.Distinct())
            {
                if (id <= 0) continue;
                if (exceptUserId is not null && id == exceptUserId) continue;
                await NotifyAsync(id, kind, purpose, body, url, severe);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not notify roles for {Kind}.", kind);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE PUSH ITSELF
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Nudges one person's open tabs. Failure here is a silent nothing on
    /// purpose -- the row is already saved, so the worst case is that the bell
    /// updates on the next page load exactly as it used to.
    /// </summary>
    private async Task LiveAsync(int userId, Notification row, string? url)
    {
        try
        {
            await _hub.Clients.Group(NotificationHub.UserGroup(userId))
                .SendAsync("notification", new
                {
                    id = row.NotificationId,
                    title = row.Title,
                    body = row.Body,
                    icon = row.Icon,
                    severityId = row.SeverityId,
                    createdAt = row.CreatedAt,
                    isRead = false,
                    url
                });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live notification to user {UserId} did not go out.", userId);
        }
    }

    private async Task PushAsync(int userId, string title, string body, string? url, bool severe)
    {
        var vapid = Vapid();
        if (vapid is null)
        {
            /* Debug, not warning. A deployment that has not set up push yet is
               a choice, not a fault, and the bell still works. */
            _logger.LogDebug("VAPID is not configured; the bell row was written but no push was sent.");
            return;
        }

        List<Models.PushSubscription> subs;
        try
        {
            subs = await _db.PushSubscriptions.Where(s => s.UserId == userId).ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read push subscriptions for user {UserId}.", userId);
            return;
        }

        if (subs.Count == 0) return;

        /* The shape public/sw.js expects. Icon and badge are the VIZO mark --
           a notification with the shop's logo on it is one people trust; the
           browser's default puzzle-piece is one they swipe away. */
        var payload = JsonSerializer.Serialize(new
        {
            title,
            body,
            /* Absolute by the time it gets here, but the fallback has to be
               too -- sw.js opens this with clients.openWindow, and a bare path
               there resolves against the service worker's scope rather than
               against the site. */
            url = url ?? AppLinks.Absolute(_cfg, "/dashboard"),
            icon = "/icon-192.png",
            badge = "/badge-96.png",
            severe
        });

        var dead = new List<Models.PushSubscription>();

        foreach (var sub in subs)
        {
            try
            {
                var target = new WebPush.PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await _client.SendNotificationAsync(target, payload, vapid);
                sub.LastUsedAt = Now();
            }
            catch (WebPushException ex) when (
                ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                /* 410 Gone / 404 is the push service saying this browser is
                   never coming back -- permission revoked, or the profile
                   deleted. Keeping it means retrying it forever. */
                dead.Add(sub);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Push failed for user {UserId}.", userId);
            }
        }

        try
        {
            if (dead.Count > 0) _db.PushSubscriptions.RemoveRange(dead);
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tidy push subscriptions for user {UserId}.", userId);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  WORDING
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every notification this application sends is titled
    /// <c>VIZO — &lt;what happened&gt;</c>.
    ///
    /// A phone shows perhaps forty characters of a title on the lock screen, so
    /// the brand goes first and the event immediately after -- "VIZO — Order
    /// created by Ahmed". No other prefix, no per-module variation: a person
    /// glancing at a locked phone should recognise it as this shop's before
    /// they read a word of it.
    /// </summary>
    public static string Title(string purpose)
    {
        var what = (purpose ?? "").Trim();
        if (what.Length == 0) return "VIZO";

        /* An em dash, matching the wording used across the printed documents. */
        return $"VIZO — {what}";
    }

    /// <summary>
    /// Puts "For &lt;Full Name&gt; (&lt;Role&gt;)." on the end of a body.
    ///
    /// Read from the database rather than from the JWT, because the person
    /// being TOLD is not the person who acted -- the token belongs to whoever
    /// pressed the button, and this line is about whoever is about to read it.
    ///
    /// The original wording is trimmed first when it would not otherwise fit:
    /// the column is 300 characters, and losing the end of a sentence is a far
    /// smaller failure than losing the whole row, which is what used to happen
    /// when a long body overflowed. An unknown user id simply gets the body
    /// back unchanged -- a notification with no addressee still beats no
    /// notification.
    /// </summary>
    private async Task<string> AddressedTo(int userId, string body)
    {
        try
        {
            var who = await _db.Users.AsNoTracking()
                .Where(u => u.UserId == userId)
                .Select(u => new { u.FullName, role = u.Role.RoleName })
                .FirstOrDefaultAsync();

            if (who is null || string.IsNullOrWhiteSpace(who.FullName)) return body;

            var line = $"For {who.FullName.Trim()} ({who.role}).";
            var text = (body ?? "").Trim();

            /* +1 for the space between the two. */
            var room = 300 - line.Length - 1;
            if (room <= 0) return line;

            return text.Length == 0 ? line : $"{Truncate(text, room)} {line}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not name the recipient of a notification to user {UserId}.", userId);
            return body;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..(max - 1)] + "…";

    /* Same, for the nullable url. A null stays null -- the bell reads that as
       "nothing to open" rather than as an empty link. Named differently rather
       than overloaded, because nullability alone does not distinguish an
       overload to the compiler. */
    private static string? TruncateOrNull(string? s, int max) =>
        s is null ? null : Truncate(s, max);
}
