using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /purchases screens: purchase orders, goods receipts and purchase
/// invoices.
///
/// PURCHASE RETURNS ARE GONE (this session) -- the owner's instruction was to
/// remove the scenario entirely while sales returns stay. Nothing here creates
/// or lists one any more. The nine that were made before this are left exactly
/// as they were: "PurchaseReturn" and "PurchaseReturnItem" still hold the rows,
/// ProductHistoryController still folds them into a product's ledger, and
/// DocumentsController can still print or share the PDF of one that already
/// exists. Only the ability to make a new one, and the screen to browse them by
/// name, are removed.
///
/// The inbound chain is PO -> GRN -> PI, and the three are deliberately
/// separate things people conflate:
///   * PO  -- what we asked for.
///   * GRN -- what actually arrived. STOCK RISES HERE, not at the invoice.
///   * PI  -- the bill. The payable rises here.
/// China's own commercial invoice is theirs; the PI is our record of the
/// payable and carries SupplierInvoiceNo as the reference back to it.
///
/// TRAP: on the purchase side CreatedByUser / ReceivedByUser / ApprovedByUser
/// are all EMPLOYEE navigations, not User -- so the name is one hop further on
/// at .CreatedByUser.User.FullName. The sales side is the opposite. Getting
/// this wrong does not compile, which is the good outcome.
///
/// Controller-only by design: no DTOs, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports via Fail().
/// </summary>
[Route("api/purchases")]
[ApiController]
/* NOT THE ORDER DESK'S. The owner: "order department cannot access any
   purchases page ... order department can never see purchases".

   By ROLE rather than by a permission, on purpose: "never" is a statement about
   the job, and a permission can be ticked in Setup. This is ANDed with the
   BackOffice policy above, so nothing that worked for the accountant or the
   Super Admin changes. The route guard in the web app's proxy.ts says the
   same thing. */
[Authorize(Roles = "super-admin,accountant")]
[Authorize(Policy = "BackOffice")]
public class PurchasesController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public PurchasesController(AppDbContext db, IConfiguration cfg,
        ILogger<PurchasesController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    // ══════════════════════════════════════════════════════════════════
    //  PURCHASE ORDERS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("orders")]
    public async Task<IActionResult> GetPurchaseOrders(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? supplierId)
    {
        try
        {
            var rows = _db.PurchaseOrders.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(p => p.Status.StatusKey == status);
            if (supplierId is not null) rows = rows.Where(p => p.SupplierUserId == supplierId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(p => p.PoNo.ToLower().Contains(term) ||
                                       (p.SupplierUser.DisplayName ?? p.SupplierUser.LegalName).ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(p => p.PoDate).ThenByDescending(p => p.PoId)
                .Select(p => new
                {
                    id = p.PoId,
                    poNo = p.PoNo,
                    supplierId = p.SupplierUserId,
                    supplierName = (p.SupplierUser.DisplayName ?? p.SupplierUser.LegalName),
                    location = p.Location.LocationName,
                    poDate = p.PoDate,
                    expectedDate = p.ExpectedDate,
                    status = p.Status.StatusKey,
                    statusName = p.Status.StatusName,
                    itemCount = p.PurchaseOrderItems.Count,
                    total = p.TotalAmount,
                    createdBy = p.CreatedByUser.User.FullName,
                    approvedBy = p.ApprovedByUser != null ? p.ApprovedByUser.User.FullName : null,
                    notes = p.Notes,

                    /* How much of what was ordered has actually landed. Driven by
                       the GRN lines, because the GRN is what moved stock. */
                    orderedUnits = p.PurchaseOrderItems.Sum(i => (int?)i.Quantity) ?? 0,
                    receivedUnits = p.GoodsReceipts
                        .SelectMany(g => g.GoodsReceiptItems)
                        .Sum(i => (int?)i.QtyReceived) ?? 0
                })
                .ToListAsync();

            return Ok(items.Select(p => new
            {
                p.id, p.poNo, p.supplierId, p.supplierName,
                supplierInitials = Initials(p.supplierName),
                p.location, p.poDate, p.expectedDate, p.status, p.statusName,
                p.itemCount, p.total, p.createdBy, p.approvedBy, p.notes,
                p.orderedUnits, p.receivedUnits,
                receivedPercent = p.orderedUnits == 0 ? 0
                    : (int)Math.Round(100.0 * p.receivedUnits / p.orderedUnits)
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the purchase-order list");
        }
    }

    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetPurchaseOrder(int id)
    {
        try
        {
            var p = await _db.PurchaseOrders.AsNoTracking()
                .Where(x => x.PoId == id)
                .Select(x => new
                {
                    id = x.PoId,
                    poNo = x.PoNo,
                    supplierId = x.SupplierUserId,
                    supplierName = (x.SupplierUser.DisplayName ?? x.SupplierUser.LegalName),
                    supplierCode = x.SupplierUser.PartyCode,
                    supplierPhone = x.SupplierUser.User.Phone,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    poDate = x.PoDate,
                    expectedDate = x.ExpectedDate,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    subtotal = x.Subtotal,
                    discount = x.DiscountAmount,
                    tax = x.TaxAmount,
                    total = x.TotalAmount,
                    notes = x.Notes,
                    createdBy = x.CreatedByUser.User.FullName,
                    approvedBy = x.ApprovedByUser != null ? x.ApprovedByUser.User.FullName : null,
                    lines = x.PurchaseOrderItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        id = i.PoItemId,
                        lineNo = i.LineNo,
                        productId = i.ProductId,
                        sku = i.Product.Sku,
                        imageUrl = i.Product.ImageUrl,
                        name = i.Product.ProductName,
                        packing = i.Product.Packing,
                        qty = i.Quantity,
                        unitCost = i.UnitCost,
                        taxPercent = i.TaxPercent,
                        lineTotal = i.LineTotal,

                        /* How much of THIS line has actually arrived, counted
                           off the posted receipts for this order. The detail
                           screen shows an "x% received" figure that the list
                           endpoint already returned but this one did not, so
                           the same order read 0% when opened. Only POSTED
                           receipts count -- a draft GRN is somebody still
                           typing, not stock on the shelf. */
                        received = x.GoodsReceipts
                            .Where(g => g.Status.StatusKey == "POSTED")
                            .SelectMany(g => g.GoodsReceiptItems)
                            .Where(gi => gi.ProductId == i.ProductId)
                            .Sum(gi => (int?)(gi.QtyReceived - gi.QtyDamaged)) ?? 0
                    }).ToList(),
                    receipts = x.GoodsReceipts.OrderByDescending(g => g.ReceiptDate).Select(g => new
                    {
                        id = g.GrnId,
                        grnNo = g.GrnNo,
                        receiptDate = g.ReceiptDate,
                        status = g.Status.StatusKey,
                        statusName = g.Status.StatusName,
                        receivedBy = g.ReceivedByUser != null ? g.ReceivedByUser.User.FullName : null,
                        unitsReceived = g.GoodsReceiptItems.Sum(gi => (int?)(gi.QtyReceived - gi.QtyDamaged)) ?? 0
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (p is null) return NotFound(new { message = $"No purchase order with id {id}." });

            return Ok(new
            {
                p.id, p.poNo, p.supplierId, p.supplierName,
                supplierInitials = Initials(p.supplierName),
                p.supplierCode, p.supplierPhone, p.locationId, p.location,
                p.poDate, p.expectedDate, p.status, p.statusName,
                p.subtotal, p.discount, p.tax, p.total, p.notes,
                p.createdBy, p.approvedBy, p.lines, p.receipts
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load purchase order {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  GOODS RECEIPTS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The two counts the supplier list shows above its table.
    ///
    /// They were hard-coded in the markup -- "Open POs = 8", "Pending GRNs =
    /// 2" -- on a page whose every other figure is real, which is the worst
    /// place to put a made-up number: nobody thinks to check it.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetPurchasesSummary()
    {
        try
        {
            /* Open = ordered and not yet fully received. DRAFT and
               PENDING_APPROVAL are not open orders, they are paperwork; and
               RECEIVED/CLOSED/CANCELLED are done. */
            var openPos = await _db.PurchaseOrders
                .CountAsync(o => o.Status.StatusKey == "APPROVED"
                              || o.Status.StatusKey == "PARTIALLY_RECEIVED");

            var openPoValue = await _db.PurchaseOrders
                .Where(o => o.Status.StatusKey == "APPROVED" || o.Status.StatusKey == "PARTIALLY_RECEIVED")
                .SumAsync(o => (decimal?)o.TotalAmount) ?? 0m;

            /* "Pending" means goods are in but the supplier's bill has not been
               entered against them -- the gap where money is owed and nobody
               has written it down. */
            var pendingGrns = await _db.GoodsReceipts
                .CountAsync(g => !_db.PurchaseInvoices.Any(i => i.PoId != null && i.PoId == g.PoId));

            var payablesRaw = await _db.PurchaseInvoices.AsNoTracking()
                .Where(i => i.Status.StatusKey != "CANCELLED")
                .Select(i => new
                {
                    total = i.TotalAmount,
                    paid = i.VoucherAllocations
                        .Where(a => a.Voucher.Status.StatusKey == "POSTED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m
                })
                .ToListAsync();

            return Ok(new
            {
                openPos,
                openPoValue,
                pendingGrns,
                payableTotal = payablesRaw.Sum(i => i.total - i.paid)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the purchases summary");
        }
    }

    [HttpGet("grns")]
    public async Task<IActionResult> GetGrns([FromQuery] string? q, [FromQuery] string? status)
    {
        try
        {
            var rows = _db.GoodsReceipts.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(g => g.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(g => g.GrnNo.ToLower().Contains(term) ||
                                       (g.SupplierUser.DisplayName ?? g.SupplierUser.LegalName).ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(g => g.ReceiptDate).ThenByDescending(g => g.GrnId)
                .Select(g => new
                {
                    id = g.GrnId,
                    grnNo = g.GrnNo,
                    poId = g.PoId,
                    poNo = g.Po != null ? g.Po.PoNo : null,
                    supplierId = g.SupplierUserId,
                    supplierName = (g.SupplierUser.DisplayName ?? g.SupplierUser.LegalName),
                    location = g.Location.LocationName,
                    receiptDate = g.ReceiptDate,
                    deliveryNoteNo = g.DeliveryNoteNo,
                    vehicleNo = g.VehicleNo,
                    totalValue = g.TotalValue,
                    status = g.Status.StatusKey,
                    statusName = g.Status.StatusName,
                    receivedBy = g.ReceivedByUser.User.FullName,
                    itemCount = g.GoodsReceiptItems.Count,
                    unitsReceived = g.GoodsReceiptItems.Sum(i => (int?)i.QtyReceived) ?? 0,
                    unitsDamaged = g.GoodsReceiptItems.Sum(i => (int?)i.QtyDamaged) ?? 0
                })
                .ToListAsync();

            var rowsOut = items.Select(g => new
            {
                g.id, g.grnNo, g.poId, g.poNo, g.supplierId, g.supplierName,
                supplierInitials = Initials(g.supplierName),
                g.location, g.receiptDate, g.deliveryNoteNo, g.vehicleNo,
                g.totalValue, g.status, g.statusName, g.receivedBy,
                g.itemCount, g.unitsReceived, g.unitsDamaged,
                unitsAccepted = g.unitsReceived - g.unitsDamaged
            }).ToList();

            /* The four figures the screen shows above the table. They used to
               be typed into the markup -- "GRNs This Week = 5", "Units
               Received = 1,000", "Damaged Units = 9" -- so the page looked
               live and lied in four places at once.

               Computed over the WHOLE filter, not the page being displayed:
               a summary that changes when you turn a page is not a summary. */
            var weekStart = Today().AddDays(-7);
            var poInTransit = await _db.PurchaseOrders
                .CountAsync(o => o.Status.StatusKey == "APPROVED"
                              || o.Status.StatusKey == "PARTIALLY_RECEIVED");

            var summary = new
            {
                grnsThisWeek = items.Count(g => g.receiptDate >= weekStart),
                poInTransit,
                unitsReceived = items.Sum(g => g.unitsReceived),
                unitsDamaged = items.Sum(g => g.unitsDamaged),
                totalValue = items.Sum(g => g.totalValue),
                count = items.Count
            };

            return Ok(new { summary, items = rowsOut });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the goods-receipt list");
        }
    }

    [HttpGet("grns/{id:int}")]
    public async Task<IActionResult> GetGrn(int id)
    {
        try
        {
            var g = await _db.GoodsReceipts.AsNoTracking()
                .Where(x => x.GrnId == id)
                .Select(x => new
                {
                    id = x.GrnId,
                    grnNo = x.GrnNo,
                    poId = x.PoId,
                    poNo = x.Po != null ? x.Po.PoNo : null,
                    supplierId = x.SupplierUserId,
                    supplierName = (x.SupplierUser.DisplayName ?? x.SupplierUser.LegalName),
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    receiptDate = x.ReceiptDate,
                    deliveryNoteNo = x.DeliveryNoteNo,
                    vehicleNo = x.VehicleNo,
                    totalValue = x.TotalValue,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    receivedBy = x.ReceivedByUser.User.FullName,
                    notes = x.Notes,
                    lines = x.GoodsReceiptItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        id = i.GrnItemId,
                        lineNo = i.LineNo,
                        productId = i.ProductId,
                        sku = i.Product.Sku,
                        imageUrl = i.Product.ImageUrl,
                        name = i.Product.ProductName,
                        qtyReceived = i.QtyReceived,
                        qtyDamaged = i.QtyDamaged,
                        qtyAccepted = i.QtyReceived - i.QtyDamaged,
                        unitCost = i.UnitCost,
                        batchNo = i.BatchNo,
                        expiryDate = i.ExpiryDate
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (g is null) return NotFound(new { message = $"No goods receipt with id {id}." });

            return Ok(new
            {
                g.id, g.grnNo, g.poId, g.poNo, g.supplierId, g.supplierName,
                supplierInitials = Initials(g.supplierName),
                g.locationId, g.location, g.receiptDate, g.deliveryNoteNo, g.vehicleNo,
                g.totalValue, g.status, g.statusName, g.receivedBy, g.notes, g.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load goods receipt {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  PURCHASE INVOICES
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("invoices")]
    public async Task<IActionResult> GetPurchaseInvoices([FromQuery] string? q, [FromQuery] string? status)
    {
        try
        {
            var rows = _db.PurchaseInvoices.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(i => i.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(i => i.InvoiceNo.ToLower().Contains(term) ||
                                       i.SupplierInvoiceNo.ToLower().Contains(term) ||
                                       (i.SupplierUser.DisplayName ?? i.SupplierUser.LegalName).ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.PiId)
                .Select(i => new
                {
                    id = i.PiId,
                    invoiceNo = i.InvoiceNo,
                    supplierInvoiceNo = i.SupplierInvoiceNo,
                    supplierId = i.SupplierUserId,
                    supplierName = (i.SupplierUser.DisplayName ?? i.SupplierUser.LegalName),
                    poId = i.PoId,
                    poNo = i.Po != null ? i.Po.PoNo : null,
                    invoiceDate = i.InvoiceDate,
                    dueDate = i.DueDate,
                    subtotal = i.Subtotal,
                    discount = i.DiscountAmount,
                    tax = i.TaxAmount,
                    whtAmount = i.WhtAmount,
                    total = i.TotalAmount,
                    status = i.Status.StatusKey,
                    statusName = i.Status.StatusName,
                    paymentMethod = i.Method.MethodKey,
                    createdBy = i.CreatedByUser.User.FullName,
                    paid = i.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m
                })
                .ToListAsync();

            return Ok(items.Select(i => new
            {
                i.id, i.invoiceNo, i.supplierInvoiceNo, i.supplierId, i.supplierName,
                supplierInitials = Initials(i.supplierName),
                i.poId, i.poNo, i.invoiceDate, i.dueDate,
                i.subtotal, i.discount, i.tax, i.whtAmount, i.total,
                i.status, i.statusName, i.paymentMethod, i.createdBy,
                i.paid, balance = i.total - i.paid,
                isOverdue = i.total - i.paid > 0 && i.dueDate < Today()
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the purchase-invoice list");
        }
    }

    [HttpGet("invoices/{id:int}")]
    public async Task<IActionResult> GetPurchaseInvoice(int id)
    {
        try
        {
            var i = await _db.PurchaseInvoices.AsNoTracking()
                .Where(x => x.PiId == id)
                .Select(x => new
                {
                    id = x.PiId,
                    invoiceNo = x.InvoiceNo,
                    supplierInvoiceNo = x.SupplierInvoiceNo,
                    supplierId = x.SupplierUserId,
                    supplierName = (x.SupplierUser.DisplayName ?? x.SupplierUser.LegalName),
                    supplierCode = x.SupplierUser.PartyCode,
                    poId = x.PoId,
                    poNo = x.Po != null ? x.Po.PoNo : null,
                    invoiceDate = x.InvoiceDate,
                    dueDate = x.DueDate,
                    subtotal = x.Subtotal,
                    discount = x.DiscountAmount,
                    tax = x.TaxAmount,
                    whtAmount = x.WhtAmount,
                    total = x.TotalAmount,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    paymentMethod = x.Method.MethodKey,
                    createdBy = x.CreatedByUser.User.FullName,
                    paid = x.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m,
                    lines = x.PurchaseInvoiceItems.OrderBy(l => l.LineNo).Select(l => new
                    {
                        id = l.PiItemId,
                        lineNo = l.LineNo,
                        productId = l.ProductId,
                        sku = l.Product.Sku,
                        imageUrl = l.Product.ImageUrl,
                        name = l.Product.ProductName,
                        qty = l.Quantity,
                        unitCost = l.UnitCost,
                        taxPercent = l.TaxPercent,
                        lineTotal = l.LineTotal
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (i is null) return NotFound(new { message = $"No purchase invoice with id {id}." });

            return Ok(new
            {
                i.id, i.invoiceNo, i.supplierInvoiceNo, i.supplierId, i.supplierName,
                supplierInitials = Initials(i.supplierName),
                i.supplierCode, i.poId, i.poNo, i.invoiceDate, i.dueDate,
                i.subtotal, i.discount, i.tax, i.whtAmount, i.total,
                i.status, i.statusName, i.paymentMethod, i.createdBy,
                i.paid, balance = i.total - i.paid, i.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load purchase invoice {id}");
        }
    }

    /// <summary>
    /// Supplier payables due inside `withinDays`, oldest first. Feeds the
    /// accountant's payables screen and the payment reminders.
    /// </summary>
    [HttpGet("payables")]
    public async Task<IActionResult> GetPayables([FromQuery] int withinDays = 30)
    {
        try
        {
            var cutoff = Today().AddDays(withinDays);

            var rows = await _db.PurchaseInvoices.AsNoTracking()
                .Where(i => i.DueDate <= cutoff)
                .Select(i => new
                {
                    id = i.PiId,
                    invoiceNo = i.InvoiceNo,
                    supplierInvoiceNo = i.SupplierInvoiceNo,
                    supplierId = i.SupplierUserId,
                    supplierName = (i.SupplierUser.DisplayName ?? i.SupplierUser.LegalName),
                    invoiceDate = i.InvoiceDate,
                    dueDate = i.DueDate,
                    total = i.TotalAmount,
                    paid = i.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m
                })
                .ToListAsync();

            var open = rows.Where(r => r.total - r.paid > 0)
                .OrderBy(r => r.dueDate)
                .Select(r => new
                {
                    r.id, r.invoiceNo, r.supplierInvoiceNo, r.supplierId, r.supplierName,
                    supplierInitials = Initials(r.supplierName),
                    r.invoiceDate, r.dueDate, r.total, r.paid,
                    balance = r.total - r.paid,
                    daysToDue = r.dueDate.DayNumber - Today().DayNumber,
                    isOverdue = r.dueDate < Today()
                })
                .ToList();

            return Ok(new
            {
                withinDays,
                count = open.Count,
                totalDue = open.Sum(o => o.balance),
                overdueCount = open.Count(o => o.isOverdue),
                overdueTotal = open.Where(o => o.isOverdue).Sum(o => o.balance),
                items = open
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load supplier payables");
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
                suppliers = await _db.Parties.AsNoTracking()
                    .Where(p => (p.User.RoleId == 6 || p.User.RoleId == 7) && p.User.IsActive)
                    .OrderBy(p => (p.DisplayName ?? p.LegalName))
                    .Select(p => new { id = p.UserId, code = p.PartyCode, name = (p.DisplayName ?? p.LegalName) })
                    .ToListAsync(),
                locations = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive).OrderBy(l => l.LocationName)
                    .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName })
                    .ToListAsync(),
                poStatuses = await _db.PurchaseOrderStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),
                postingStatuses = await _db.PostingStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),
                invoiceStatuses = await _db.InvoiceStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),
                paymentMethods = await _db.PaymentMethods.AsNoTracking()
                    .Where(m => m.IsActive)
                    .Select(m => new { id = m.MethodId, key = m.MethodKey, name = m.MethodName })
                    .ToListAsync(),
                products = await _db.Products.AsNoTracking()
                    .Where(p => p.IsActive).OrderBy(p => p.ProductName)
                    .Select(p => new
                    {
                        id = p.ProductId, sku = p.Sku, name = p.ProductName, imageUrl = p.ImageUrl,
                        costPrice = p.CostPrice, packing = p.Packing,
                        taxRatePercent = p.TaxRatePercent
                    })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load purchase lookups");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CREATE  --  PO, GRN, PI, PR
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Raises a purchase order. Line totals are recomputed here from qty, cost
    /// and tax; a total that arrives from the browser is a total anybody can
    /// edit.
    /// </summary>
    [HttpPost("orders")]
    public async Task<IActionResult> CreatePurchaseOrder([FromBody] PoRequest body)
    {
        try
        {
            var err = await ValidateLines(body.Lines, body.SupplierId, body.LocationId);
            if (err is not null) return BadRequest(new { message = err });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can raise a purchase order." });

            var status = await _db.PurchaseOrderStatuses
                .FirstOrDefaultAsync(s => s.StatusKey == (body.SubmitForApproval ? "PENDING_APPROVAL" : "DRAFT"));
            if (status is null) return BadRequest(new { message = "Purchase-order statuses are not configured." });

            decimal subtotal = 0, tax = 0;
            foreach (var l in body.Lines)
            {
                var net = l.Qty * l.UnitCost;
                subtotal += net;
                tax += net * (l.TaxPercent / 100m);
            }
            var total = subtotal - body.Discount + tax;

            await using var tx = await _db.Database.BeginTransactionAsync();

            var po = new PurchaseOrder
            {
                PoNo = await NextNumber("PO"),
                SupplierUserId = body.SupplierId,
                LocationId = body.LocationId,
                PoDate = body.PoDate ?? Today(),
                ExpectedDate = body.ExpectedDate,
                StatusId = status.StatusId,
                Subtotal = subtotal,
                DiscountAmount = body.Discount,
                TaxAmount = tax,
                TotalAmount = total,
                Notes = body.Notes,
                CreatedByUserId = me.Value,
                ApprovedByUserId = null
            };
            _db.PurchaseOrders.Add(po);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                var net = l.Qty * l.UnitCost;
                _db.PurchaseOrderItems.Add(new PurchaseOrderItem
                {
                    PoId = po.PoId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty,
                    UnitCost = l.UnitCost,
                    TaxPercent = l.TaxPercent,
                    LineTotal = net + net * (l.TaxPercent / 100m)
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("PO_CREATED", "PurchaseOrder", po.PoNo, $"{body.Lines.Count} lines, {total:N0}", 1);
            /* The PDF exists the moment the document does. Print and Download
               then hand out the stored Cloudinary file rather than rendering a
               fresh one, so what is on screen is what is in the store. A
               failure here is logged and swallowed -- the document is saved
               either way and the PDF can be rebuilt from the row. */
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "purchase-order", po.PoId, CurrentUserId());

            /* -- D1 -- money about to leave the business, so Accounts is told
               at the point it is proposed rather than when the bill lands. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.PoCreated,
                $"Purchase order raised by {CurrentUserName()}",
                $"{po.PoNo} -- PKR {po.TotalAmount:N0}. Needs approval.",
                url: $"/purchases/orders/{po.PoId}",
                exceptUserId: CurrentUserId());

            return Ok(new { id = po.PoId, poNo = po.PoNo, message = $"Purchase order {po.PoNo} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the purchase order");
        }
    }

    /// <summary>
    /// Approves a purchase order. Separate from creation on purpose: whoever
    /// raises the order is not necessarily allowed to commit the money.
    /// </summary>
    [HttpPost("orders/{id:int}/approve")]
    public async Task<IActionResult> ApprovePurchaseOrder(int id)
    {
        try
        {
            var po = await _db.PurchaseOrders.Include(p => p.Status)
                .FirstOrDefaultAsync(p => p.PoId == id);
            if (po is null) return NotFound(new { message = $"No purchase order with id {id}." });
            if (po.Status.StatusKey is "APPROVED" or "RECEIVED" or "CLOSED")
                return BadRequest(new { message = $"{po.PoNo} is already {po.Status.StatusName}." });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can approve a purchase order." });

            var approved = await _db.PurchaseOrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "APPROVED");
            if (approved is null) return BadRequest(new { message = "No APPROVED status is configured." });

            po.StatusId = approved.StatusId;
            po.ApprovedByUserId = me.Value;
            await _db.SaveChangesAsync();
            await Log("PO_APPROVED", "PurchaseOrder", po.PoNo, $"{po.TotalAmount:N0}", 2);

            /* -- D2 -- the order department is waiting for this before they can
               send anything to the supplier. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant", "order-dept" },
                NotificationKinds.PoApproved,
                $"Purchase order approved by {CurrentUserName()}",
                $"{po.PoNo} -- PKR {po.TotalAmount:N0}. It can go to the supplier.",
                url: $"/purchases/orders/{po.PoId}",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{po.PoNo} approved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"approve purchase order {id}");
        }
    }

    /// <summary>
    /// Records a goods receipt. THIS IS WHERE STOCK RISES -- not at the invoice.
    /// Damaged units are received but not added to sellable stock.
    /// </summary>
    [HttpPost("grns")]
    public async Task<IActionResult> CreateGrn([FromBody] GrnRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "A goods receipt needs at least one line." });
            if (!await _db.Parties.AnyAsync(p => p.UserId == body.SupplierId))
                return BadRequest(new { message = "Pick a valid supplier." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });
            if (string.IsNullOrWhiteSpace(body.DeliveryNoteNo))
                return BadRequest(new { message = "The supplier's delivery-note number is required." });

            foreach (var l in body.Lines)
            {
                if (l.QtyReceived <= 0)
                    return BadRequest(new { message = "Every line needs a received quantity above zero." });
                if (l.QtyDamaged < 0 || l.QtyDamaged > l.QtyReceived)
                    return BadRequest(new { message = "Damaged cannot be negative or more than received." });
                if (!await _db.Products.AnyAsync(p => p.ProductId == l.ProductId))
                    return BadRequest(new { message = $"Product {l.ProductId} does not exist." });
            }

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can receive stock." });

            var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "POSTED");
            if (posted is null) return BadRequest(new { message = "No POSTED status is configured." });

            var receipt = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "PURCHASE")
                          ?? await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "RECEIPT");
            if (receipt is null) return BadRequest(new { message = "No inbound movement type is configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var grn = new GoodsReceipt
            {
                GrnNo = await NextNumber("GRN"),
                PoId = body.PoId,
                SupplierUserId = body.SupplierId,
                LocationId = body.LocationId,
                ReceiptDate = body.ReceiptDate ?? Today(),
                DeliveryNoteNo = body.DeliveryNoteNo.Trim(),
                VehicleNo = body.VehicleNo,
                TotalValue = body.Lines.Sum(l => l.QtyReceived * l.UnitCost),
                StatusId = posted.StatusId,
                ReceivedByUserId = me.Value,
                Notes = body.Notes
            };
            _db.GoodsReceipts.Add(grn);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                _db.GoodsReceiptItems.Add(new GoodsReceiptItem
                {
                    GrnId = grn.GrnId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    QtyReceived = l.QtyReceived,
                    QtyDamaged = l.QtyDamaged,
                    UnitCost = l.UnitCost,
                    BatchNo = l.BatchNo,
                    ExpiryDate = l.ExpiryDate
                });

                /* Only ACCEPTED units go on the shelf. Damaged ones are recorded
                   on the line so the claim against the supplier has evidence,
                   but they are not sellable and must not inflate stock. */
                var accepted = l.QtyReceived - l.QtyDamaged;
                if (accepted <= 0) continue;

                var bal = await _db.StockBalances
                    .FirstOrDefaultAsync(s => s.ProductId == l.ProductId && s.LocationId == body.LocationId);
                if (bal is null)
                {
                    bal = new StockBalance { ProductId = l.ProductId, LocationId = body.LocationId, Quantity = 0 };
                    _db.StockBalances.Add(bal);
                }
                bal.Quantity += accepted;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = body.LocationId,
                    MovementTypeId = receipt.MovementTypeId,
                    MovedAt = Now(),
                    ReferenceNo = grn.GrnNo,
                    Quantity = accepted,
                    BalanceAfter = bal.Quantity,
                    UserId = CurrentUserId()
                });
            }
            await _db.SaveChangesAsync();

            /* Move the PO along so the buyer can see what has landed. */
            if (body.PoId is not null)
            {
                var po = await _db.PurchaseOrders
                    .Include(p => p.PurchaseOrderItems)
                    .Include(p => p.GoodsReceipts).ThenInclude(g => g.GoodsReceiptItems)
                    .FirstOrDefaultAsync(p => p.PoId == body.PoId);
                if (po is not null)
                {
                    var ordered = po.PurchaseOrderItems.Sum(i => i.Quantity);
                    var got = po.GoodsReceipts.SelectMany(g => g.GoodsReceiptItems).Sum(i => i.QtyReceived);
                    var key = got >= ordered ? "RECEIVED" : "PARTIALLY_RECEIVED";
                    var st = await _db.PurchaseOrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == key);
                    if (st is not null) po.StatusId = st.StatusId;
                    await _db.SaveChangesAsync();
                }
            }

            await tx.CommitAsync();
            await Log("GRN_CREATED", "GoodsReceipt", grn.GrnNo,
                $"{body.Lines.Count} lines, {grn.TotalValue:N0}", 1);

            /* The PDF exists the moment the document does. Print and Download
               then hand out the stored Cloudinary file rather than rendering a
               fresh one, so what is on screen is what is in the store. A
               failure here is logged and swallowed -- the document is saved
               either way and the PDF can be rebuilt from the row. */
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "goods-receipt", grn.GrnId, CurrentUserId());

            /* -- D3 -- stock has physically arrived, so the order department
               can start promising it and Accounts can expect a bill. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant", "order-dept" },
                NotificationKinds.GrnCreated,
                $"Goods received by {CurrentUserName()}",
                $"{grn.GrnNo} -- PKR {grn.TotalValue:N0} of stock is in.",
                url: $"/purchases/grns/{grn.GrnId}",
                exceptUserId: CurrentUserId());

            return Ok(new { id = grn.GrnId, grnNo = grn.GrnNo, message = $"{grn.GrnNo} received and stock updated." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "record the goods receipt");
        }
    }

    /// <summary>
    /// Records the supplier's bill. The PAYABLE rises here; stock does not move
    /// (that happened at the GRN). SupplierInvoiceNo is their number on their
    /// paper -- ours is generated.
    /// </summary>
    [HttpPost("invoices")]
    public async Task<IActionResult> CreatePurchaseInvoice([FromBody] PiRequest body)
    {
        try
        {
            var err = await ValidateLines(body.Lines, body.SupplierId, null);
            if (err is not null) return BadRequest(new { message = err });
            if (string.IsNullOrWhiteSpace(body.SupplierInvoiceNo))
                return BadRequest(new { message = "The supplier's own invoice number is required." });
            if (body.WhtAmount < 0)
                return BadRequest(new { message = "Withholding tax cannot be negative." });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can record a purchase invoice." });

            var status = await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "ISSUED")
                         ?? await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DRAFT");
            if (status is null) return BadRequest(new { message = "Invoice statuses are not configured." });

            decimal subtotal = 0, tax = 0;
            foreach (var l in body.Lines)
            {
                var net = l.Qty * l.UnitCost;
                subtotal += net;
                tax += net * (l.TaxPercent / 100m);
            }
            var total = subtotal - body.Discount + tax - body.WhtAmount;

            await using var tx = await _db.Database.BeginTransactionAsync();

            var pi = new PurchaseInvoice
            {
                InvoiceNo = await NextNumber("PI"),
                SupplierInvoiceNo = body.SupplierInvoiceNo.Trim(),
                SupplierUserId = body.SupplierId,
                PoId = body.PoId,
                InvoiceDate = body.InvoiceDate ?? Today(),
                DueDate = body.DueDate ?? (body.InvoiceDate ?? Today()).AddDays(30),
                Subtotal = subtotal,
                DiscountAmount = body.Discount,
                TaxAmount = tax,
                WhtAmount = body.WhtAmount,
                TotalAmount = total,
                StatusId = status.StatusId,
                MethodId = body.MethodId,
                CreatedByUserId = me.Value
            };
            _db.PurchaseInvoices.Add(pi);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                var net = l.Qty * l.UnitCost;
                _db.PurchaseInvoiceItems.Add(new PurchaseInvoiceItem
                {
                    PiId = pi.PiId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty,
                    UnitCost = l.UnitCost,
                    TaxPercent = l.TaxPercent,
                    LineTotal = net + net * (l.TaxPercent / 100m)
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("PI_CREATED", "PurchaseInvoice", pi.InvoiceNo,
                $"{body.SupplierInvoiceNo} / {total:N0}", 2);

            /* The PDF exists the moment the document does. Print and Download
               then hand out the stored Cloudinary file rather than rendering a
               fresh one, so what is on screen is what is in the store. A
               failure here is logged and swallowed -- the document is saved
               either way and the PDF can be rebuilt from the row. */
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "purchase-invoice", pi.PiId, CurrentUserId());

            /* -- D4 -- */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.PurchaseInvoice,
                $"Supplier bill entered by {CurrentUserName()}",
                $"{pi.InvoiceNo} -- PKR {pi.TotalAmount:N0}, due {pi.DueDate:dd MMM yyyy}.",
                url: $"/purchases/invoices/{pi.PiId}",
                exceptUserId: CurrentUserId());

            return Ok(new { id = pi.PiId, invoiceNo = pi.InvoiceNo, message = $"Purchase invoice {pi.InvoiceNo} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the purchase invoice");
        }
    }

    // ════════════════════════ validation helper ════════════════════════

    private async Task<string?> ValidateLines(List<PurchaseLineRequest>? lines, int supplierId, int? locationId)
    {
        if (lines is null || lines.Count == 0) return "At least one line is required.";
        if (!await _db.Parties.AnyAsync(p => p.UserId == supplierId)) return "Pick a valid supplier.";
        if (locationId is not null && !await _db.Locations.AnyAsync(l => l.LocationId == locationId))
            return "Pick a valid location.";

        foreach (var l in lines)
        {
            if (l.Qty <= 0) return "Every line needs a quantity above zero.";
            if (l.UnitCost < 0) return "A unit cost cannot be negative.";
            if (l.TaxPercent is < 0 or > 100) return "Tax must be between 0 and 100.";
            if (!await _db.Products.AnyAsync(p => p.ProductId == l.ProductId))
                return $"Product {l.ProductId} does not exist.";
        }
        return null;
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record PurchaseLineRequest(int ProductId, int Qty, decimal UnitCost, decimal TaxPercent);

    public record PoRequest(
        int SupplierId, int LocationId, DateOnly? PoDate, DateOnly? ExpectedDate,
        decimal Discount, string? Notes, bool SubmitForApproval,
        List<PurchaseLineRequest> Lines);

    public record GrnLineRequest(
        int ProductId, int QtyReceived, int QtyDamaged, decimal UnitCost,
        string? BatchNo, DateOnly? ExpiryDate);

    public record GrnRequest(
        int? PoId, int SupplierId, int LocationId, DateOnly? ReceiptDate,
        string DeliveryNoteNo, string? VehicleNo, string? Notes,
        List<GrnLineRequest> Lines);

    public record PiRequest(
        int SupplierId, int? PoId, string SupplierInvoiceNo,
        DateOnly? InvoiceDate, DateOnly? DueDate,
        decimal Discount, decimal WhtAmount, int MethodId,
        List<PurchaseLineRequest> Lines);


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

    /// <summary>Every purchase order on the current filter, as a spreadsheet.</summary>
    [HttpGet("orders/export")]
    public async Task<IActionResult> ExportPurchaseOrders(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? supplierId)
    {
        try
        {
            var action = await GetPurchaseOrders(q, status, supplierId);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var columns = new[]
            {
                new XlsxWriter.Column("PO No", "poNo", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Supplier", "supplierName", XlsxWriter.CellKind.Text, 32),
                new XlsxWriter.Column("Deliver To", "location", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("PO Date", "poDate", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Expected", "expectedDate", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Status", "statusName"),
                new XlsxWriter.Column("Lines", "itemCount", XlsxWriter.CellKind.Integer, 10),
                new XlsxWriter.Column("Ordered Units", "orderedUnits", XlsxWriter.CellKind.Integer, 14),
                new XlsxWriter.Column("Received Units", "receivedUnits", XlsxWriter.CellKind.Integer, 14),
                new XlsxWriter.Column("Received %", "receivedPercent", XlsxWriter.CellKind.Number, 12),
                new XlsxWriter.Column("Total", "total", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Raised By", "createdBy", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("Approved By", "approvedBy", XlsxWriter.CellKind.Text, 20),
            };

            var bytes = XlsxWriter.FromPayload("Purchase Orders",
                JsonSerializer.SerializeToElement(ok.Value, ExportJson), columns);
            return File(bytes, XlsxWriter.ContentType, $"purchase-orders-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "export the purchase orders");
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
