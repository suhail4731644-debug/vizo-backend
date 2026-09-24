using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// Customers and suppliers -- the /parties screens.
///
/// A party is NOT a login. "Party" carries the trading record and shares its
/// primary key with "User", which is why creating one writes both rows inside a
/// single transaction: the User row with RequiresEmail = false (so the
/// ck_user_email_required check lets the e-mail be null) and the Party row that
/// hangs off it.
///
/// Controller-only by design: no DTO classes, no services, no interfaces, no
/// repositories. Request bodies bind to the records at the foot of the file.
/// Every action is wrapped in try/catch and reports through Fail().
/// </summary>
[Route("api/parties")]
[ApiController]
[Authorize(Policy = "Staff")]
public class PartiesController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public PartiesController(AppDbContext db, IConfiguration cfg,
        ILogger<PartiesController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    /* TRAP: Party.SalesPersonUserId is a foreign key to "Employee", NOT to
       "User" -- so the navigation is an Employee and the name lives one hop
       further on at .SalesPersonUser.User.FullName. Reading .FullName straight
       off the navigation does not compile, which is the good outcome; the bad
       one is assuming it is a User id when filtering.

       Role ids: 5 = customer, 6 = supplier, 7 = customer & supplier.
       Kept as named constants rather than magic numbers scattered through the
       queries -- they come from the "Role" seed and never change. */
    private const int RoleCustomer = 5;
    private const int RoleSupplier = 6;
    private const int RoleBoth = 7;

    /// <summary>
    /// The user id a salesperson's party list must be narrowed to, or null when
    /// the caller is entitled to the whole book.
    ///
    /// About the ROLE, not a permission, for the same reason
    /// SalesController.SalesScopeUserId is: granting Sales the right to manage
    /// customers lets them open and edit accounts, it does not make them the
    /// back office. Accounts, the order desk, the warehouse and the owner all
    /// see everything -- you cannot chase a receivable through a keyhole.
    /// </summary>
    private int? MyPartiesOnly() =>
        CurrentRole() == Services.OrderWorkflow.RoleSales ? CurrentUserId() : null;

    // ══════════════════════════════════════════════════════════════════
    //  LIST
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The party list. `type` is customer | supplier | all -- "customer" also
    /// returns the customer-and-supplier rows, because a shop that also supplies
    /// us is still a customer and must not vanish from the customer screen.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetParties(
        [FromQuery] string? type, [FromQuery] string? q,
        [FromQuery] bool includeInactive = true,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.Parties.AsNoTracking().AsQueryable();

            /* A REP ADMINISTERS THEIR OWN ACCOUNTS, AND SELLS TO ANYBODY.

               Two different questions, and conflating them is how this ends up
               either useless or wrong:

                 THIS list is the Customers screen -- the place a rep goes to
                 edit a phone number or read a statement. It is theirs: the
                 accounts they opened, plus any the owner has since assigned to
                 them.

                 The PICKER on the order form is a different list, served by
                 GET /sales/lookups, and it is deliberately NOT filtered. The
                 brief is explicit -- "issued still he can see all customers" --
                 because a rep covering for a colleague still has to be able to
                 take the order.

               Everyone else sees the whole book. */
            if (MyPartiesOnly() is int me)
                rows = rows.Where(p => p.CreatedByUserId == me || p.SalesPersonUserId == me);

            rows = type?.ToLowerInvariant() switch
            {
                "customer" => rows.Where(p => p.User.RoleId == RoleCustomer || p.User.RoleId == RoleBoth),
                "supplier" => rows.Where(p => p.User.RoleId == RoleSupplier || p.User.RoleId == RoleBoth),
                _ => rows
            };

            if (!includeInactive)
                rows = rows.Where(p => p.User.IsActive);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(p =>
                    (p.DisplayName ?? p.LegalName).ToLower().Contains(term) ||
                    p.PartyCode.ToLower().Contains(term) ||
                    (p.DisplayName != null && p.DisplayName.ToLower().Contains(term)) ||
                    (p.User.Phone != null && p.User.Phone.Contains(term)));
            }

            var total = await rows.CountAsync();

            var items = await rows
                .OrderBy(p => (p.DisplayName ?? p.LegalName))
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(p => new
                {
                    id = p.UserId,
                    partyCode = p.PartyCode,
                    type = p.User.RoleId == RoleSupplier ? "SUPPLIER"
                         : p.User.RoleId == RoleBoth ? "BOTH" : "CUSTOMER",
                    legalName = p.LegalName,
                    displayName = p.DisplayName ?? (p.DisplayName ?? p.LegalName),
                    initials = "",
                    phone = p.User.Phone,
                    email = p.User.Email,
                    city = p.City.CityName,
                    province = p.City.Province.ProvinceName,
                    category = p.Category.CategoryKey,
                    categoryName = p.Category.CategoryName,
                    ntn = p.Ntn,
                    strn = p.Strn,
                    creditLimit = p.CreditLimit,
                    creditDays = p.CreditDays,
                    creditHoldPolicy = p.HoldPolicy.PolicyKey,
                    salesPerson = p.SalesPersonUser != null ? p.SalesPersonUser.User.FullName : null,
                    isActive = p.User.IsActive,
                    createdAt = p.User.CreatedAt,
                    rating = p.Rating.ToString(),

                    /* Receivable and payable are derived, never stored: the
                       posted ledger is the only place a balance is true. */
                    currentBalance = p.OpeningBalance + _db.JournalEntryLines
                        .Where(l => l.PartyUserId == p.UserId && l.Entry.StatusId == 2)
                        .Sum(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m,
                    payableBalance = _db.JournalEntryLines
                        .Where(l => l.PartyUserId == p.UserId && l.Entry.StatusId == 2)
                        .Sum(l => (decimal?)(l.CreditAmount - l.DebitAmount)) ?? 0m,

                    /* "When did we last do business with them" -- three separate
                       questions, and the party screens ask all three:
                         lastPurchaseAt -- they last bought from us
                         lastSupplyAt   -- they last supplied us
                         lastPaymentAt  -- they last actually paid
                       Each is a max() over the relevant document, not a stored
                       column, so it can never drift out of date. */
                    lastPurchaseAt = p.SalesInvoices
                        .Max(i => (DateOnly?)i.InvoiceDate),
                    lastSupplyAt = p.PurchaseInvoices
                        .Max(i => (DateOnly?)i.InvoiceDate),
                    lastPaymentAt = p.Collections
                        .Where(c => c.Status.StatusKey == "CONFIRMED")
                        .Max(c => (DateOnly?)c.CollectedOn)
                })
                .ToListAsync();

            /* Initials are a display concern, computed here so every screen
               shows the same two letters without each one reimplementing it. */
            var shaped = items.Select(p => new
            {
                p.id, p.partyCode, p.type, p.legalName, p.displayName,
                initials = Initials(p.displayName),
                p.phone, p.email, p.city, p.province, p.category, p.categoryName,
                p.ntn, p.strn, p.creditLimit, p.creditDays, p.creditHoldPolicy,
                p.salesPerson, p.isActive, p.createdAt, p.rating,
                p.currentBalance, p.payableBalance,
                p.lastPurchaseAt, p.lastSupplyAt, p.lastPaymentAt
            });

            return Ok(new { total, page, pageSize, items = shaped });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the party list");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ONE PARTY
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetParty(int id)
    {
        try
        {
            var p = await _db.Parties.AsNoTracking()
                .Where(x => x.UserId == id)
                .Select(x => new
                {
                    id = x.UserId,
                    partyCode = x.PartyCode,
                    type = x.User.RoleId == RoleSupplier ? "SUPPLIER"
                         : x.User.RoleId == RoleBoth ? "BOTH" : "CUSTOMER",
                    legalName = x.LegalName,
                    displayName = x.DisplayName ?? (x.DisplayName ?? x.LegalName),
                    phone = x.User.Phone,
                    altPhone = x.AltPhone,
                    email = x.User.Email,
                    cityId = x.CityId,
                    city = x.City.CityName,
                    province = x.City.Province.ProvinceName,
                    /* "PK" or "CN". Which of the two sets of tax numbers this
                       party actually has -- read from its own city, the same
                       route CheckTax takes on the way in. The detail screen
                       labels the three fields from it. */
                    country = x.City.Province.Country.Trim(),
                    addressLine = x.AddressLine,
                    categoryId = x.CategoryId,
                    category = x.Category.CategoryKey,
                    categoryName = x.Category.CategoryName,
                    industry = x.Industry,
                    ntn = x.Ntn,
                    strn = x.Strn,
                    cnic = x.Cnic,
                    creditLimit = x.CreditLimit,
                    creditDays = x.CreditDays,
                    holdPolicyId = x.HoldPolicyId,
                    creditHoldPolicy = x.HoldPolicy.PolicyKey,
                    openingBalance = x.OpeningBalance,
                    salesPersonUserId = x.SalesPersonUserId,
                    salesPerson = x.SalesPersonUser != null ? x.SalesPersonUser.User.FullName : null,
                    defaultLocationId = x.DefaultLocationId,
                    rating = x.Rating.ToString(),
                    notes = x.Notes,
                    /* The documents, and the one PDF they are bound into.
                       Null everywhere for an account opened before 22
                       September, which is most of them. */
                    documents = new
                    {
                        cnicFront = x.CnicFrontUrl,
                        cnicBack = x.CnicBackUrl,
                        cardFront = x.CardFrontUrl,
                        cardBack = x.CardBackUrl,
                        affidavitFront = x.AffidavitFrontUrl,
                        affidavitBack = x.AffidavitBackUrl,
                        pdfUrl = x.LegalDocsPdfUrl
                    },
                    isActive = x.User.IsActive,
                    createdAt = x.User.CreatedAt,
                    orderCount = x.SalesOrders.Count,
                    invoiceCount = x.SalesInvoices.Count
                })
                .FirstOrDefaultAsync();

            if (p is null) return NotFound(new { message = $"No party with id {id}." });

            var balance = p.openingBalance + await _db.JournalEntryLines
                .Where(l => l.PartyUserId == id && l.Entry.StatusId == 2)
                .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;

            return Ok(new
            {
                p.id, p.partyCode, p.type, p.legalName, p.displayName,
                initials = Initials(p.displayName),
                p.phone, p.altPhone, p.email, p.cityId, p.city, p.province, p.country, p.addressLine,
                p.categoryId, p.category, p.categoryName, p.industry,
                p.ntn, p.strn, p.cnic,
                p.creditLimit, p.creditDays, p.holdPolicyId, p.creditHoldPolicy,
                p.openingBalance, p.salesPersonUserId, p.salesPerson, p.defaultLocationId,
                p.rating, p.notes, p.isActive, p.createdAt,
                /* Projected above but never handed on until now, so the customer screen's
                   "Legal documents" button and the edit screen's pictures had nothing to
                   show. */
                p.documents,
                p.orderCount, p.invoiceCount,
                currentBalance = balance
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load party {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  STATEMENT
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The customer statement: every posted ledger line against this party,
    /// oldest first, with a running balance. Only POSTED entries (StatusId 2)
    /// count -- a draft entry has not happened yet.
    /// </summary>
    [HttpGet("{id:int}/statement")]
    public async Task<IActionResult> GetStatement(int id,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var party = await _db.Parties.AsNoTracking()
                .Where(p => p.UserId == id)
                .Select(p => new
                {
                    p.UserId, p.PartyCode, p.LegalName,
                    Display = p.DisplayName ?? p.LegalName,
                    p.OpeningBalance, p.CreditLimit, p.CreditDays,
                    Phone = p.User.Phone, City = p.City.CityName
                })
                .FirstOrDefaultAsync();

            if (party is null) return NotFound(new { message = $"No party with id {id}." });

            var q = _db.JournalEntryLines.AsNoTracking()
                .Where(l => l.PartyUserId == id && l.Entry.StatusId == 2);

            if (from is not null) q = q.Where(l => l.Entry.EntryDate >= from);
            if (to is not null) q = q.Where(l => l.Entry.EntryDate <= to);

            var lines = await q
                .OrderBy(l => l.Entry.EntryDate).ThenBy(l => l.LineId)
                .Select(l => new
                {
                    id = l.LineId,
                    date = l.Entry.EntryDate,
                    entryNo = l.Entry.EntryNo,
                    entryType = l.Entry.EntryType.TypeName,
                    reference = l.Entry.ReferenceNo,
                    narration = l.Description ?? l.Entry.Narration,
                    debit = l.DebitAmount,
                    credit = l.CreditAmount
                })
                .ToListAsync();

            /* Running balance is computed after the fetch: SQL window functions
               are not worth the round trip for a statement this size, and doing
               it here keeps the opening balance in one place. */
            var running = party.OpeningBalance;
            var rows = lines.Select(l =>
            {
                running += l.debit - l.credit;
                return new
                {
                    l.id, l.date, l.entryNo, l.entryType, l.reference, l.narration,
                    l.debit, l.credit, balance = running
                };
            }).ToList();

            return Ok(new
            {
                party = new
                {
                    id = party.UserId,
                    partyCode = party.PartyCode,
                    name = party.Display,
                    initials = Initials(party.Display),
                    phone = party.Phone,
                    city = party.City,
                    creditLimit = party.CreditLimit,
                    creditDays = party.CreditDays
                },
                openingBalance = party.OpeningBalance,
                closingBalance = running,
                totalDebit = rows.Sum(r => r.debit),
                totalCredit = rows.Sum(r => r.credit),
                lines = rows,

                /* Letterhead for the printed statement. It lives in AppSetting
                   under the "company" group and is edited at /admin/settings,
                   but that endpoint is SuperAdmin-only while a statement is
                   printed by sales and accounts too -- so the same rows are
                   served here, read-only, with the statement they belong to.
                   The frontend previously imported a hard-coded `company`
                   object, so a change made at /admin/settings never reached
                   the paper a customer actually receives. */
                company = await CompanyHeader()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load the statement for party {id}");
        }
    }

    /// <summary>
    /// Letterhead for the printed statement, off the single "Company" row.
    ///
    /// It is NOT in AppSetting -- company details have their own table, which
    /// /admin/company serves. That endpoint is SuperAdmin-only while a
    /// statement gets printed by sales and accounts too, so the same row is
    /// read here. Returns nulls rather than throwing if the table is empty.
    /// </summary>
    private async Task<object?> CompanyHeader()
    {
        return await _db.Companies.AsNoTracking()
            .Select(c => new
            {
                name = c.CompanyName,
                legalName = c.LegalName,
                ntn = c.Ntn,
                strn = c.Strn,
                email = c.Email,
                phone = c.Phone,
                city = c.City.CityName,
                country = c.Country,
                addressLine = c.AddressLine,
                currencySymbol = c.CurrencySymbol
            })
            .FirstOrDefaultAsync();
    }

    // ══════════════════════════════════════════════════════════════════
    //  VISITS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("visits")]
    public async Task<IActionResult> GetVisits([FromQuery] int take = 100)
    {
        try
        {
            if (take is < 1 or > 500) take = 100;

            var visits = await _db.CustomerVisits.AsNoTracking()
                .OrderByDescending(v => v.VisitedAt)
                .Take(take)
                .Select(v => new
                {
                    id = v.VisitId,
                    customerId = v.CustomerUserId,
                    customerName = (v.CustomerUser.DisplayName ?? v.CustomerUser.LegalName),
                    visitedAt = v.VisitedAt,
                    salesPerson = v.SalesPersonUser.User.FullName,
                    outcome = v.Outcome.OutcomeKey,
                    outcomeName = v.Outcome.OutcomeName,
                    note = v.Notes
                })
                .ToListAsync();

            return Ok(visits.Select(v => new
            {
                v.id, v.customerId, v.customerName,
                customerInitials = Initials(v.customerName),
                v.visitedAt, v.salesPerson, v.outcome, v.outcomeName, v.note
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load customer visits");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Everything the party form's dropdowns need, in one round trip.</summary>
    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            var everyKind = CurrentRole() == Services.OrderWorkflow.RoleAdmin;

            return Ok(new
            {
                /* WHAT A REP MAY OPEN AN ACCOUNT AS.

                   Sales and accounts see Retailer, Wholesaler and Agent --
                   the three kinds of customer this business actually sells to,
                   and the owner's instruction. Distributor and Manufacturer
                   stay for the Super Admin, who opens the rare account that is
                   neither. Filtered here rather than in the screen so the list
                   cannot be widened by editing the browser. */
                categories = await _db.PartyCategories.AsNoTracking()
                    /* `everyKind` is read into a local first: a method call on
                       the controller inside a Where is not something EF can
                       turn into SQL, and it fails at run time rather than at
                       build time. */
                    .Where(c => everyKind
                             || c.CategoryKey == "RETAILER"
                             || c.CategoryKey == "WHOLESALER"
                             || c.CategoryKey == "AGENT")
                    .OrderBy(c => c.CategoryId)
                    .Select(c => new { id = c.CategoryId, key = c.CategoryKey, name = c.CategoryName })
                    .ToListAsync(),
                cities = await _db.Cities.AsNoTracking()
                    .OrderBy(c => c.CityName)
                    .Select(c => new
                    {
                        id = c.CityId,
                        name = c.CityName,
                        province = c.Province.ProvinceName,
                        /* "PK" or "CN". The new-party form uses this to show the
                           right cities once the admin has said where the
                           supplier is, and the same value decides which set of
                           tax numbers is asked for. */
                        country = c.Province.Country.Trim()
                    })
                    .ToListAsync(),
                holdPolicies = await _db.CreditHoldPolicies.AsNoTracking()
                    .OrderBy(h => h.PolicyId)
                    .Select(h => new { id = h.PolicyId, key = h.PolicyKey, name = h.PolicyName })
                    .ToListAsync(),
                locations = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive).OrderBy(l => l.LocationName)
                    .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName })
                    .ToListAsync(),
                salesPeople = await _db.Users.AsNoTracking()
                    .Where(u => u.Role.RoleKey == "sales" && u.IsActive)
                    .OrderBy(u => u.FullName)
                    .Select(u => new { id = u.UserId, name = u.FullName })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load party lookups");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CREATE / UPDATE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates the User row and the Party row together. Both or neither --
    /// a Party with no User is unreachable and a User with no Party is a login
    /// that owns nothing, so this runs inside an explicit transaction.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "Staff")]
    public async Task<IActionResult> CreateParty([FromBody] PartyRequest body)
    {
        try
        {
            var roleId = RoleFor(body.Type);

            /* The form does not have to invent a party code. If it arrives blank
               we allocate the next one in the VZ-C-#### / VZ-S-#### / VZ-B-####
               series, matching the codes already in the database. Doing it here
               rather than in the browser is what stops two people opening an
               account at the same time and picking the same number. */
            if (string.IsNullOrWhiteSpace(body.PartyCode))
            {
                var prefix = roleId switch
                {
                    RoleSupplier => "VZ-S-",
                    RoleBoth => "VZ-B-",
                    _ => "VZ-C-"
                };

                var used = await _db.Parties
                    .Where(p => p.PartyCode.StartsWith(prefix))
                    .Select(p => p.PartyCode)
                    .ToListAsync();

                var next = used
                    .Select(c => int.TryParse(c[prefix.Length..], out var n) ? n : 0)
                    .DefaultIfEmpty(0)
                    .Max() + 1;

                body = body with { PartyCode = $"{prefix}{next:0000}" };
            }

            var error = await ValidateParty(body, null);
            if (error is not null) return BadRequest(new { message = error });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var user = new User
            {
                RoleId = roleId,
                RequiresEmail = false,          // parties never sign in
                FullName = body.LegalName.Trim(),
                Email = string.IsNullOrWhiteSpace(body.Email) ? null : body.Email.Trim(),
                Phone = string.IsNullOrWhiteSpace(body.Phone) ? null : body.Phone.Trim(),
                PasswordHash = null,
                PrimaryLocationId = body.DefaultLocationId,
                IsActive = body.IsActive,
                CreatedAt = Today()
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();       // need the generated UserId

            _db.Parties.Add(new Party
            {
                UserId = user.UserId,
                PartyCode = body.PartyCode!.Trim().ToUpperInvariant(),
                LegalName = body.LegalName.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim(),
                CategoryId = body.CategoryId,
                CityId = body.CityId,
                AddressLine = body.AddressLine,
                AltPhone = body.AltPhone,
                Industry = body.Industry,
                Ntn = body.Ntn,
                Strn = body.Strn,
                Cnic = body.Cnic,
                /* Whatever was photographed on the way in. Blank when the
                   salesperson said the documents were not available. */
                CnicFrontUrl = Clean(body.CnicFrontUrl),
                CnicBackUrl = Clean(body.CnicBackUrl),
                CardFrontUrl = Clean(body.CardFrontUrl),
                CardBackUrl = Clean(body.CardBackUrl),
                AffidavitFrontUrl = Clean(body.AffidavitFrontUrl),
                AffidavitBackUrl = Clean(body.AffidavitBackUrl),
                CreditLimit = body.CreditLimit,
                CreditDays = body.CreditDays,
                HoldPolicyId = body.HoldPolicyId,
                OpeningBalance = body.OpeningBalance,
                SalesPersonUserId = body.SalesPersonUserId,
                DefaultLocationId = body.DefaultLocationId,
                Rating = string.IsNullOrWhiteSpace(body.Rating) ? 'C' : body.Rating.Trim()[0],
                Notes = body.Notes,
                /* Written once, here, and never touched again -- UpdateParty
                   does not carry it. A rep's customer list is built on this,
                   and a fact that can be edited is not a fact you can build a
                   list on. See Models/Party.Custom.cs. */
                CreatedByUserId = CurrentUserId()
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("PARTY_CREATED", "Party", body.PartyCode,
                $"{body.LegalName} ({body.Type})", 1);

            /* Opening an account is the owner's business -- it is where a
               credit limit starts. Named, with the account on the line and a
               link straight to it. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.PartyAdded,
                $"Account opened by {CurrentUserName()}",
                $"{body.LegalName} ({body.PartyCode}) was added as a {body.Type?.ToLowerInvariant() ?? "customer"}.",
                url: $"/parties/{user.UserId}",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id = user.UserId,
                partyCode = body.PartyCode,
                message = $"{body.LegalName} saved as {body.PartyCode}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "create the party");
        }
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "Staff")]
    public async Task<IActionResult> UpdateParty(int id, [FromBody] PartyRequest body)
    {
        try
        {
            var party = await _db.Parties.FirstOrDefaultAsync(p => p.UserId == id);
            if (party is null) return NotFound(new { message = $"No party with id {id}." });

            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return NotFound(new { message = $"No user row behind party {id}." });

            var error = await ValidateParty(body, id);
            if (error is not null) return BadRequest(new { message = error });

            user.RoleId = RoleFor(body.Type);
            user.FullName = body.LegalName.Trim();
            user.Email = string.IsNullOrWhiteSpace(body.Email) ? null : body.Email.Trim();
            user.Phone = string.IsNullOrWhiteSpace(body.Phone) ? null : body.Phone.Trim();
            user.PrimaryLocationId = body.DefaultLocationId;
            user.IsActive = body.IsActive;

            party.PartyCode = (body.PartyCode ?? party.PartyCode).Trim().ToUpperInvariant();
            party.LegalName = body.LegalName.Trim();
            party.DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim();
            party.CategoryId = body.CategoryId;
            party.CityId = body.CityId;
            party.AddressLine = body.AddressLine;
            party.AltPhone = body.AltPhone;
            party.Industry = body.Industry;
            party.Ntn = body.Ntn;
            party.Strn = body.Strn;
            party.Cnic = body.Cnic;

            /* A PICTURE SENT HERE REPLACES THE ONE ON FILE; ONE LEFT OUT
               CHANGES NOTHING.

               The edit screen only sends a photograph when somebody has just
               taken one. If it sent nulls for the rest, opening a customer and
               pressing Save would quietly wipe their documents -- and nothing
               on that screen would have suggested it was about to.

               Nothing is re-read on an edit either: the owner's rule is that
               the details already in the database win, and a later photograph
               is filed, not obeyed. */
            party.CnicFrontUrl = Clean(body.CnicFrontUrl) ?? party.CnicFrontUrl;
            party.CnicBackUrl = Clean(body.CnicBackUrl) ?? party.CnicBackUrl;
            party.CardFrontUrl = Clean(body.CardFrontUrl) ?? party.CardFrontUrl;
            party.CardBackUrl = Clean(body.CardBackUrl) ?? party.CardBackUrl;
            party.AffidavitFrontUrl = Clean(body.AffidavitFrontUrl) ?? party.AffidavitFrontUrl;
            party.AffidavitBackUrl = Clean(body.AffidavitBackUrl) ?? party.AffidavitBackUrl;

            party.CreditLimit = body.CreditLimit;
            party.CreditDays = body.CreditDays;
            party.HoldPolicyId = body.HoldPolicyId;
            party.OpeningBalance = body.OpeningBalance;
            party.SalesPersonUserId = body.SalesPersonUserId;
            party.DefaultLocationId = body.DefaultLocationId;
            party.Rating = string.IsNullOrWhiteSpace(body.Rating) ? 'C' : body.Rating.Trim()[0];
            party.Notes = body.Notes;

            await _db.SaveChangesAsync();
            await Log("PARTY_UPDATED", "Party", party.PartyCode, body.LegalName, 1);

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.PartyChanged,
                $"Account edited by {CurrentUserName()}",
                $"{body.LegalName} ({party.PartyCode}) was changed.",
                url: $"/parties/{id}",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{body.LegalName} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"save party {id}");
        }
    }

    [HttpPatch("{id:int}/active")]
    [Authorize(Policy = "Staff")]
    public async Task<IActionResult> SetActive(int id, [FromBody] ActiveRequest body)
    {
        try
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return NotFound(new { message = $"No party with id {id}." });

            user.IsActive = body.Value;
            await _db.SaveChangesAsync();
            await Log(body.Value ? "PARTY_ACTIVATED" : "PARTY_DEACTIVATED",
                "Party", id.ToString(), user.FullName, 2);

            /* Switching an account off stops it trading. Worth interrupting the
               owner for, which is why this one is flagged severe. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.PartyChanged,
                $"Account {(body.Value ? "reopened" : "closed")} by {CurrentUserName()}",
                $"{user.FullName} was {(body.Value ? "switched back on" : "switched off and can no longer trade")}.",
                url: $"/parties/{id}",
                severe: !body.Value,
                exceptUserId: CurrentUserId());

            return Ok(new { id, isActive = body.Value });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"change the status of party {id}");
        }
    }

    // ════════════════════════ validation helpers ════════════════════════

    private static int RoleFor(string? type) => type?.ToUpperInvariant() switch
    {
        "SUPPLIER" => RoleSupplier,
        "BOTH" => RoleBoth,
        _ => RoleCustomer
    };

    /* ───────────────────── THE TWO SETS OF TAX NUMBERS ─────────────────────

       A Pakistani party has an NTN, an STRN and a CNIC. A Chinese one has none
       of those: it has a Unified Social Credit Code, a VAT registration and a
       Resident ID card, and all three look nothing like ours.

       Which set applies is NOT asked for on the request. It is read from the
       party's own city, through its province -- see 17_party_country.sql. A
       supplier in Guangdong is Chinese; there is no second field to disagree
       with that, and no way to send a Karachi address flagged as Chinese.

       The three database columns are shared. An 18-character Social Credit Code
       lives in "Ntn" and the screen labels it USCC. Renaming the columns would
       mean touching every report and export that reads them, to gain nothing a
       label does not already give. */

    private static readonly System.Text.RegularExpressions.Regex PkNtn =
        new(@"^\d{7}-\d$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PkStrn =
        new(@"^\d{2}-\d{2}-\d{4}-\d{3}-\d{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PkCnic =
        new(@"^\d{5}-\d{7}-\d$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /* The Social Credit Code and the VAT number are drawn from a restricted
       alphabet: digits plus the capitals EXCEPT I, O, S, V and Z, which were
       left out precisely because they are misread as 1, 0, 5, U and 2. */
    private const string CnAlphabet = "0-9A-HJ-NP-RTUW-Y";

    private static readonly System.Text.RegularExpressions.Regex CnUscc =
        new($"^[{CnAlphabet}]{{18}}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex CnVat =
        new($"^[{CnAlphabet}]{{15}}$|^[{CnAlphabet}]{{18}}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    /* Seventeen digits and a check character, which is a digit or an X. */
    private static readonly System.Text.RegularExpressions.Regex CnIdCard =
        new(@"^\d{17}[\dX]$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? CheckTax(string country, PartyRequest b)
    {
        string N(string? v) => (v ?? "").Trim();
        var ntn = N(b.Ntn);
        var strn = N(b.Strn);
        var cnic = N(b.Cnic);

        if (country == "CN")
        {
            var uscc = ntn.ToUpperInvariant();
            var vat = strn.ToUpperInvariant();
            var id = cnic.ToUpperInvariant();

            if (uscc.Length > 0 && !CnUscc.IsMatch(uscc))
                return "The Unified Social Credit Code is 18 characters, digits and capitals "
                     + "(no I, O, S, V or Z) -- for example 91440300MA5EDK8T5H.";
            if (vat.Length > 0 && !CnVat.IsMatch(vat))
                return "The VAT taxpayer number is 15 or 18 characters, digits and capitals "
                     + "(no I, O, S, V or Z).";
            if (id.Length > 0 && !CnIdCard.IsMatch(id))
                return "A Resident ID card number is 17 digits and a check character "
                     + "(a digit or X) -- for example 440301199001011234.";
            return null;
        }

        if (ntn.Length > 0 && !PkNtn.IsMatch(ntn))
            return "NTN must look like 1234567-8.";
        if (strn.Length > 0 && !PkStrn.IsMatch(strn))
            return "STRN must look like 32-77-8901-234-56.";
        if (cnic.Length > 0 && !PkCnic.IsMatch(cnic))
            return "CNIC must look like 00000-0000000-0.";
        return null;
    }

    private async Task<string?> ValidateParty(PartyRequest b, int? existingId)
    {
        if (string.IsNullOrWhiteSpace((b.DisplayName ?? b.LegalName))) return "Legal name is required.";
        if (b.CreditLimit < 0) return "Credit limit cannot be negative.";
        if (b.CreditDays < 0 || b.CreditDays > 365) return "Credit days must be between 0 and 365.";

        var code = (b.PartyCode ?? "").Trim().ToUpperInvariant();
        var codeTaken = await _db.Parties
            .AnyAsync(p => p.PartyCode.ToUpper() == code && (existingId == null || p.UserId != existingId));
        if (codeTaken) return $"Party code {code} is already in use.";

        if (!string.IsNullOrWhiteSpace(b.Email))
        {
            var email = b.Email.Trim().ToLowerInvariant();
            var emailTaken = await _db.Users
                .AnyAsync(u => u.Email!.ToLower() == email && (existingId == null || u.UserId != existingId));
            if (emailTaken) return $"{b.Email} is already on another account.";
        }

        if (!await _db.PartyCategories.AnyAsync(c => c.CategoryId == b.CategoryId))
            return "Pick a valid category.";
        var country = await _db.Cities.AsNoTracking()
            .Where(c => c.CityId == b.CityId)
            .Select(c => c.Province.Country)
            .FirstOrDefaultAsync();
        if (country is null) return "Pick a valid city.";

        if (!await _db.CreditHoldPolicies.AnyAsync(h => h.PolicyId == b.HoldPolicyId))
            return "Pick a valid credit-hold policy.";

        /* Checked here as well as on the form, because a shape the browser
           happens to enforce is not a shape the database is protected by. */
        var badTax = CheckTax(country.Trim().ToUpperInvariant(), b);
        if (badTax is not null) return badTax;

        /* All three tax numbers are UNIQUE in the schema, and until now nothing
           said so before the insert -- a number already on another account came
           back as a raw 500 with a constraint name in it. The party code and the
           e-mail have always been checked properly; these three were simply
           missed. Same treatment, same wording. */
        foreach (var (value, what) in new[]
                 {
                     ((b.Ntn ?? "").Trim(),  country == "CN" ? "Social Credit Code" : "NTN"),
                     ((b.Strn ?? "").Trim(), country == "CN" ? "VAT number" : "STRN"),
                     ((b.Cnic ?? "").Trim(), country == "CN" ? "ID card number" : "CNIC"),
                 })
        {
            if (value.Length == 0) continue;

            var taken = what switch
            {
                "NTN" or "Social Credit Code" => await _db.Parties.AnyAsync(
                    p => p.Ntn == value && (existingId == null || p.UserId != existingId)),
                "STRN" or "VAT number" => await _db.Parties.AnyAsync(
                    p => p.Strn == value && (existingId == null || p.UserId != existingId)),
                _ => await _db.Parties.AnyAsync(
                    p => p.Cnic == value && (existingId == null || p.UserId != existingId)),
            };

            if (taken) return $"{what} {value} is already on another account.";
        }

        return null;
    }

    /// <summary>Trimmed, or null when there was nothing but space.</summary>
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ══════════════════════════ request bodies ══════════════════════════

    /* THE SIX PHOTOGRAPHS ARE OPTIONAL AND THEY ARE LINKS.

       The browser uploads each picture to Cloudinary as it is taken (through
       /api/upload/image) and sends the URLs here with the rest of the form, so
       a slow line never holds a six-photograph form hostage to one request.
       Every one of them may be null: a shopkeeper with no affidavit -- or with
       nothing at all -- is still a customer. See
       backend/database/22_customer_documents.sql. */
    public record PartyRequest(
        string? PartyCode, string LegalName, string? DisplayName, string Type,
        string? Email, string? Phone, string? AltPhone, string? AddressLine,
        int CategoryId, int CityId, string? Industry,
        string? Ntn, string? Strn, string? Cnic,
        decimal CreditLimit, int CreditDays, int HoldPolicyId, decimal OpeningBalance,
        int? SalesPersonUserId, int? DefaultLocationId, string? Rating, string? Notes,
        bool IsActive,
        string? CnicFrontUrl = null, string? CnicBackUrl = null,
        string? CardFrontUrl = null, string? CardBackUrl = null,
        string? AffidavitFrontUrl = null, string? AffidavitBackUrl = null);

    public record ActiveRequest(bool Value);

    // ══════════════════════════════════════════════════════════════════
    //  EXPORT
    // ══════════════════════════════════════════════════════════════════

    /*  The Export button used to be a toast, or nothing at all. It now returns
        a real .xlsx.

        The export runs the SAME list action the screen runs and writes its
        result, rather than re-querying -- so what lands in Excel is what was on
        the page, filters and all, and the two cannot drift.

        Money, dates and counts are written as typed cells rather than strings,
        so the columns sort and total in Excel instead of being text that merely
        looks like numbers.                                                     */

    /// <summary>Customers and suppliers on the current filter, as a spreadsheet.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportParties(
        [FromQuery] string? type, [FromQuery] string? q, [FromQuery] bool includeInactive = true)
    {
        try
        {
            var action = await GetParties(type, q, includeInactive, 1, 5000);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var columns = new[]
            {
                new XlsxWriter.Column("Code", "partyCode", XlsxWriter.CellKind.Text, 14),
                new XlsxWriter.Column("Legal Name", "legalName", XlsxWriter.CellKind.Text, 32),
                new XlsxWriter.Column("Trading As", "displayName", XlsxWriter.CellKind.Text, 26),
                new XlsxWriter.Column("Type", "type"),
                new XlsxWriter.Column("Category", "categoryName"),
                new XlsxWriter.Column("City", "city"),
                new XlsxWriter.Column("Province", "province"),
                new XlsxWriter.Column("Phone", "phone", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Email", "email", XlsxWriter.CellKind.Text, 28),
                new XlsxWriter.Column("NTN", "ntn", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("STRN", "strn", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("Credit Limit", "creditLimit", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Credit Days", "creditDays", XlsxWriter.CellKind.Integer, 12),
                new XlsxWriter.Column("Hold Policy", "creditHoldPolicy"),
                new XlsxWriter.Column("Receivable", "currentBalance", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Payable", "payableBalance", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Sales Rep", "salesPerson", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("Rating", "rating", XlsxWriter.CellKind.Text, 8),
                new XlsxWriter.Column("Active", "isActive", XlsxWriter.CellKind.Text, 8),
                new XlsxWriter.Column("Last Purchase", "lastPurchaseAt", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Last Payment", "lastPaymentAt", XlsxWriter.CellKind.Date),
            };

            var bytes = XlsxWriter.FromPayload("Parties",
                JsonSerializer.SerializeToElement(ok.Value, ExportJson), columns);
            return File(bytes, XlsxWriter.ContentType, $"parties-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "export the parties");
        }
    }

    /* The API writes anonymous objects with their own already-camelCase names;
       matching that here means the column Field values below are the same keys
       the browser sees. */
    private static readonly JsonSerializerOptions ExportJson = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

}
