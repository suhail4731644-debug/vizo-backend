using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /packing screen -- the Order Department's own home page since
/// 23 September, and the front door of dispatching an order.
///
/// This controller used to hold the pre-chain "pick and box" queue: an order
/// arrived PACKED and moved to /dispatch. That screen and its PACKED status
/// were retired on 22 September (migration 21) -- stock now leaves at
/// DISPATCHED, from the place the order screen asks for, and there is nothing
/// left of the old queue worth keeping.
///
/// WHAT REPLACED IT is not a queue at all. The owner's brief: three dropdowns
/// -- salesperson, customer, order -- that narrow each other down and can be
/// filled in from any direction, an order's items shown with their pictures
/// and the Super Admin's own selling price (never the margin a rep may have
/// added), and a quantity the order desk may reduce but never raise. THIS
/// controller only READS that -- salespeople, customers and orders ready to
/// pack, and one order's own detail. The actual dispatch -- moving stock,
/// writing DispatchedQty, rebuilding the invoice's PDF, warning of a shortage
/// -- is the same PATCH /sales/orders/{id}/status the order detail page has
/// always used, extended to accept the quantities this screen lets the order
/// desk adjust (SalesController.SetOrderStatus). One action that changes an
/// order's status belongs in one place.
///
/// "READY TO PACK" means the order sits at INVOICED or AT_ORDER_DEPT. Both are
/// offered, on purpose: OrderWorkflow now lets the order desk dispatch straight
/// from either one, so there is no long "take up the order" click this screen
/// would otherwise be missing.
///
/// Controller-only by design: no DTOs, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports via Fail().
/// </summary>
[Route("api/packing")]
[ApiController]
[Authorize(Policy = "OrderDept")]
public class PackingController : ApiControllerBase
{
    public PackingController(AppDbContext db, IConfiguration cfg,
        ILogger<PackingController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env)
    {
    }

    /// <summary>The two statuses this screen will pick an order up from. See the class comment.</summary>
    private static readonly string[] Ready = { OrderWorkflow.Invoiced, OrderWorkflow.AtOrderDept };

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS -- the sales and customer dropdowns
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Only salespeople and customers who currently have something ready to
    /// pack. A full staff list or a full customer list would be true every
    /// day of the year and useful on none of them -- the point of these two
    /// boxes is to narrow the third one down, and there is nothing to narrow
    /// towards a name with an empty queue.
    /// </summary>
    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            var readyOrders = _db.SalesOrders.AsNoTracking()
                .Where(o => Ready.Contains(o.Status.StatusKey));

            var repIds = await readyOrders
                .Where(o => o.SalesPersonUserId != null)
                .Select(o => o.SalesPersonUserId!.Value)
                .Distinct()
                .ToListAsync();

            /* "assigned to the sales role" -- literally. A ready order's
               SalesPersonUserId is usually a rep, but not always (an order
               keyed in on somebody's behalf still carries who keyed it in),
               and this box must never offer a name that is not really a
               salesperson. The customer list below still tags itself with
               the order's true credited id, whatever role that person holds --
               only the dropdown's OWN contents are narrowed here. */
            var salesPeople = await _db.Employees.AsNoTracking()
                .Where(e => repIds.Contains(e.UserId) && e.User.Role.RoleKey == "sales")
                .OrderBy(e => e.User.FullName)
                .Select(e => new { id = e.UserId, name = e.User.FullName })
                .ToListAsync();

            /* One row per customer with a ready order, and EVERY rep credited
               with one of their ready orders -- not the customer's assigned rep
               (Party.SalesPersonUserId), which can differ from who actually
               wrote a given order, and not always exactly one: the same shop
               can have one order from its usual rep and another keyed in by
               somebody else. The reverse flow ("pick the customer, the
               salesperson sets itself") only guesses when there is exactly one
               name to guess -- with more than one it leaves the box open and
               the order list, filtered on the customer alone, already shows
               every rep's order for them. */
            var pairs = await readyOrders
                .Where(o => o.SalesPersonUserId != null)
                .Select(o => new { o.CustomerUserId, o.SalesPersonUserId })
                .Distinct()
                .ToListAsync();

            var customerIds = pairs.Select(x => x.CustomerUserId).Distinct().ToList();
            var names = await _db.Parties.AsNoTracking()
                .Where(p => customerIds.Contains(p.UserId))
                .Select(p => new { p.UserId, name = (p.DisplayName ?? p.LegalName) })
                .ToDictionaryAsync(p => p.UserId, p => p.name);

            var customers = pairs.GroupBy(x => x.CustomerUserId)
                .Select(g => new
                {
                    id = g.Key,
                    name = names.GetValueOrDefault(g.Key, $"Customer {g.Key}"),
                    repIds = g.Select(x => x.SalesPersonUserId!.Value).Distinct().ToList()
                })
                .OrderBy(c => c.name)
                .ToList();

            return Ok(new
            {
                salesPeople,
                customers,
                count = await readyOrders.CountAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the packing lookups");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE ORDER DROPDOWN
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Orders ready to pack, narrowed by whichever of the two upstream boxes
    /// is filled in. Neither is required -- an empty order dropdown with
    /// nothing picked above it is still every order waiting on the order desk.
    /// </summary>
    [HttpGet("orders")]
    public async Task<IActionResult> GetPackableOrders(
        [FromQuery] int? salesPersonId, [FromQuery] int? customerId)
    {
        try
        {
            var rows = _db.SalesOrders.AsNoTracking()
                .Where(o => Ready.Contains(o.Status.StatusKey));

            if (salesPersonId is not null) rows = rows.Where(o => o.SalesPersonUserId == salesPersonId);
            if (customerId is not null) rows = rows.Where(o => o.CustomerUserId == customerId);

            var items = await rows
                .OrderBy(o => o.OrderDate).ThenBy(o => o.OrderId)
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customerId = o.CustomerUserId,
                    customerName = (o.CustomerUser.DisplayName ?? o.CustomerUser.LegalName),
                    repId = o.SalesPersonUserId,
                    repName = o.SalesPersonUserId == null ? null
                        : _db.Users.Where(u => u.UserId == o.SalesPersonUserId).Select(u => u.FullName).FirstOrDefault(),
                    status = o.Status.StatusKey,
                    statusName = o.Status.StatusName,
                    orderDate = o.OrderDate,
                    total = o.TotalAmount,
                    itemCount = o.SalesOrderItems.Count,
                    totalUnits = o.SalesOrderItems.Sum(l => (int?)l.Quantity) ?? 0,
                    /* A handful of pictures for the closed row -- see the
                       Packing page. The full line list is the {id} call below. */
                    thumbnails = o.SalesOrderItems.OrderBy(l => l.LineNo)
                        .Select(l => l.Product.ImageUrl)
                        .Where(u => u != null)
                        .Take(4)
                        .ToList()
                })
                .ToListAsync();

            return Ok(new { count = items.Count, items });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load packable orders");
        }
    }

    /// <summary>
    /// One order's full detail: its customer and salesperson (so the screen can
    /// set both dropdowns above it, whichever direction the order was reached
    /// from), and every line with the picture, the quantity ordered, and the
    /// PRICE -- which is deliberately the product's own selling price
    /// (Product.SalePrice, what the Super Admin set), never the order line's
    /// own Rate. "The Order Department must only view the base selling price
    /// defined by the Super Admin," in the owner's own words -- a rep's margin
    /// is not this screen's business.
    /// </summary>
    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetPackableOrder(int id)
    {
        try
        {
            var order = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.OrderId == id && Ready.Contains(o.Status.StatusKey))
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customerId = o.CustomerUserId,
                    customerName = (o.CustomerUser.DisplayName ?? o.CustomerUser.LegalName),
                    repId = o.SalesPersonUserId,
                    repName = o.SalesPersonUserId == null ? null
                        : _db.Users.Where(u => u.UserId == o.SalesPersonUserId).Select(u => u.FullName).FirstOrDefault(),
                    status = o.Status.StatusKey,
                    statusName = o.Status.StatusName,
                    locationId = o.LocationId,
                    orderDate = o.OrderDate,
                    total = o.TotalAmount,
                    lines = o.SalesOrderItems.OrderBy(l => l.LineNo).Select(l => new
                    {
                        orderItemId = l.OrderItemId,
                        productId = l.ProductId,
                        name = l.Product.ProductName,
                        sku = l.Product.Sku,
                        imageUrl = l.Product.ImageUrl,
                        packing = l.Product.Packing,
                        qty = l.Quantity,
                        price = l.Product.SalePrice
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (order is null)
                return NotFound(new { message = $"Order {id} is not waiting to be packed." });

            return Ok(new
            {
                order.id, order.orderNo, order.customerId, order.customerName,
                order.repId, order.repName, order.status, order.statusName,
                order.locationId, order.orderDate, order.total,
                lineTotal = order.lines.Sum(l => (int?)l.qty) ?? 0,
                order.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load order {id} for packing");
        }
    }
}
