using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// Shared plumbing for EVERY controller in this API -- admin and non-admin alike.
///
/// This is NOT a service layer. It is plain ASP.NET controller inheritance:
/// no interface, nothing registered in DI, nothing injected anywhere. It exists
/// so the same five helpers are not copy-pasted into ten files, and so every
/// action reports failure the same way.
///
/// The rule the whole API follows: EVERY action body sits inside try/catch,
/// and the catch calls <see cref="Fail"/>. Before this, 46 admin actions had
/// zero try/catch between them -- any exception surfaced to the browser as a
/// bare 500 with an empty body, which is why failures looked like "nothing
/// happened" instead of "this is what went wrong".
/// </summary>
public abstract class ApiControllerBase : ControllerBase
{
    protected readonly AppDbContext _db;
    protected readonly IConfiguration _cfg;
    protected readonly ILogger _logger;
    protected readonly IWebHostEnvironment _env;

    protected ApiControllerBase(AppDbContext db, IConfiguration cfg,
        ILogger logger, IWebHostEnvironment env)
    {
        _db = db;
        _cfg = cfg;
        _logger = logger;
        _env = env;
    }

    /// <summary>
    /// The single failure path for every admin action.
    ///
    /// Logs the whole exception server-side, then answers with JSON the screen
    /// can actually show: what was being attempted, and the real message off
    /// the base exception (Npgsql puts the useful text there -- a constraint
    /// name, a null violation -- while the outer DbUpdateException only ever
    /// says "An error occurred while saving the entity changes").
    ///
    /// The stack trace is attached in Development only. It is the thing you
    /// want while building and the thing you must not ship.
    /// </summary>
    protected IActionResult Fail(Exception ex, string what)
    {
        _logger.LogError(ex, "Failed to {What} ({Method} {Path})",
            what, Request.Method, Request.Path);

        return StatusCode(500, new
        {
            message = $"Could not {what}.",
            error = ex.GetBaseException().Message,
            type = ex.GetBaseException().GetType().Name,
            detail = _env.IsDevelopment() ? ex.ToString() : null
        });
    }

    /* "timestamp without time zone" columns reject a Utc-kind DateTime, so every
       timestamp written goes through here. */
    /*  BUSINESS TIME IS PAKISTAN TIME, NOT UTC.

        These used to return DateTime.UtcNow. Pakistan is UTC+5, so between
        midnight and 5am local the UTC date is still YESTERDAY -- an order taken
        at 1am on the 3rd was written down as the 2nd. On a sales ledger that is
        not a rounding error: it moves a sale into the wrong day, the wrong
        week, and at month end the wrong month.

        The zone is resolved by id so the machine's own locale cannot change the
        answer. Windows calls it "Pakistan Standard Time" and Linux calls it
        "Asia/Karachi"; both are tried, and if a machine has neither the code
        falls back to a fixed +5 rather than silently reverting to UTC.

        The Kind stays Unspecified because every timestamp column in this schema
        is "timestamp without time zone" and Npgsql refuses anything else.      */

    private static readonly TimeZoneInfo BusinessZone = ResolveBusinessZone();

    private static TimeZoneInfo ResolveBusinessZone()
    {
        foreach (var id in new[] { "Pakistan Standard Time", "Asia/Karachi" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        /* Pakistan has not observed daylight saving since 2009, so a fixed
           offset is a correct fallback rather than an approximation. */
        return TimeZoneInfo.CreateCustomTimeZone("PKT", TimeSpan.FromHours(5), "Pakistan Time", "PKT");
    }

    /// <summary>Now, in Pakistan time, with Kind=Unspecified for Npgsql.</summary>
    protected static DateTime Now() => DateTime.SpecifyKind(
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BusinessZone), DateTimeKind.Unspecified);

    /// <summary>Today's business date in Pakistan, which is what a ledger means by "today".</summary>
    protected static DateOnly Today() => DateOnly.FromDateTime(Now());

    protected int CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    /// <summary>
    /// The signed-in person's name, from the JWT rather than the database --
    /// it is already in the token, and a notification is not worth a query.
    ///
    /// Used by every notification title: they all read
    /// "VIZO — Order created by Ahmed", and this is the "Ahmed".
    /// Falls back to the first name only, because a lock screen shows about
    /// forty characters of a title and "Muhammad Talha bin Suhail" would eat
    /// all of them.
    /// </summary>
    /// <summary>
    /// The signed-in person's role key -- "super-admin", "sales",
    /// "order-dept", "accountant".
    ///
    /// From the JWT, which already carries it. Every rule about who may do what
    /// to an order is decided from this.
    /// </summary>
    protected string CurrentRole() => User.FindFirstValue(ClaimTypes.Role) ?? "";

    protected string CurrentUserName(bool firstNameOnly = true)
    {
        var full = User.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrWhiteSpace(full)) return "somebody";
        if (!firstNameOnly) return full;

        var space = full.IndexOf(' ');
        return space > 0 ? full[..space] : full;
    }

    /* NOTE for anyone reading the scaffolded models: "User" carries TWO
       location collections and they are easy to mix up.
           User.Locations           -> locations this person is IN CHARGE OF
                                       (inverse of Location.InChargeUserId)
           User.LocationsNavigation -> the UserLocation junction, i.e. the
                                       locations they may WORK OUT OF
       Access control wants the second one. */

    protected static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    protected async Task Log(string action, string entityType, string reference,
        string? detail, int severityId)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            UserId = CurrentUserId(),
            ActionName = action,
            EntityType = entityType,
            EntityReference = reference,
            Detail = detail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            SeverityId = severityId,
            LoggedAt = Now()
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Next document number for a series, read straight off "DocumentSeries" so
    /// the prefix, padding and year setting a Super Admin picked at
    /// /admin/numbering are the ones actually used. Increments NextNumber in the
    /// same call.
    ///
    /// KNOWN LIMIT: the read and the increment are not atomic against another
    /// connection, so two documents created in the same instant can take the
    /// same number. The proper fix is one PostgreSQL sequence per series, which
    /// is a schema change -- it is written up in
    /// backend/Database/db_code_changes.txt section 3.1 rather than faked here.
    /// </summary>
    protected async Task<string> NextNumber(string prefix)
    {
        var series = await _db.DocumentSeries.FirstOrDefaultAsync(s => s.Prefix == prefix);
        if (series is null) return $"{prefix}-{Now():yyyyMMddHHmmss}";

        var n = series.NextNumber;
        series.NextNumber = n + 1;
        await _db.SaveChangesAsync();

        var year = series.IncludeYear ? $"{Now():yy}-" : "";
        return $"{series.Prefix}-{year}{n.ToString().PadLeft(series.Padding, '0')}";
    }

    /// <summary>
    /// The signed-in user as an EMPLOYEE id. Purchase-side documents
    /// (PurchaseOrder, GoodsReceipt, PurchaseInvoice, PurchaseReturn,
    /// StockAdjustment, StockTransfer) and Claim/Collection/Delivery all have
    /// FKs to "Employee", not "User" -- they share the same key, but only staff
    /// have an Employee row, so this checks rather than assumes.
    /// </summary>
    protected async Task<int?> CurrentEmployeeId()
    {
        var id = CurrentUserId();
        return await _db.Employees.AnyAsync(e => e.UserId == id) ? id : null;
    }
}
