using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /claims screens -- warranty claims, tracked PER ITEM and never per order.
///
/// A claim has two independent halves and the screens show both:
///   * what the CUSTOMER walked away with (ClaimOutcome) -- settled the moment
///     the item is handed back or replaced;
///   * where it stands with the SUPPLIER (ClaimStage) -- which can stay open
///     for weeks after the customer is finished with.
/// Collapsing those two into one status is the mistake this design avoids.
///
/// NOTE: the model class is `Claim`, which collides with
/// System.Security.Claims.Claim. This file does not import that namespace; the
/// controllers that need both alias one of them.
///
/// Controller-only by design: no DTOs, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports via Fail().
/// </summary>
[Route("api/claims")]
[ApiController]
/* NOT THE ORDER DESK'S, since 23 September. The owner: "remove Claims from
   [the Order Department panel]. There is no need for a Claims page in the
   Order Department panel." By role, ANDed with the BackOffice policy above --
   the same pattern PurchasesController uses to close a screen to one role
   while leaving it open to the others BackOffice already admits. proxy.ts's
   /claims rule and migration 25 (which takes claims.view/receive/settle off
   the order-dept role) say the same thing on the other two layers. */
[Authorize(Roles = "super-admin,accountant")]
[Authorize(Policy = "BackOffice")]
public class ClaimsController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public ClaimsController(AppDbContext db, IConfiguration cfg,
        ILogger<ClaimsController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    // ══════════════════════════════════════════════════════════════════
    //  LIST
    // ══════════════════════════════════════════════════════════════════

    [HttpGet]
    public async Task<IActionResult> GetClaims(
        [FromQuery] string? q, [FromQuery] string? stage, [FromQuery] bool openOnly = false)
    {
        try
        {
            var rows = _db.Claims.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(stage)) rows = rows.Where(c => c.Stage.StageKey == stage);
            if (openOnly) rows = rows.Where(c => c.Stage.IsOpen);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(c => c.ClaimNo.ToLower().Contains(term) ||
                                       (c.CustomerUser.DisplayName ?? c.CustomerUser.LegalName).ToLower().Contains(term) ||
                                       c.Product.ProductName.ToLower().Contains(term) ||
                                       c.Product.Sku.ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(c => c.ReceivedOn).ThenByDescending(c => c.ClaimId)
                .Select(c => new
                {
                    id = c.ClaimId,
                    claimNo = c.ClaimNo,
                    customerId = c.CustomerUserId,
                    customerName = (c.CustomerUser.DisplayName ?? c.CustomerUser.LegalName),
                    receivedOn = c.ReceivedOn,
                    receivedBy = c.ReceivedByUser.User.FullName,
                    productId = c.ProductId,
                    productName = c.Product.ProductName,
                    sku = c.Product.Sku,
                    imageUrl = c.Product.ImageUrl,
                    qty = c.Quantity,
                    unitCost = c.UnitCost,
                    value = c.Quantity * c.UnitCost,
                    reason = c.Reason.ReasonKey,
                    reasonLabel = c.Reason.ReasonName,
                    usuallyAccepted = c.Reason.UsuallyAccepted,
                    note = c.ClaimNote,
                    originalOrderNo = c.OriginalOrderNo,
                    customerOutcome = c.Outcome.OutcomeKey,
                    customerOutcomeLabel = c.Outcome.OutcomeName,
                    stage = c.Stage.StageKey,
                    stageLabel = c.Stage.StageName,
                    isOpen = c.Stage.IsOpen,
                    supplierId = c.SupplierUserId,
                    supplierName = c.SupplierUser != null ? (c.SupplierUser.DisplayName ?? c.SupplierUser.LegalName) : null,
                    sentOn = c.SentOn,
                    settledOn = c.SettledOn,
                    supplierNote = c.SupplierNote,
                    remindersSent = c.RemindersSent
                })
                .ToListAsync();

            var shaped = items.Select(c => new
            {
                c.id, c.claimNo, c.customerId, c.customerName,
                customerInitials = Initials(c.customerName),
                c.receivedOn, c.receivedBy, c.productId, c.productName, c.sku,
                c.qty, c.unitCost, c.value,
                c.reason, c.reasonLabel, c.usuallyAccepted, c.note, c.originalOrderNo,
                c.customerOutcome, c.customerOutcomeLabel,
                c.stage, c.stageLabel, c.isOpen,
                c.supplierId, c.supplierName, c.sentOn, c.settledOn, c.supplierNote,
                c.remindersSent,

                /* How long this has been sitting with the supplier. Drives the
                   reminder the Order Dept sees; derived, never stored. */
                daysWithSupplier = c.sentOn == null ? (int?)null
                    : ((c.settledOn ?? Today()).DayNumber - c.sentOn.Value.DayNumber)
            }).ToList();

            return Ok(new
            {
                openCount = shaped.Count(c => c.isOpen),
                openValue = shaped.Where(c => c.isOpen).Sum(c => c.value),
                totalValue = shaped.Sum(c => c.value),
                items = shaped
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the claims list");
        }
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetClaim(int id)
    {
        try
        {
            var c = await _db.Claims.AsNoTracking()
                .Where(x => x.ClaimId == id)
                .Select(x => new
                {
                    id = x.ClaimId,
                    claimNo = x.ClaimNo,
                    customerId = x.CustomerUserId,
                    customerName = (x.CustomerUser.DisplayName ?? x.CustomerUser.LegalName),
                    customerCode = x.CustomerUser.PartyCode,
                    customerPhone = x.CustomerUser.User.Phone,
                    receivedOn = x.ReceivedOn,
                    receivedBy = x.ReceivedByUser.User.FullName,
                    productId = x.ProductId,
                    productName = x.Product.ProductName,
                    sku = x.Product.Sku,
                    imageUrl = x.Product.ImageUrl,
                    qty = x.Quantity,
                    unitCost = x.UnitCost,
                    reasonId = x.ReasonId,
                    reason = x.Reason.ReasonKey,
                    reasonLabel = x.Reason.ReasonName,
                    usuallyAccepted = x.Reason.UsuallyAccepted,
                    note = x.ClaimNote,
                    originalOrderNo = x.OriginalOrderNo,
                    outcomeId = x.OutcomeId,
                    customerOutcome = x.Outcome.OutcomeKey,
                    customerOutcomeLabel = x.Outcome.OutcomeName,
                    stageId = x.StageId,
                    stage = x.Stage.StageKey,
                    stageLabel = x.Stage.StageName,
                    isOpen = x.Stage.IsOpen,
                    supplierId = x.SupplierUserId,
                    supplierName = x.SupplierUser != null ? (x.SupplierUser.DisplayName ?? x.SupplierUser.LegalName) : null,
                    sentOn = x.SentOn,
                    settledOn = x.SettledOn,
                    supplierNote = x.SupplierNote,
                    remindersSent = x.RemindersSent
                })
                .FirstOrDefaultAsync();

            if (c is null) return NotFound(new { message = $"No claim with id {id}." });

            return Ok(new
            {
                c.id, c.claimNo, c.customerId, c.customerName,
                customerInitials = Initials(c.customerName),
                c.customerCode, c.customerPhone, c.receivedOn, c.receivedBy,
                c.productId, c.productName, c.sku, c.qty, c.unitCost,
                value = c.qty * c.unitCost,
                c.reasonId, c.reason, c.reasonLabel, c.usuallyAccepted,
                c.note, c.originalOrderNo,
                c.outcomeId, c.customerOutcome, c.customerOutcomeLabel,
                c.stageId, c.stage, c.stageLabel, c.isOpen,
                c.supplierId, c.supplierName, c.sentOn, c.settledOn, c.supplierNote,
                c.remindersSent,
                daysWithSupplier = c.sentOn == null ? (int?)null
                    : ((c.settledOn ?? Today()).DayNumber - c.sentOn.Value.DayNumber)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load claim {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  MOVING A CLAIM ALONG
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends a claim on to the supplier. Stamps SentOn, which is what the
    /// "stuck with supplier" reminder counts from.
    /// </summary>
    /// <summary>
    /// A shopkeeper walks in with a dead piece. Recorded against the ITEM, not
    /// an order -- the customer rarely knows which invoice it came on, and
    /// "Claim" has no order FK, only a free-text OriginalOrderNo.
    ///
    /// UnitCost is taken from the product rather than the request: the value of
    /// a claim is what the stock cost us, and letting the browser name that
    /// figure would let the claim book be inflated from the front end.
    ///
    /// NOTE ON STOCK. This writes the claim row and nothing else. There is no
    /// CLAIM movement type and none of the seeded claims carry a StockMovement,
    /// so claim stock is tracked as a physical shelf rather than a system
    /// balance. Posting a movement here would invent behaviour the schema does
    /// not model.
    ///
    /// NOTE ON NUMBERING. "DocumentSeries" has no CLM row, so NextNumber falls
    /// back to a timestamp. The one-line insert that fixes this is in
    /// backend/database/db_code_changes.txt section 5.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateClaimRequest body)
    {
        try
        {
            if (body.Quantity <= 0)
                return BadRequest(new { message = "How many pieces came back?" });

            var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductId == body.ProductId);
            if (product is null) return BadRequest(new { message = "Pick an item that exists." });

            if (!await _db.Parties.AnyAsync(p => p.UserId == body.CustomerUserId))
                return BadRequest(new { message = "Pick a customer that exists." });

            var reason = await _db.ClaimReasons.FirstOrDefaultAsync(r => r.ReasonId == body.ReasonId);
            if (reason is null) return BadRequest(new { message = "Pick a reason." });

            var outcome = await _db.ClaimOutcomes.FirstOrDefaultAsync(o => o.OutcomeId == body.OutcomeId);
            if (outcome is null) return BadRequest(new { message = "Say what the customer got." });

            var stage = await _db.ClaimStages.FirstOrDefaultAsync(s => s.StageKey == "RECEIVED");
            if (stage is null) return BadRequest(new { message = "No RECEIVED stage is configured." });

            /* Claim.ReceivedByUserId points at Employee, not User -- they share
               the key, but only staff have an Employee row. */
            var employeeId = await CurrentEmployeeId();
            if (employeeId is null)
                return BadRequest(new { message = "Only staff accounts can receive a claim." });

            var claim = new Claim
            {
                ClaimNo = await NextNumber("CLM"),
                CustomerUserId = body.CustomerUserId,
                ReceivedOn = Today(),
                ReceivedByUserId = employeeId.Value,
                ProductId = body.ProductId,
                Quantity = body.Quantity,
                UnitCost = product.CostPrice,
                ReasonId = body.ReasonId,
                ClaimNote = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim(),
                OriginalOrderNo = string.IsNullOrWhiteSpace(body.OriginalOrderNo) ? null : body.OriginalOrderNo.Trim(),
                OutcomeId = body.OutcomeId,
                StageId = stage.StageId,
                RemindersSent = 0
            };

            _db.Claims.Add(claim);
            await _db.SaveChangesAsync();
            await Log("CLAIM_RECEIVED", "Claim", claim.ClaimNo,
                      $"{claim.Quantity} x {product.ProductName} ({reason.ReasonName})", 1);

            /* -- E1 -- a claim is money tied up in stock that cannot be sold,
               which is why Accounts hears about it and not just the warehouse. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.ClaimCreated,
                $"Claim raised by {CurrentUserName()}",
                $"{claim.ClaimNo} -- {claim.Quantity} x {product.ProductName}, " +
                $"PKR {claim.UnitCost * claim.Quantity:N0} ({reason.ReasonName}).",
                url: $"/claims/{claim.ClaimId}",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id = claim.ClaimId,
                claimNo = claim.ClaimNo,
                value = claim.UnitCost * claim.Quantity,
                message = $"{claim.ClaimNo} received into claim stock."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "receive the claim");
        }
    }

    [HttpPost("{id:int}/send")]
    public async Task<IActionResult> SendToSupplier(int id, [FromBody] SendClaimRequest body)
    {
        try
        {
            var claim = await _db.Claims.Include(c => c.Stage)
                .FirstOrDefaultAsync(c => c.ClaimId == id);
            if (claim is null) return NotFound(new { message = $"No claim with id {id}." });

            if (claim.SentOn is not null)
                return BadRequest(new { message = $"{claim.ClaimNo} was already sent on {claim.SentOn:yyyy-MM-dd}." });

            if (body.SupplierId is not null &&
                !await _db.Parties.AnyAsync(p => p.UserId == body.SupplierId))
                return BadRequest(new { message = "Pick a valid supplier." });

            var stage = await _db.ClaimStages.FirstOrDefaultAsync(s => s.StageKey == "SENT");
            if (stage is null) return BadRequest(new { message = "No SENT stage is configured." });

            claim.SupplierUserId = body.SupplierId ?? claim.SupplierUserId;
            if (claim.SupplierUserId is null)
                return BadRequest(new { message = "A claim needs a supplier before it can be sent." });

            claim.StageId = stage.StageId;
            claim.SentOn = body.SentOn ?? Today();
            claim.SupplierNote = body.Note ?? claim.SupplierNote;
            await _db.SaveChangesAsync();
            await Log("CLAIM_SENT", "Claim", claim.ClaimNo, body.Note, 1);

            /* -- E2 -- */
            await _push.NotifyRoleAsync(
                "super-admin",
                NotificationKinds.ClaimSent,
                $"Claim sent by {CurrentUserName()}",
                $"{claim.ClaimNo} has gone to the supplier.",
                url: $"/claims/{claim.ClaimId}",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{claim.ClaimNo} sent to the supplier." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"send claim {id} to the supplier");
        }
    }

    /// <summary>
    /// Records one more chase at the supplier. "Claim"."RemindersSent" is the
    /// count the claim screens show next to "asked N times already", so the
    /// button that says a reminder went out now actually moves that number.
    ///
    /// This bumps the counter and writes the activity row; it does not send the
    /// message itself -- there is no outbound WhatsApp or SMS channel wired up,
    /// and pretending otherwise is what this pass is removing.
    /// </summary>
    [HttpPost("{id:int}/remind")]
    public async Task<IActionResult> Remind(int id, [FromBody] RemindClaimRequest? body)
    {
        try
        {
            var claim = await _db.Claims.FirstOrDefaultAsync(c => c.ClaimId == id);
            if (claim is null) return NotFound(new { message = $"No claim with id {id}." });

            if (claim.SentOn is null)
                return BadRequest(new { message = $"{claim.ClaimNo} has not been sent to a supplier yet." });

            if (claim.SettledOn is not null)
                return BadRequest(new { message = $"{claim.ClaimNo} is already settled." });

            claim.RemindersSent = (short)(claim.RemindersSent + 1);
            if (!string.IsNullOrWhiteSpace(body?.Note)) claim.SupplierNote = body.Note;

            await _db.SaveChangesAsync();
            await Log("CLAIM_REMINDER", "Claim", claim.ClaimNo,
                      $"Chased the supplier (reminder {claim.RemindersSent})", 3);

            /* -- E3 -- */
            await _push.NotifyRoleAsync(
                "super-admin",
                NotificationKinds.ClaimReminded,
                $"Claim chased by {CurrentUserName()}",
                $"{claim.ClaimNo} -- reminder {claim.RemindersSent} sent to the supplier.",
                url: $"/claims/{claim.ClaimId}",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id,
                remindersSent = (int)claim.RemindersSent,
                message = $"Chase number {claim.RemindersSent} recorded against {claim.ClaimNo}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"record a reminder on claim {id}");
        }
    }

    /// <summary>Records the supplier's answer and closes the supplier half.</summary>
    [HttpPost("{id:int}/settle")]
    public async Task<IActionResult> Settle(int id, [FromBody] SettleClaimRequest body)
    {
        try
        {
            var claim = await _db.Claims.FirstOrDefaultAsync(c => c.ClaimId == id);
            if (claim is null) return NotFound(new { message = $"No claim with id {id}." });
            if (claim.SentOn is null)
                return BadRequest(new { message = $"{claim.ClaimNo} has not been sent to a supplier yet." });

            var stage = await _db.ClaimStages.FirstOrDefaultAsync(s => s.StageKey == body.StageKey);
            if (stage is null) return BadRequest(new { message = $"Unknown stage '{body.StageKey}'." });

            claim.StageId = stage.StageId;
            claim.SettledOn = body.SettledOn ?? Today();
            claim.SupplierNote = body.Note ?? claim.SupplierNote;
            await _db.SaveChangesAsync();
            await Log("CLAIM_SETTLED", "Claim", claim.ClaimNo, $"{stage.StageName}: {body.Note}", 2);

            /* -- E4 -- */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.ClaimSettled,
                $"Claim settled by {CurrentUserName()}",
                $"{claim.ClaimNo} -- {stage.StageName}, PKR {claim.UnitCost * claim.Quantity:N0}.",
                url: $"/claims/{claim.ClaimId}",
                exceptUserId: CurrentUserId());

            return Ok(new { id, stage = stage.StageKey, message = $"{claim.ClaimNo} marked {stage.StageName}." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"settle claim {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  SCORECARDS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Which suppliers actually honour their claims. Honour rate is settled
    /// against everything that reached a decision -- claims still in flight are
    /// left out of the denominator, otherwise a supplier looks worse simply for
    /// having recent claims.
    /// </summary>
    [HttpGet("supplier-scorecard")]
    public async Task<IActionResult> SupplierScorecard()
    {
        try
        {
            var rows = await _db.Claims.AsNoTracking()
                .Where(c => c.SupplierUserId != null)
                .Select(c => new
                {
                    supplierId = c.SupplierUserId!.Value,
                    supplierName = c.SupplierUser!.LegalName,
                    stage = c.Stage.StageKey,
                    isOpen = c.Stage.IsOpen,
                    value = c.Quantity * c.UnitCost,
                    c.SentOn,
                    c.SettledOn
                })
                .ToListAsync();

            var cards = rows.GroupBy(r => new { r.supplierId, r.supplierName })
                .Select(g =>
                {
                    var sent = g.Count(x => x.SentOn != null);
                    /* "Honoured" is REPLACED or CREDITED; "refused" is REJECTED or
                       WRITTEN_OFF. There is no single SETTLED stage -- the
                       supplier either replaces the item, credits it, refuses it,
                       or it is written off. */
                    var settled = g.Count(x => x.stage == "REPLACED" || x.stage == "CREDITED");
                    var refused = g.Count(x => x.stage == "REJECTED" || x.stage == "WRITTEN_OFF");
                    var decided = settled + refused;
                    var turnarounds = g.Where(x => x.SentOn != null && x.SettledOn != null)
                        .Select(x => x.SettledOn!.Value.DayNumber - x.SentOn!.Value.DayNumber)
                        .ToList();

                    return new
                    {
                        supplierId = g.Key.supplierId,
                        supplierName = g.Key.supplierName,
                        supplierInitials = Initials(g.Key.supplierName),
                        total = g.Count(),
                        sent,
                        settled,
                        refused,
                        open = g.Count(x => x.isOpen),
                        avgDays = turnarounds.Count == 0 ? 0 : (int)Math.Round(turnarounds.Average()),
                        honourRate = decided == 0 ? 0 : (int)Math.Round(100.0 * settled / decided),
                        valueOpen = g.Where(x => x.isOpen).Sum(x => x.value),
                        valueTotal = g.Sum(x => x.value)
                    };
                })
                .OrderByDescending(c => c.valueOpen)
                .ToList();

            return Ok(cards);
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the supplier claim scorecard");
        }
    }

    /// <summary>The items that come back most -- what to stop stocking.</summary>
    [HttpGet("worst-items")]
    public async Task<IActionResult> WorstItems([FromQuery] int limit = 10)
    {
        try
        {
            if (limit is < 1 or > 100) limit = 10;

            return Ok(await _db.Claims.AsNoTracking()
                .GroupBy(c => new { c.ProductId, c.Product.ProductName, c.Product.Sku })
                .Select(g => new
                {
                    productId = g.Key.ProductId,
                    productName = g.Key.ProductName,
                    sku = g.Key.Sku,
                    claims = g.Count(),
                    qty = g.Sum(c => c.Quantity),
                    value = g.Sum(c => c.Quantity * c.UnitCost)
                })
                .OrderByDescending(x => x.value)
                .Take(limit)
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the worst-items claim report");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            return Ok(new
            {
                stages = await _db.ClaimStages.AsNoTracking()
                    .Select(s => new { id = s.StageId, key = s.StageKey, name = s.StageName, isOpen = s.IsOpen })
                    .ToListAsync(),
                reasons = await _db.ClaimReasons.AsNoTracking()
                    .Select(r => new { id = r.ReasonId, key = r.ReasonKey, name = r.ReasonName, usuallyAccepted = r.UsuallyAccepted })
                    .ToListAsync(),
                outcomes = await _db.ClaimOutcomes.AsNoTracking()
                    .Select(o => new { id = o.OutcomeId, key = o.OutcomeKey, name = o.OutcomeName })
                    .ToListAsync(),
                suppliers = await _db.Parties.AsNoTracking()
                    .Where(p => (p.User.RoleId == 6 || p.User.RoleId == 7) && p.User.IsActive)
                    .OrderBy(p => (p.DisplayName ?? p.LegalName))
                    .Select(p => new { id = p.UserId, code = p.PartyCode, name = (p.DisplayName ?? p.LegalName) })
                    .ToListAsync(),
                customers = await _db.Parties.AsNoTracking()
                    .Where(p => (p.User.RoleId == 5 || p.User.RoleId == 7) && p.User.IsActive)
                    .OrderBy(p => (p.DisplayName ?? p.LegalName))
                    .Select(p => new { id = p.UserId, code = p.PartyCode, name = (p.DisplayName ?? p.LegalName) })
                    .ToListAsync(),
                products = await _db.Products.AsNoTracking()
                    .Where(p => p.IsActive).OrderBy(p => p.ProductName)
                    .Select(p => new { id = p.ProductId, sku = p.Sku, name = p.ProductName, imageUrl = p.ImageUrl, costPrice = p.CostPrice })
                    .ToListAsync(),

                /* The chase-and-write-off policy the claim screens quote back at
                   the user. It lives in "AppSetting" under the "claim" group and
                   is edited at /admin/settings, but that endpoint is
                   SuperAdmin-only while claims are worked by the order desk and
                   accounts too -- so the same rows are served here, read-only,
                   to whoever may see a claim at all. */
                policy = await ClaimPolicy()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load claim lookups");
        }
    }

    /// <summary>
    /// The "claim" group out of AppSetting, shaped for the screens. Values are
    /// stored as text, so each one is parsed with the same default the seed
    /// carries -- a missing or mistyped row degrades to a sensible number
    /// rather than throwing on a page that is only quoting a reminder period.
    /// </summary>
    private async Task<object> ClaimPolicy()
    {
        var rows = await _db.AppSettings.AsNoTracking()
            .Where(s => s.SettingGroup == "claim")
            .ToDictionaryAsync(s => s.SettingKey, s => s.SettingValue);

        int num(string key, int fallback) =>
            rows.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

        return new
        {
            windowDays = num("claim.windowDays", 180),
            remindSupplierAfterDays = num("claim.remindSupplierAfterDays", 14),
            remindEveryHours = num("claim.remindEveryHours", 48),
            remindUnsentAfterDays = num("claim.remindUnsentAfterDays", 3),
            replaceUpfront = rows.TryGetValue("claim.replaceUpfront", out var r) &&
                             (r.Equals("true", StringComparison.OrdinalIgnoreCase) || r == "1"),
            writeOffAccount = rows.GetValueOrDefault("claim.writeOffAccount", "Warranty & Claims")
        };
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record CreateClaimRequest(
        int CustomerUserId, int ProductId, int Quantity, int ReasonId, int OutcomeId,
        string? OriginalOrderNo, string? Note);

    public record SendClaimRequest(int? SupplierId, DateOnly? SentOn, string? Note);

    public record SettleClaimRequest(string StageKey, DateOnly? SettledOn, string? Note);

    public record RemindClaimRequest(string? Note);
}
