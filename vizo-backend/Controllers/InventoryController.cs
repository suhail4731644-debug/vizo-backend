using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /inventory screens: products, categories, brands, stock levels,
/// movements, adjustments and transfers.
///
/// Controller-only by design: no DTO classes, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports through
/// Fail().
///
/// Stock is never stored on the product. "StockBalance" holds quantity per
/// (product, location) and is the only truth; a product's total is the sum
/// across locations, computed in the query.
/// </summary>
[Route("api/inventory")]
[ApiController]
/* WHO MAY SEE AND CHANGE THE ITEM CATALOGUE.

   The whole controller is BackOffice, which lets the Order Department reach
   every screen in the Stock section. The owner's rule (21 September) is that
   the order desk uses Stock in Hand, Transfers, Stock Correction and Stock
   History -- and NOT Items, Categories or Brands, which they may neither open
   nor create.

   So the item, category and brand endpoints below name the three roles that may
   have anything to do with the catalogue -- the order desk is not one of them --
   and creating or changing anything also needs products.manage.

   BY ROLE, NOT BY THE products.view PERMISSION, FOR READING. Permissions reach
   this API in the sign-in token, which lives eight hours; the menu in the web
   app refreshes them from the database on every page load. Checking the new
   permission here would mean an accountant already signed in sees "Items" in
   their menu and is refused by the API until they sign in again. The role has no
   such window, and takes effect the moment this is deployed. The permission is
   still what the menu and the route guard read.

   Everything else here (stock-levels, movements, transfers, adjustments and
   /lookups, which the transfer and correction forms fill their pickers from)
   is deliberately left as it was. The attributes are ANDed with BackOffice. */
[Authorize(Policy = "BackOffice")]
public class InventoryController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public InventoryController(AppDbContext db, IConfiguration cfg,
        ILogger<InventoryController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    // ══════════════════════════════════════════════════════════════════
    //  PRODUCTS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("products")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> GetProducts(
        [FromQuery] string? q, [FromQuery] int? categoryId, [FromQuery] int? brandId,
        [FromQuery] string? status, [FromQuery] bool includeInactive = true,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            /* Up to 5000 so ExportProducts, which calls this action for the
               whole filtered list, actually gets the whole list. The limit used
               to be 200 with an out-of-range fallback of 50 -- so the export
               asked for 5000, was quietly given 50, and a catalogue of more than
               fifty products exported as its first fifty. The screen itself
               asks for a page of 24. */
            if (pageSize is < 1 or > 5000) pageSize = 50;

            var rows = _db.Products.AsNoTracking().AsQueryable();

            if (categoryId is not null) rows = rows.Where(p => p.CategoryId == categoryId);
            if (brandId is not null) rows = rows.Where(p => p.BrandId == brandId);
            if (!includeInactive) rows = rows.Where(p => p.IsActive);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(p => p.ProductName.ToLower().Contains(term) ||
                                       p.Sku.ToLower().Contains(term) ||
                                       p.ProductBarcodes.Any(b => b.Barcode.Contains(term)));
            }

            /* Status is derived from stock, but it is filtered HERE, before the
               page is cut -- filtering the page afterwards (as this used to)
               returned "low stock, page 1" as whatever few of the first fifty
               happened to be low. */
            rows = (status ?? "").ToLowerInvariant() switch
            {
                "inactive" => rows.Where(p => !p.IsActive),
                "out" => rows.Where(p => p.IsActive && (p.StockBalances.Sum(b => (int?)b.Quantity) ?? 0) <= 0),
                "low" => rows.Where(p => p.IsActive
                                         && (p.StockBalances.Sum(b => (int?)b.Quantity) ?? 0) > 0
                                         && (p.StockBalances.Sum(b => (int?)b.Quantity) ?? 0) <= p.MinQty),
                "active" => rows.Where(p => p.IsActive && (p.StockBalances.Sum(b => (int?)b.Quantity) ?? 0) > p.MinQty),
                _ => rows
            };

            var total = await rows.CountAsync();

            var items = await rows
                .OrderBy(p => p.ProductName)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new
                {
                    id = p.ProductId,
                    sku = p.Sku,
                    name = p.ProductName,
                    description = p.Description,
                    categoryId = p.CategoryId,
                    categoryName = p.Category.CategoryName,
                    brandId = p.BrandId,
                    brandName = p.Brand.BrandName,
                    packing = p.Packing,
                    minQty = p.MinQty,
                    maxQty = p.MaxQty,
                    costPrice = p.CostPrice,
                    dutyPrice = p.DutyPrice,
                    /* Derived, not read from the column: SalePrice is the
                       authority, and a row written by an older build (which
                       leaves MarginPrice at 0) must not show a margin of 0. */
                    marginPrice = p.SalePrice - p.CostPrice - p.DutyPrice,
                    salePrice = p.SalePrice,
                    taxRatePercent = p.TaxRatePercent,
                    hideStock = p.HideStock,
                    isActive = p.IsActive,
                    imageUrl = p.ImageUrl,
                    createdAt = p.CreatedAt,
                    totalStock = p.StockBalances.Sum(s => (int?)s.Quantity) ?? 0,
                    barcodes = p.ProductBarcodes.Select(b => b.Barcode).ToList()
                })
                .ToListAsync();

            /* status is derived, not stored: out -> low -> inactive -> active. */
            var shaped = items.Select(p => new
            {
                p.id, p.sku, p.name, p.description,
                p.categoryId, p.categoryName, p.brandId, p.brandName,
                p.packing, p.minQty, p.maxQty,
                p.costPrice, p.dutyPrice, p.marginPrice, p.salePrice, p.taxRatePercent,
                marginPercent = MarginPercent(p.costPrice, p.dutyPrice, p.marginPrice),
                p.hideStock, p.isActive, p.imageUrl, p.createdAt,
                p.totalStock, p.barcodes,
                status = !p.isActive ? "inactive"
                       : p.totalStock <= 0 ? "out"
                       : p.totalStock <= p.minQty ? "low" : "active"
            }).ToList();

            /* The figures over the WHOLE catalogue, not the page on screen.
               The product screen pages on the server now (AGENTS.md rule 3),
               and a "Low stock: 2" that only counted page one would be a
               number that changes when you press Next. One small query, the
               same status rule as the rows above. */
            var all = await _db.Products.AsNoTracking()
                .Select(p => new
                {
                    p.IsActive, p.MinQty, landed = p.CostPrice + p.DutyPrice,
                    stock = p.StockBalances.Sum(b => (int?)b.Quantity) ?? 0
                })
                .ToListAsync();

            var stats = new
            {
                total = all.Count,
                active = all.Count(p => p.IsActive && p.stock > p.MinQty),
                low = all.Count(p => p.IsActive && p.stock > 0 && p.stock <= p.MinQty),
                @out = all.Count(p => p.IsActive && p.stock <= 0),
                inactive = all.Count(p => !p.IsActive),
                stockValue = all.Sum(p => Math.Max(0, p.stock) * p.landed)
            };

            return Ok(new { total, page, pageSize, stats, items = shaped });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the product list");
        }
    }

    [HttpGet("products/{id:int}")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> GetProduct(int id)
    {
        try
        {
            var p = await _db.Products.AsNoTracking()
                .Where(x => x.ProductId == id)
                .Select(x => new
                {
                    id = x.ProductId,
                    sku = x.Sku,
                    name = x.ProductName,
                    description = x.Description,
                    categoryId = x.CategoryId,
                    categoryName = x.Category.CategoryName,
                    brandId = x.BrandId,
                    brandName = x.Brand.BrandName,
                    packing = x.Packing,
                    minQty = x.MinQty,
                    maxQty = x.MaxQty,
                    costPrice = x.CostPrice,
                    dutyPrice = x.DutyPrice,
                    marginPrice = x.SalePrice - x.CostPrice - x.DutyPrice,
                    salePrice = x.SalePrice,
                    taxRatePercent = x.TaxRatePercent,
                    hideStock = x.HideStock,
                    isActive = x.IsActive,
                    imageUrl = x.ImageUrl,
                    createdAt = x.CreatedAt,
                    barcodes = x.ProductBarcodes.Select(b => b.Barcode).ToList(),
                    totalStock = x.StockBalances.Sum(s => (int?)s.Quantity) ?? 0,
                    stockSpread = x.StockBalances.Select(s => new
                    {
                        locationId = s.LocationId,
                        locationCode = s.Location.LocationCode,
                        locationName = s.Location.LocationName,
                        qty = s.Quantity
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (p is null) return NotFound(new { message = $"No product with id {id}." });

            return Ok(new
            {
                p.id, p.sku, p.name, p.description,
                p.categoryId, p.categoryName, p.brandId, p.brandName,
                p.packing, p.minQty, p.maxQty,
                p.costPrice, p.dutyPrice, p.marginPrice, p.salePrice, p.taxRatePercent,
                marginPercent = MarginPercent(p.costPrice, p.dutyPrice, p.marginPrice),
                p.hideStock, p.isActive, p.imageUrl, p.createdAt,
                p.barcodes, p.totalStock, p.stockSpread,
                status = !p.isActive ? "inactive"
                       : p.totalStock <= 0 ? "out"
                       : p.totalStock <= p.minQty ? "low" : "active"
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load product {id}");
        }
    }

    [HttpPost("products")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> CreateProduct([FromBody] ProductRequest body)
    {
        try
        {
            var error = await ValidateProduct(body, null);
            if (error is not null) return BadRequest(new { message = error });

            await using var tx = await _db.Database.BeginTransactionAsync();

            /* THE SKU IS DECIDED HERE, not in the browser.

               The form shows a preview, but between typing and saving somebody
               else may have saved the same product and taken the serial -- only
               the server can see the table. So the SKU is worked out again
               inside the transaction, and the preview is only ever a guess.

               The one exception is a SKU that came off a scanned BARCODE and
               is already one of ours (VZ-...). That is not a guess: it is
               printed on the box, and the product has to carry the code the
               box says. It is still checked for uniqueness in ValidateProduct. */
            var sku = await ResolveSku(body);

            var product = new Product
            {
                Sku = sku,
                ProductName = CleanName(body.Name),
                Description = body.Description,
                CategoryId = body.CategoryId,
                BrandId = body.BrandId,
                Packing = body.Packing,
                MinQty = body.MinQty,
                MaxQty = body.MaxQty,
                CostPrice = body.CostPrice,
                DutyPrice = body.DutyPrice,
                MarginPrice = body.SalePrice - body.CostPrice - body.DutyPrice,
                SalePrice = body.SalePrice,
                TaxRatePercent = body.TaxRatePercent,
                HideStock = body.HideStock,
                IsActive = body.IsActive,
                ImageUrl = body.ImageUrl,
                CreatedAt = Today()
            };
            _db.Products.Add(product);
            await _db.SaveChangesAsync();

            foreach (var code in (body.Barcodes ?? new List<string>())
                     .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct())
            {
                _db.ProductBarcodes.Add(new ProductBarcode
                {
                    ProductId = product.ProductId,
                    Barcode = code.Trim()
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("PRODUCT_CREATED", "Product", product.Sku, product.ProductName, 1);

            /* The owner asked to be told, by name, who added what: "Talha added
               a new product" with the product on the line. Clicking it opens
               the item. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.ProductAdded,
                $"New item added by {CurrentUserName()}",
                $"{product.ProductName} ({product.Sku}) is now in the catalogue" +
                (product.SalePrice > 0 ? $" at PKR {product.SalePrice:N0}." : "."),
                url: $"/inventory/products/{product.ProductId}",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id = product.ProductId,
                sku = product.Sku,
                message = $"{product.ProductName} added as {product.Sku}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "create the product");
        }
    }

    [HttpPut("products/{id:int}")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> UpdateProduct(int id, [FromBody] ProductRequest body)
    {
        try
        {
            var product = await _db.Products
                .Include(p => p.ProductBarcodes)
                .FirstOrDefaultAsync(p => p.ProductId == id);
            if (product is null) return NotFound(new { message = $"No product with id {id}." });

            var error = await ValidateProduct(body, id);
            if (error is not null) return BadRequest(new { message = error });

            /* Editing keeps the SKU unless one is sent. It is printed on
               labels and invoices already, so changing a product's name does
               NOT re-derive it -- a SKU that shifted every time somebody fixed
               a typo would be no use as an identifier. */
            if (!string.IsNullOrWhiteSpace(body.Sku))
                product.Sku = body.Sku.Trim().ToUpperInvariant();
            product.ProductName = CleanName(body.Name);
            product.Description = body.Description;
            product.CategoryId = body.CategoryId;
            product.BrandId = body.BrandId;
            product.Packing = body.Packing;
            product.MinQty = body.MinQty;
            product.MaxQty = body.MaxQty;
            product.CostPrice = body.CostPrice;
            product.DutyPrice = body.DutyPrice;
            product.MarginPrice = body.SalePrice - body.CostPrice - body.DutyPrice;
            product.SalePrice = body.SalePrice;
            product.TaxRatePercent = body.TaxRatePercent;
            product.HideStock = body.HideStock;
            product.IsActive = body.IsActive;
            product.ImageUrl = body.ImageUrl;

            var wanted = (body.Barcodes ?? new List<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct().ToList();

            _db.ProductBarcodes.RemoveRange(
                product.ProductBarcodes.Where(b => !wanted.Contains(b.Barcode)));
            foreach (var code in wanted.Where(c => product.ProductBarcodes.All(b => b.Barcode != c)))
                _db.ProductBarcodes.Add(new ProductBarcode { ProductId = id, Barcode = code });

            await _db.SaveChangesAsync();
            await Log("PRODUCT_UPDATED", "Product", product.Sku, product.ProductName, 1);

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.ProductChanged,
                $"Item edited by {CurrentUserName()}",
                $"{product.ProductName} ({product.Sku}) was changed" +
                (product.SalePrice > 0 ? $" -- now PKR {product.SalePrice:N0}." : "."),
                url: $"/inventory/products/{product.ProductId}",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{product.ProductName} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"save product {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  SKU, NAME AND BARCODE CHECKS  --  what the new-product form asks
    //  while the person is still typing
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// What the SKU will be, and whether this exact product is already in the
    /// catalogue -- asked on every pause in typing, so the answer is on screen
    /// before Save is pressed rather than as an error after it.
    ///
    /// A PREVIEW. The real SKU is worked out again when the product is saved,
    /// inside the same transaction as the insert; see ResolveSku.
    /// </summary>
    [HttpPost("products/sku-preview")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> SkuPreview([FromBody] SkuPreviewRequest body)
    {
        try
        {
            var categoryName = body.CategoryId is null ? null
                : await _db.Categories.AsNoTracking().Where(c => c.CategoryId == body.CategoryId)
                    .Select(c => c.CategoryName).FirstOrDefaultAsync();
            var brandName = body.BrandId is null ? null
                : await _db.Brands.AsNoTracking().Where(b => b.BrandId == body.BrandId)
                    .Select(b => b.BrandName).FirstOrDefaultAsync();

            var fromBarcode = SkuFromBarcodes(body.Barcodes);
            var parts = SkuGenerator.Parse(body.Name, categoryName, brandName);

            string? sku = null;
            if (fromBarcode is not null) sku = fromBarcode;
            else if (!string.IsNullOrWhiteSpace(body.Name) && categoryName is not null)
                sku = await NextSkuFor(parts.Stem);

            /* The duplicate check, same rule the save uses: exactly the same
               name, ignoring case and extra spaces. */
            object? duplicate = null;
            if (!string.IsNullOrWhiteSpace(body.Name))
            {
                var normal = SkuGenerator.NormalName(body.Name);
                var names = await _db.Products.AsNoTracking()
                    .Select(p => new { p.ProductId, p.ProductName, p.Sku })
                    .ToListAsync();
                var twin = names.FirstOrDefault(p => SkuGenerator.NormalName(p.ProductName) == normal);
                if (twin is not null)
                    duplicate = new { id = twin.ProductId, name = twin.ProductName, sku = twin.Sku };
            }

            bool? barcodeSkuTaken = fromBarcode is null ? null
                : await _db.Products.AnyAsync(p => p.Sku.ToUpper() == fromBarcode);

            return Ok(new
            {
                sku,
                source = fromBarcode is not null ? "barcode" : sku is null ? "incomplete" : "generated",
                barcodeSkuTaken,
                parts = new { word = parts.Word, model = parts.Model, category = parts.Category, color = parts.Color },
                duplicate
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "work out the SKU");
        }
    }

    /// <summary>
    /// Who already owns a barcode, answered the moment it is scanned -- so the
    /// camera does not add a code to the form only for Save to refuse it later.
    /// Also says whether the code is one of OUR SKUs printed as a barcode.
    /// </summary>
    [HttpGet("barcodes/lookup")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> LookupBarcode([FromQuery] string code, [FromQuery] int? excludeProductId)
    {
        try
        {
            var c = (code ?? "").Trim();
            if (c.Length == 0) return BadRequest(new { message = "No barcode to look up." });

            var owner = await _db.ProductBarcodes.AsNoTracking()
                .Where(b => b.Barcode == c && (excludeProductId == null || b.ProductId != excludeProductId))
                .Select(b => new { id = b.ProductId, name = b.Product.ProductName, sku = b.Product.Sku })
                .FirstOrDefaultAsync();

            var sku = SkuFromBarcodes(new List<string> { c });

            return Ok(new { code = c, taken = owner is not null, owner, sku });
        }
        catch (Exception ex)
        {
            return Fail(ex, "look up the barcode");
        }
    }

    /// <summary>
    /// The SKU a new product is saved under.
    ///
    ///   1. A barcode that CARRIES one of our SKUs wins. It is printed on the
    ///      box, so the product has to have the code the box says -- the brief
    ///      is explicit that a SKU found in a barcode replaces whatever was
    ///      generated.
    ///   2. Otherwise it is generated from the name and category, with the next
    ///      free serial for that stem.
    ///
    /// The serial is allocated under a transaction-scoped advisory lock, so two
    /// people saving the same product at the same moment get 01 and 02 rather
    /// than one of them failing on the unique index. Adding a product is rare
    /// enough that serialising it costs nothing anybody will notice.
    /// </summary>
    private async Task<string> ResolveSku(ProductRequest body)
    {
        var fromBarcode = SkuFromBarcodes(body.Barcodes)
            ?? (SkuGenerator.LooksLikeSku(body.Sku) && (body.Barcodes ?? new()).Any(b =>
                    b.Contains(body.Sku!.Trim(), StringComparison.OrdinalIgnoreCase))
                ? body.Sku!.Trim().ToUpperInvariant() : null);
        if (fromBarcode is not null) return fromBarcode;

        var categoryName = await _db.Categories.AsNoTracking()
            .Where(c => c.CategoryId == body.CategoryId).Select(c => c.CategoryName).FirstAsync();
        var brandName = await _db.Brands.AsNoTracking()
            .Where(b => b.BrandId == body.BrandId).Select(b => b.BrandName).FirstOrDefaultAsync();

        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(4242001)");

        return await NextSkuFor(SkuGenerator.Parse(body.Name, categoryName, brandName).Stem);
    }

    private async Task<string> NextSkuFor(string stem)
    {
        var upper = stem.ToUpperInvariant();
        var existing = await _db.Products.AsNoTracking()
            .Where(p => p.Sku.ToUpper().StartsWith(upper))
            .Select(p => p.Sku)
            .ToListAsync();
        return SkuGenerator.Next(upper, existing);
    }

    /// <summary>
    /// The first of our own SKUs found inside any of the barcodes -- whole, or
    /// embedded in a longer payload such as a QR code that carries "SKU:" and
    /// a URL around it.
    /// </summary>
    private static string? SkuFromBarcodes(IEnumerable<string>? codes)
    {
        foreach (var raw in codes ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var m = System.Text.RegularExpressions.Regex.Match(raw.ToUpperInvariant(),
                @"(?<![A-Z0-9])VZ(-[A-Z0-9]{1,8}){2,6}(?![A-Z0-9-])");
            if (m.Success) return m.Value;
        }
        return null;
    }

    /// <summary>A name as stored: trimmed, runs of spaces collapsed.</summary>
    private static string CleanName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+", " ");

    /// <summary>
    /// Margin as a percentage of LANDED cost (cost + duty) -- the base the
    /// pricing form works on, so the figure on the list and the figure the
    /// person typed are the same number.
    /// </summary>
    private static decimal MarginPercent(decimal cost, decimal duty, decimal margin)
    {
        var landed = cost + duty;
        return landed > 0 ? Math.Round(margin / landed * 100m, 2) : 0m;
    }

    // ══════════════════════════════════════════════════════════════════
    //  CATEGORIES AND BRANDS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("categories")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> GetCategories()
    {
        try
        {
            return Ok(await _db.Categories.AsNoTracking()
                .OrderBy(c => c.CategoryName)
                .Select(c => new
                {
                    id = c.CategoryId,
                    name = c.CategoryName,
                    parentId = c.ParentCategoryId,
                    parentName = c.ParentCategory != null ? c.ParentCategory.CategoryName : null,
                    isActive = c.IsActive,
                    productCount = c.Products.Count
                })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load categories");
        }
    }

    [HttpPost("categories")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> CreateCategory([FromBody] CategoryRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return BadRequest(new { message = "Category name is required." });

            var name = body.Name.Trim();
            if (await _db.Categories.AnyAsync(c => c.CategoryName.ToLower() == name.ToLower()))
                return BadRequest(new { message = $"A category called {name} already exists." });

            var c = new Category
            {
                CategoryName = name,
                ParentCategoryId = body.ParentId == 0 ? null : body.ParentId,
                IsActive = body.IsActive
            };
            _db.Categories.Add(c);
            await _db.SaveChangesAsync();
            await Log("CATEGORY_CREATED", "Category", c.CategoryId.ToString(), name, 1);

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.CatalogChanged,
                $"Catalogue changed by {CurrentUserName()}",
                $"Category {name} was added.",
                url: "/inventory/categories",
                exceptUserId: CurrentUserId());

            return Ok(new { id = c.CategoryId, message = $"{name} added." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "create the category");
        }
    }

    [HttpPut("categories/{id:int}")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> UpdateCategory(int id, [FromBody] CategoryRequest body)
    {
        try
        {
            var c = await _db.Categories.FirstOrDefaultAsync(x => x.CategoryId == id);
            if (c is null) return NotFound(new { message = $"No category with id {id}." });
            if (string.IsNullOrWhiteSpace(body.Name))
                return BadRequest(new { message = "Category name is required." });
            if (body.ParentId == id)
                return BadRequest(new { message = "A category cannot be its own parent." });

            c.CategoryName = body.Name.Trim();
            c.ParentCategoryId = body.ParentId;
            c.IsActive = body.IsActive;
            await _db.SaveChangesAsync();
            await Log("CATEGORY_UPDATED", "Category", id.ToString(), c.CategoryName, 1);

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.CatalogChanged,
                $"Catalogue changed by {CurrentUserName()}",
                $"Category {c.CategoryName} was renamed or edited.",
                url: "/inventory/categories",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{c.CategoryName} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"save category {id}");
        }
    }

    [HttpGet("brands")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> GetBrands()
    {
        try
        {
            return Ok(await _db.Brands.AsNoTracking()
                .OrderBy(b => b.BrandName)
                .Select(b => new
                {
                    id = b.BrandId,
                    code = b.BrandCode,
                    name = b.BrandName,
                    description = b.Description,
                    isActive = b.IsActive,
                    productCount = b.Products.Count
                })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load brands");
        }
    }

    [HttpPost("brands")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> CreateBrand([FromBody] BrandRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return BadRequest(new { message = "Brand name is required." });
            if (string.IsNullOrWhiteSpace(body.Code))
                return BadRequest(new { message = "Brand code is required." });

            var code = body.Code.Trim().ToUpperInvariant();
            if (await _db.Brands.AnyAsync(b => b.BrandCode.ToUpper() == code))
                return BadRequest(new { message = $"Brand code {code} is already in use." });

            var b = new Brand
            {
                BrandCode = code,
                BrandName = body.Name.Trim(),
                Description = body.Description,
                IsActive = body.IsActive
            };
            _db.Brands.Add(b);
            await _db.SaveChangesAsync();
            await Log("BRAND_CREATED", "Brand", code, b.BrandName, 1);

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.CatalogChanged,
                $"Catalogue changed by {CurrentUserName()}",
                $"Brand {b.BrandName} was added.",
                url: "/inventory/brands",
                exceptUserId: CurrentUserId());

            return Ok(new { id = b.BrandId, message = $"{b.BrandName} added." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "create the brand");
        }
    }

    [HttpPut("brands/{id:int}")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> UpdateBrand(int id, [FromBody] BrandRequest body)
    {
        try
        {
            var b = await _db.Brands.FirstOrDefaultAsync(x => x.BrandId == id);
            if (b is null) return NotFound(new { message = $"No brand with id {id}." });
            if (string.IsNullOrWhiteSpace(body.Name))
                return BadRequest(new { message = "Brand name is required." });

            var code = (body.Code ?? b.BrandCode).Trim().ToUpperInvariant();
            if (await _db.Brands.AnyAsync(x => x.BrandCode.ToUpper() == code && x.BrandId != id))
                return BadRequest(new { message = $"Brand code {code} is already in use." });

            b.BrandCode = code;
            b.BrandName = body.Name.Trim();
            b.Description = body.Description;
            b.IsActive = body.IsActive;
            await _db.SaveChangesAsync();
            await Log("BRAND_UPDATED", "Brand", code, b.BrandName, 1);

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.CatalogChanged,
                $"Catalogue changed by {CurrentUserName()}",
                $"Brand {b.BrandName} was renamed or edited.",
                url: "/inventory/brands",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{b.BrandName} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"save brand {id}");
        }
    }


    /// <summary>
    /// Deletes a category. Refuses while products still point at it -- the FK
    /// would reject it anyway, but a clear message beats a 23503 in the log.
    /// </summary>
    [HttpDelete("categories/{id:int}")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> DeleteCategory(int id)
    {
        try
        {
            var c = await _db.Categories
                .Include(x => x.Products)
                .Include(x => x.InverseParentCategory)
                .FirstOrDefaultAsync(x => x.CategoryId == id);
            if (c is null) return NotFound(new { message = $"No category with id {id}." });

            if (c.Products.Count > 0)
                return BadRequest(new
                {
                    message = $"{c.CategoryName} still has {c.Products.Count} product(s). " +
                              "Move them to another category first, or set this one inactive instead."
                });
            if (c.InverseParentCategory.Count > 0)
                return BadRequest(new
                {
                    message = $"{c.CategoryName} has {c.InverseParentCategory.Count} sub-categor" +
                              (c.InverseParentCategory.Count == 1 ? "y" : "ies") + ". Remove those first."
                });

            var name = c.CategoryName;
            _db.Categories.Remove(c);
            await _db.SaveChangesAsync();
            await Log("CATEGORY_DELETED", "Category", id.ToString(), name, 3);

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.CatalogChanged,
                $"Catalogue changed by {CurrentUserName()}",
                $"Category {name} was deleted.",
                url: "/inventory/categories",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{name} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"delete category {id}");
        }
    }

    /// <summary>Deletes a brand. Refuses while products still point at it.</summary>
    [HttpDelete("brands/{id:int}")]
    [Authorize(Roles = "super-admin,accountant")]
    [Authorize(Policy = "perm:products.manage")]
    public async Task<IActionResult> DeleteBrand(int id)
    {
        try
        {
            var b = await _db.Brands.Include(x => x.Products)
                .FirstOrDefaultAsync(x => x.BrandId == id);
            if (b is null) return NotFound(new { message = $"No brand with id {id}." });

            if (b.Products.Count > 0)
                return BadRequest(new
                {
                    message = $"{b.BrandName} still has {b.Products.Count} product(s). " +
                              "Reassign them first, or set this brand inactive instead."
                });

            var name = b.BrandName;
            _db.Brands.Remove(b);
            await _db.SaveChangesAsync();
            await Log("BRAND_DELETED", "Brand", id.ToString(), name, 3);

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.CatalogChanged,
                $"Catalogue changed by {CurrentUserName()}",
                $"Brand {name} was deleted.",
                url: "/inventory/brands",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{name} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"delete brand {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  STOCK LEVELS AND MOVEMENTS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("stock-levels")]
    public async Task<IActionResult> GetStockLevels(
        [FromQuery] int? locationId, [FromQuery] int? cityId,
        [FromQuery] string? q, [FromQuery] string? status)
    {
        try
        {
            var rows = _db.StockBalances.AsNoTracking().AsQueryable();

            if (locationId is not null) rows = rows.Where(s => s.LocationId == locationId);

            /* STOCK IN HAND, BY CITY.

               A city is where the business actually keeps things: Karachi has a
               warehouse and an order department, Lahore has its own pair, and
               "how much do we hold in Lahore" is the warehouse and the desk
               added together. Filtering by one location cannot answer it --
               half the stock is on the other shelf -- and the screen was
               therefore only ever able to show one warehouse or the whole
               company with nothing in between.

               Passing neither gives the whole system, which is the other half
               of what was asked for: everything, combined. */
            if (cityId is not null) rows = rows.Where(s => s.Location.CityId == cityId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(s => s.Product.ProductName.ToLower().Contains(term) ||
                                       s.Product.Sku.ToLower().Contains(term));
            }

            var items = await rows
                .OrderBy(s => s.Product.ProductName).ThenBy(s => s.Location.LocationName)
                .Select(s => new
                {
                    productId = s.ProductId,
                    sku = s.Product.Sku,
                    imageUrl = s.Product.ImageUrl,
                    name = s.Product.ProductName,
                    packing = s.Product.Packing,
                    minQty = s.Product.MinQty,
                    maxQty = s.Product.MaxQty,
                    costPrice = s.Product.CostPrice,
                    locationId = s.LocationId,
                    locationCode = s.Location.LocationCode,
                    locationName = s.Location.LocationName,
                    locationKind = s.Location.Kind.KindKey,
                    cityId = s.Location.CityId,
                    cityName = s.Location.City.CityName,
                    qty = s.Quantity
                })
                .ToListAsync();

            var shaped = items.Select(s => new
            {
                s.productId, s.sku, s.name, s.packing, s.minQty, s.maxQty, s.costPrice,
                s.locationId, s.locationCode, s.locationName, s.locationKind,
                s.cityId, s.cityName, s.qty,
                packets = s.packing > 0 ? s.qty / s.packing : 0,
                loose = s.packing > 0 ? s.qty % s.packing : s.qty,
                value = s.qty * s.costPrice,
                status = s.qty <= 0 ? "out"
                       : s.qty <= s.minQty ? "low"
                       : s.maxQty > 0 && s.qty > s.maxQty ? "over" : "ok"
            }).ToList();

            if (!string.IsNullOrWhiteSpace(status))
                shaped = shaped.Where(s => s.status == status).ToList();

            return Ok(new
            {
                totalValue = shaped.Sum(s => s.value),
                totalUnits = shaped.Sum(s => s.qty),
                /* What the filter is currently looking at, so the screen can
                   label its own figures honestly rather than always saying
                   "total" whether or not a city is selected. */
                scope = cityId is not null ? "city" : locationId is not null ? "location" : "all",
                cityId,
                locationId,
                /* Each city's own total, so the dropdown can show what it is
                   about to switch to and the owner can compare two towns
                   without changing the filter twice. Always the whole company,
                   never the current filter -- a breakdown that moves when you
                   pick one of its own rows is a breakdown nobody can read. */
                byCity = await _db.StockBalances.AsNoTracking()
                    .GroupBy(b => new { b.Location.CityId, b.Location.City.CityName })
                    .Select(g => new
                    {
                        cityId = g.Key.CityId,
                        city = g.Key.CityName,
                        units = g.Sum(x => x.Quantity),
                        value = g.Sum(x => x.Quantity * x.Product.CostPrice),
                        locations = g.Select(x => x.LocationId).Distinct().Count()
                    })
                    .OrderBy(c => c.city)
                    .ToListAsync(),
                items = shaped
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load stock levels");
        }
    }

    [HttpGet("movements")]
    public async Task<IActionResult> GetMovements(
        [FromQuery] int? productId, [FromQuery] int? locationId,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.StockMovements.AsNoTracking().AsQueryable();

            if (productId is not null) rows = rows.Where(m => m.ProductId == productId);
            if (locationId is not null) rows = rows.Where(m => m.LocationId == locationId);
            if (from is not null)
                rows = rows.Where(m => m.MovedAt >= from.Value.ToDateTime(TimeOnly.MinValue));
            if (to is not null)
                rows = rows.Where(m => m.MovedAt <= to.Value.ToDateTime(TimeOnly.MaxValue));

            var total = await rows.CountAsync();

            var items = await rows
                .OrderByDescending(m => m.MovedAt).ThenByDescending(m => m.MovementId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(m => new
                {
                    id = m.MovementId,
                    productId = m.ProductId,
                    sku = m.Product.Sku,
                    imageUrl = m.Product.ImageUrl,
                    name = m.Product.ProductName,
                    locationId = m.LocationId,
                    locationName = m.Location.LocationName,
                    movementType = m.MovementType.TypeKey,
                    movementTypeName = m.MovementType.TypeName,
                    movedAt = m.MovedAt,
                    referenceNo = m.ReferenceNo,
                    qty = m.Quantity,
                    balanceAfter = m.BalanceAfter,
                    user = m.User.FullName
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, items });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load stock movements");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ADJUSTMENTS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("adjustments")]
    public async Task<IActionResult> GetAdjustments([FromQuery] string? status)
    {
        try
        {
            var rows = _db.StockAdjustments.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
                rows = rows.Where(a => a.Status.StatusKey == status);

            return Ok(await rows
                .OrderByDescending(a => a.AdjustmentDate).ThenByDescending(a => a.AdjustmentId)
                .Select(a => new
                {
                    id = a.AdjustmentId,
                    adjustmentNo = a.AdjustmentNo,
                    locationId = a.LocationId,
                    locationName = a.Location.LocationName,
                    adjustmentDate = a.AdjustmentDate,
                    reason = a.Reason.ReasonKey,
                    reasonName = a.Reason.ReasonName,
                    reasonNotes = a.ReasonNotes,
                    status = a.Status.StatusKey,
                    statusName = a.Status.StatusName,
                    createdBy = a.CreatedByUser.User.FullName,
                    itemCount = a.StockAdjustmentItems.Count,
                    netUnits = a.StockAdjustmentItems.Sum(i => (int?)(i.NewQty - i.CurrentQty)) ?? 0
                })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load stock adjustments");
        }
    }

    [HttpGet("adjustments/{id:int}")]
    public async Task<IActionResult> GetAdjustment(int id)
    {
        try
        {
            var a = await _db.StockAdjustments.AsNoTracking()
                .Where(x => x.AdjustmentId == id)
                .Select(x => new
                {
                    id = x.AdjustmentId,
                    adjustmentNo = x.AdjustmentNo,
                    locationId = x.LocationId,
                    locationName = x.Location.LocationName,
                    adjustmentDate = x.AdjustmentDate,
                    reason = x.Reason.ReasonKey,
                    reasonName = x.Reason.ReasonName,
                    reasonNotes = x.ReasonNotes,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    createdBy = x.CreatedByUser.User.FullName,
                    lines = x.StockAdjustmentItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        id = i.AdjustmentItemId,
                        lineNo = i.LineNo,
                        productId = i.ProductId,
                        sku = i.Product.Sku,
                        imageUrl = i.Product.ImageUrl,
                        name = i.Product.ProductName,
                        currentQty = i.CurrentQty,
                        newQty = i.NewQty,
                        delta = i.NewQty - i.CurrentQty,
                        costPrice = i.Product.CostPrice
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (a is null) return NotFound(new { message = $"No adjustment with id {id}." });
            return Ok(a);
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load adjustment {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  TRANSFERS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("transfers")]
    public async Task<IActionResult> GetTransfers([FromQuery] string? status)
    {
        try
        {
            var rows = _db.StockTransfers.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
                rows = rows.Where(t => t.Status.StatusKey == status);

            return Ok(await rows
                .OrderByDescending(t => t.TransferDate).ThenByDescending(t => t.TransferId)
                .Select(t => new
                {
                    id = t.TransferId,
                    transferNo = t.TransferNo,
                    fromLocationId = t.FromLocationId,
                    fromLocation = t.FromLocation.LocationName,
                    toLocationId = t.ToLocationId,
                    toLocation = t.ToLocation.LocationName,
                    transferDate = t.TransferDate,
                    receivedOn = t.ReceivedOn,
                    status = t.Status.StatusKey,
                    statusName = t.Status.StatusName,
                    initiatedBy = t.InitiatedByUser.User.FullName,
                    approvedBy = t.ApprovedByUser != null ? t.ApprovedByUser.User.FullName : null,
                    notes = t.Notes,
                    itemCount = t.StockTransferItems.Count,
                    totalUnits = t.StockTransferItems.Sum(i => (int?)i.Quantity) ?? 0
                })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load stock transfers");
        }
    }

    [HttpGet("transfers/{id:int}")]
    public async Task<IActionResult> GetTransfer(int id)
    {
        try
        {
            var t = await _db.StockTransfers.AsNoTracking()
                .Where(x => x.TransferId == id)
                .Select(x => new
                {
                    id = x.TransferId,
                    transferNo = x.TransferNo,
                    fromLocationId = x.FromLocationId,
                    fromLocation = x.FromLocation.LocationName,
                    toLocationId = x.ToLocationId,
                    toLocation = x.ToLocation.LocationName,
                    transferDate = x.TransferDate,
                    receivedOn = x.ReceivedOn,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    initiatedBy = x.InitiatedByUser.User.FullName,
                    approvedBy = x.ApprovedByUser != null ? x.ApprovedByUser.User.FullName : null,
                    notes = x.Notes,
                    lines = x.StockTransferItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        id = i.TransferItemId,
                        lineNo = i.LineNo,
                        productId = i.ProductId,
                        sku = i.Product.Sku,
                        imageUrl = i.Product.ImageUrl,
                        name = i.Product.ProductName,
                        qty = i.Quantity,
                        packing = i.Product.Packing
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (t is null) return NotFound(new { message = $"No transfer with id {id}." });
            return Ok(t);
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load transfer {id}");
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
                categories = await _db.Categories.AsNoTracking()
                    .Where(c => c.IsActive).OrderBy(c => c.CategoryName)
                    .Select(c => new { id = c.CategoryId, name = c.CategoryName, parentId = c.ParentCategoryId })
                    .ToListAsync(),
                brands = await _db.Brands.AsNoTracking()
                    .Where(b => b.IsActive).OrderBy(b => b.BrandName)
                    .Select(b => new { id = b.BrandId, code = b.BrandCode, name = b.BrandName })
                    .ToListAsync(),

                /* Everything this company sells is VIZO, so the new-product
                   form starts there. Looked up by NAME, not id -- nothing in
                   the code should know a row's id -- and null if somebody has
                   renamed or deactivated it, in which case the form simply
                   starts empty. */
                defaultBrandId = await _db.Brands.AsNoTracking()
                    .Where(b => b.IsActive && b.BrandName.ToUpper() == "VIZO")
                    .Select(b => (int?)b.BrandId)
                    .FirstOrDefaultAsync(),
                locations = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive).OrderBy(l => l.LocationName)
                    .Select(l => new
                    {
                        id = l.LocationId,
                        code = l.LocationCode,
                        name = l.LocationName,
                        /* Which town the shelf is in, and what sort of shelf it
                           is. Stock in hand is asked about by CITY -- Karachi
                           has one warehouse and one order desk, Lahore has its
                           own pair -- and without these the screen could only
                           offer a flat list of every location in the company
                           with no way to add up a city's. */
                        kind = l.Kind.KindKey,
                        kindLabel = l.Kind.KindName,
                        cityId = l.CityId,
                        city = l.City.CityName,
                        /* The claim shelf holds stock that is not for sale --
                           damaged goods waiting on a supplier. Counted, but the
                           screen can say so. (There was an In Transit shelf
                           here too until migration 20 removed it: goods on a van
                           belong to neither end of a transfer, and a location
                           for them only ever held a duplicate of the units.) */
                        isSellable = !l.ExcludeFromSellable
                    })
                    .ToListAsync(),

                /* Only the cities that actually have somewhere to keep stock.
                   The "City" table carries every town in Pakistan and 300-odd
                   Chinese ones for the supplier forms; offering all of them in
                   a stock filter would bury the two that matter. */
                stockCities = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive)
                    .Select(l => new { l.CityId, city = l.City.CityName })
                    .Distinct()
                    .OrderBy(c => c.city)
                    .Select(c => new { id = c.CityId, name = c.city })
                    .ToListAsync(),
                adjustmentReasons = await _db.AdjustmentReasons.AsNoTracking()
                    .Select(r => new { id = r.ReasonId, key = r.ReasonKey, name = r.ReasonName })
                    .ToListAsync(),
                movementTypes = await _db.MovementTypes.AsNoTracking()
                    .Select(m => new { id = m.MovementTypeId, key = m.TypeKey, name = m.TypeName })
                    .ToListAsync(),
                transferStatuses = await _db.TransferStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),

                /* Every active product, for the item pickers on the adjustment
                   and transfer forms. Those two screens used to import a
                   hard-coded array from the frontend's src/data/products, so a
                   product created minutes earlier could not be adjusted or
                   transferred at all -- it simply was not in the list. Read
                   live here so the picker is never behind the catalogue.
                   totalStock is the sum across locations; the per-location
                   figure the adjustment form actually needs comes from
                   GET /inventory/stock-levels?locationId=. */
                products = await _db.Products.AsNoTracking()
                    .Where(p => p.IsActive)
                    .OrderBy(p => p.ProductName)
                    .Select(p => new
                    {
                        id = p.ProductId,
                        sku = p.Sku,
                        imageUrl = p.ImageUrl,
                        name = p.ProductName,
                        packing = p.Packing,
                        costPrice = p.CostPrice,
                        salePrice = p.SalePrice,
                        totalStock = p.StockBalances.Sum(b => (int?)b.Quantity) ?? 0
                    })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load inventory lookups");
        }
    }

    // ════════════════════════ validation helpers ════════════════════════

    private async Task<string?> ValidateProduct(ProductRequest b, int? existingId)
    {
        if (string.IsNullOrWhiteSpace(b.Name)) return "Product name is required.";
        if (b.Packing < 1) return "Packing must be at least 1.";
        if (b.MinQty < 0) return "Minimum quantity cannot be negative.";
        if (b.MaxQty < 0) return "Maximum quantity cannot be negative.";
        if (b.MaxQty > 0 && b.MaxQty < b.MinQty)
            return "Maximum quantity cannot be below the minimum.";
        if (b.CostPrice < 0 || b.SalePrice < 0 || b.DutyPrice < 0) return "Prices cannot be negative.";
        if (b.TaxRatePercent is < 0 or > 100) return "Tax rate must be between 0 and 100.";

        /* THE SAME PRODUCT TWICE IS REFUSED -- exactly the same name.

           "Exactly" means what a person reading the shelf label would mean:
           spaces and case do not count, so "VIZO  Titan T9" and "vizo titan
           t9" are the same thing. Anything that actually differs -- a colour,
           a model, a pack size -- is a different product and goes in with a
           SKU of its own, which is what was asked for.

           Checked here and not only in the form, because the form is not the
           only way in and two people can press Save at the same moment. */
        var normal = SkuGenerator.NormalName(b.Name);
        var names = await _db.Products.AsNoTracking()
            .Where(p => existingId == null || p.ProductId != existingId)
            .Select(p => new { p.ProductId, p.ProductName, p.Sku })
            .ToListAsync();
        var twin = names.FirstOrDefault(p => SkuGenerator.NormalName(p.ProductName) == normal);
        if (twin is not null)
            return $"\"{twin.ProductName}\" is already in the catalogue as {twin.Sku}. " +
                   "Change the name -- a different colour or model -- to add it as a separate product.";

        if (!string.IsNullOrWhiteSpace(b.Sku))
        {
            var sku = b.Sku.Trim().ToUpperInvariant();
            if (await _db.Products.AnyAsync(p => p.Sku.ToUpper() == sku &&
                                                 (existingId == null || p.ProductId != existingId)))
                return $"SKU {sku} is already in use.";
        }

        /* A barcode belongs to one product. The column is UNIQUE, so without
           this the save would fail on a constraint name inside a stack trace. */
        var codes = (b.Barcodes ?? new List<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct().ToList();
        if (codes.Count > 0)
        {
            var taken = await _db.ProductBarcodes.AsNoTracking()
                .Where(x => codes.Contains(x.Barcode) && (existingId == null || x.ProductId != existingId))
                .Select(x => new { x.Barcode, x.Product.ProductName })
                .FirstOrDefaultAsync();
            if (taken is not null)
                return $"Barcode {taken.Barcode} already belongs to {taken.ProductName}.";
        }

        if (!await _db.Categories.AnyAsync(c => c.CategoryId == b.CategoryId))
            return "Pick a valid category.";
        if (!await _db.Brands.AnyAsync(x => x.BrandId == b.BrandId))
            return "Pick a valid brand.";

        return null;
    }

    // ══════════════════════════ request bodies ══════════════════════════

    /* Sku is OPTIONAL now. Leave it empty and the server generates one; send
       one only when it came off a scanned barcode (see ResolveSku), or on an
       edit to keep the one the product already has.

       MarginPrice is not accepted: it is SalePrice - CostPrice - DutyPrice,
       worked out here, so the three numbers on the row can never disagree. */
    public record ProductRequest(
        string? Sku, string Name, string? Description, int CategoryId, int BrandId,
        int Packing, int MinQty, int MaxQty,
        decimal CostPrice, decimal DutyPrice, decimal SalePrice, decimal TaxRatePercent,
        bool HideStock, bool IsActive, string? ImageUrl, List<string>? Barcodes);

    public record SkuPreviewRequest(string? Name, int? CategoryId, int? BrandId, List<string>? Barcodes);

    public record CategoryRequest(string Name, int? ParentId, bool IsActive);

    public record BrandRequest(string Code, string Name, string? Description, bool IsActive);

    // ══════════════════════════════════════════════════════════════════
    //  CREATE  --  adjustments and transfers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Corrects the shelf count. The line carries CurrentQty (what the system
    /// thought) and NewQty (what was actually counted); the difference is what
    /// moves. CurrentQty is re-read from StockBalance here rather than trusted
    /// from the browser, because between opening the form and saving it somebody
    /// else may have sold the same item -- writing the client's stale figure back
    /// would silently undo their sale.
    /// </summary>
    [HttpPost("adjustments")]
    public async Task<IActionResult> CreateAdjustment([FromBody] AdjustmentRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "An adjustment needs at least one line." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });
            if (!await _db.AdjustmentReasons.AnyAsync(r => r.ReasonId == body.ReasonId))
                return BadRequest(new { message = "Pick a valid reason." });
            foreach (var l in body.Lines)
            {
                if (l.NewQty < 0) return BadRequest(new { message = "A counted quantity cannot be negative." });
                if (!await _db.Products.AnyAsync(p => p.ProductId == l.ProductId))
                    return BadRequest(new { message = $"Product {l.ProductId} does not exist." });
            }

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can correct stock." });

            var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "POSTED");
            var type = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "ADJUSTMENT");
            if (posted is null || type is null)
                return BadRequest(new { message = "POSTED status or ADJUSTMENT movement type is not configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var adj = new StockAdjustment
            {
                AdjustmentNo = await NextNumber("ADJ"),
                LocationId = body.LocationId,
                AdjustmentDate = body.AdjustmentDate ?? Today(),
                ReasonId = body.ReasonId,
                ReasonNotes = body.ReasonNotes ?? "",
                StatusId = posted.StatusId,
                CreatedByUserId = me.Value
            };
            _db.StockAdjustments.Add(adj);
            await _db.SaveChangesAsync();

            short n = 1;
            var moved = 0;
            foreach (var l in body.Lines)
            {
                var bal = await _db.StockBalances
                    .FirstOrDefaultAsync(s => s.ProductId == l.ProductId && s.LocationId == body.LocationId);
                if (bal is null)
                {
                    bal = new StockBalance { ProductId = l.ProductId, LocationId = body.LocationId, Quantity = 0 };
                    _db.StockBalances.Add(bal);
                    await _db.SaveChangesAsync();
                }

                var current = bal.Quantity;          // the truth, right now
                var delta = l.NewQty - current;

                _db.StockAdjustmentItems.Add(new StockAdjustmentItem
                {
                    AdjustmentId = adj.AdjustmentId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    CurrentQty = current,
                    NewQty = l.NewQty
                });

                if (delta == 0) continue;          // counted the same, nothing to move
                bal.Quantity = l.NewQty;
                moved++;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = body.LocationId,
                    MovementTypeId = type.MovementTypeId,
                    MovedAt = Now(),
                    ReferenceNo = adj.AdjustmentNo,
                    Quantity = delta,
                    BalanceAfter = bal.Quantity,
                    UserId = CurrentUserId()
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("STOCK_ADJUSTED", "StockAdjustment", adj.AdjustmentNo,
                $"{moved} of {body.Lines.Count} lines moved", 3);

            /* The PDF exists the moment the document does. Print and Download
               then hand out the stored Cloudinary file rather than rendering a
               fresh one, so what is on screen is what is in the store. A
               failure here is logged and swallowed -- the document is saved
               either way and the PDF can be rebuilt from the row. */
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "stock-adjustment", adj.AdjustmentId, CurrentUserId());

            /* -- D6 -- ALWAYS reaches Admin, and marked severe.
               Writing stock up or down is the easiest way to cover a theft, so
               this is one of the few that is allowed to buzz a phone. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.StockAdjusted,
                $"Stock corrected by {CurrentUserName()}",
                $"{adj.AdjustmentNo} -- {moved} {(moved == 1 ? "line" : "lines")} changed.",
                url: $"/inventory/adjustments/{adj.AdjustmentId}",
                severe: true);

            return Ok(new
            {
                id = adj.AdjustmentId,
                adjustmentNo = adj.AdjustmentNo,
                linesChanged = moved,
                message = moved == 0
                    ? $"{adj.AdjustmentNo} saved. Every line matched the system count, so no stock moved."
                    : $"{adj.AdjustmentNo} posted. {moved} line(s) corrected."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "post the stock adjustment");
        }
    }

    /// <summary>
    /// Moves stock between two locations. Goods leave the FROM shelf
    /// immediately and are only added to the TO shelf when the receiving end
    /// confirms (POST transfers/{id}/receive) -- stock in a van belongs to
    /// neither shelf, and counting it in both is how a transfer creates
    /// inventory out of nothing.
    /// </summary>
    [HttpPost("transfers")]
    public async Task<IActionResult> CreateTransfer([FromBody] TransferRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "A transfer needs at least one line." });
            if (body.FromLocationId == body.ToLocationId)
                return BadRequest(new { message = "From and To must be different locations." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.FromLocationId))
                return BadRequest(new { message = "Pick a valid source location." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.ToLocationId))
                return BadRequest(new { message = "Pick a valid destination location." });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can move stock." });

            var status = await _db.TransferStatuses.FirstOrDefaultAsync(s => s.StatusKey == "IN_TRANSIT");
            var outType = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "TRANSFER_OUT");
            if (status is null || outType is null)
                return BadRequest(new { message = "IN_TRANSIT status or TRANSFER_OUT movement type is not configured." });

            /* Check every line before moving any of them, so a short line on
               row 5 does not leave rows 1-4 already deducted. */
            foreach (var l in body.Lines)
            {
                if (l.Qty <= 0) return BadRequest(new { message = "Every line needs a quantity above zero." });
                var have = await _db.StockBalances
                    .Where(s => s.ProductId == l.ProductId && s.LocationId == body.FromLocationId)
                    .Select(s => (int?)s.Quantity).FirstOrDefaultAsync() ?? 0;
                if (have < l.Qty)
                {
                    var p = await _db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == l.ProductId);
                    return BadRequest(new
                    {
                        message = $"{p?.ProductName ?? $"Product {l.ProductId}"}: asked for {l.Qty}, only {have} on the source shelf."
                    });
                }
            }

            await using var tx = await _db.Database.BeginTransactionAsync();

            var tr = new StockTransfer
            {
                TransferNo = await NextNumber("TRF"),
                FromLocationId = body.FromLocationId,
                ToLocationId = body.ToLocationId,
                TransferDate = body.TransferDate ?? Today(),
                StatusId = status.StatusId,
                InitiatedByUserId = me.Value,
                ApprovedByUserId = null,
                ReceivedOn = null,
                Notes = body.Notes
            };
            _db.StockTransfers.Add(tr);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                _db.StockTransferItems.Add(new StockTransferItem
                {
                    TransferId = tr.TransferId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty
                });

                var from = await _db.StockBalances
                    .FirstAsync(s => s.ProductId == l.ProductId && s.LocationId == body.FromLocationId);
                from.Quantity -= l.Qty;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = body.FromLocationId,
                    MovementTypeId = outType.MovementTypeId,
                    MovedAt = Now(),
                    ReferenceNo = tr.TransferNo,
                    Quantity = -l.Qty,
                    BalanceAfter = from.Quantity,
                    UserId = CurrentUserId()
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("TRANSFER_SENT", "StockTransfer", tr.TransferNo,
                $"{body.Lines.Count} lines", 2);

            /* The PDF exists the moment the document does. Print and Download
               then hand out the stored Cloudinary file rather than rendering a
               fresh one, so what is on screen is what is in the store. A
               failure here is logged and swallowed -- the document is saved
               either way and the PDF can be rebuilt from the row. */
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "stock-transfer", tr.TransferId, CurrentUserId());

            /* -- A3 and A4 together --
               The mapped flow treats "stock requested" and "stock sent" as two
               separate events with two different audiences. This action is both
               at once: creating a transfer moves the stock out of the source in
               the same call. Separating them needs a status on StockTransfer
               that distinguishes raised from sent, which is a schema change, so
               one notification carries both facts rather than inventing a
               "sent" event that did not happen independently. */
            var locNames = await _db.Locations.AsNoTracking()
                .Where(l => l.LocationId == body.FromLocationId || l.LocationId == body.ToLocationId)
                .ToDictionaryAsync(l => l.LocationId, l => l.LocationName);
            var fromName = locNames.GetValueOrDefault(body.FromLocationId, "a location");
            var toName = locNames.GetValueOrDefault(body.ToLocationId, "a location");
            var lineCount = body.Lines.Count;

            await _push.NotifyRolesAsync(
                new[] { "super-admin", "order-dept" },
                NotificationKinds.TransferSent,
                $"Stock sent by {CurrentUserName()}",
                $"{tr.TransferNo} -- {fromName} to {toName}, {lineCount} " +
                $"{(lineCount == 1 ? "item" : "items")} now in transit.",
                url: $"/inventory/transfers/{tr.TransferId}",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id = tr.TransferId,
                transferNo = tr.TransferNo,
                message = $"{tr.TransferNo} sent. Stock leaves the source now and lands when the destination receives it."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "send the stock transfer");
        }
    }

    /// <summary>The receiving end confirms; this is when stock lands on the TO shelf.</summary>
    [HttpPost("transfers/{id:int}/receive")]
    public async Task<IActionResult> ReceiveTransfer(int id)
    {
        try
        {
            var tr = await _db.StockTransfers
                .Include(t => t.Status)
                .Include(t => t.StockTransferItems)
                .FirstOrDefaultAsync(t => t.TransferId == id);

            if (tr is null) return NotFound(new { message = $"No transfer with id {id}." });
            if (tr.Status.StatusKey == "RECEIVED")
                return BadRequest(new { message = $"{tr.TransferNo} was already received." });
            if (tr.Status.StatusKey is "DRAFT" or "REJECTED")
                return BadRequest(new { message = $"{tr.TransferNo} is {tr.Status.StatusName} and has not been sent." });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can receive a transfer." });

            var received = await _db.TransferStatuses.FirstOrDefaultAsync(s => s.StatusKey == "RECEIVED");
            var inType = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "TRANSFER_IN");
            if (received is null || inType is null)
                return BadRequest(new { message = "RECEIVED status or TRANSFER_IN movement type is not configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            foreach (var l in tr.StockTransferItems)
            {
                var to = await _db.StockBalances
                    .FirstOrDefaultAsync(s => s.ProductId == l.ProductId && s.LocationId == tr.ToLocationId);
                if (to is null)
                {
                    to = new StockBalance { ProductId = l.ProductId, LocationId = tr.ToLocationId, Quantity = 0 };
                    _db.StockBalances.Add(to);
                    await _db.SaveChangesAsync();
                }
                to.Quantity += l.Quantity;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = tr.ToLocationId,
                    MovementTypeId = inType.MovementTypeId,
                    MovedAt = Now(),
                    ReferenceNo = tr.TransferNo,
                    Quantity = l.Quantity,
                    BalanceAfter = to.Quantity,
                    UserId = CurrentUserId()
                });
            }

            tr.StatusId = received.StatusId;
            tr.ReceivedOn = Today();
            tr.ApprovedByUserId = me.Value;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("TRANSFER_RECEIVED", "StockTransfer", tr.TransferNo, null, 2);

            /* -- A5 -- whoever sent it is waiting to hear it arrived. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "order-dept" },
                NotificationKinds.TransferReceived,
                $"Stock received by {CurrentUserName()}",
                $"{tr.TransferNo} has arrived and is on the shelf.",
                url: $"/inventory/transfers/{tr.TransferId}",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{tr.TransferNo} received. Stock is now on the destination shelf." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"receive transfer {id}");
        }
    }

    // ══════════════════════ request bodies (part 2) ═════════════════════

    public record AdjustmentLineRequest(int ProductId, int NewQty);

    public record AdjustmentRequest(
        int LocationId, DateOnly? AdjustmentDate, int ReasonId, string? ReasonNotes,
        List<AdjustmentLineRequest> Lines);

    public record TransferLineRequest(int ProductId, int Qty);

    public record TransferRequest(
        int FromLocationId, int ToLocationId, DateOnly? TransferDate, string? Notes,
        List<TransferLineRequest> Lines);

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

    /// <summary>The product catalogue on the current filter, as a spreadsheet.</summary>
    [HttpGet("products/export")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> ExportProducts(
        [FromQuery] string? q, [FromQuery] int? categoryId, [FromQuery] int? brandId,
        [FromQuery] string? status, [FromQuery] bool includeInactive = true)
    {
        try
        {
            var action = await GetProducts(q, categoryId, brandId, status, includeInactive, 1, 5000);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var columns = new[]
            {
                new XlsxWriter.Column("Code", "sku", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Product", "name", XlsxWriter.CellKind.Text, 38),
                new XlsxWriter.Column("Category", "categoryName", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("Brand", "brandName", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Pack", "packing", XlsxWriter.CellKind.Integer, 8),
                new XlsxWriter.Column("Cost", "costPrice", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Duty", "dutyPrice", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Margin", "marginPrice", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Margin %", "marginPercent", XlsxWriter.CellKind.Percent, 10),
                new XlsxWriter.Column("Sale Price", "salePrice", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Tax %", "taxRatePercent", XlsxWriter.CellKind.Number, 10),
                new XlsxWriter.Column("On Hand", "totalStock", XlsxWriter.CellKind.Integer, 12),
                new XlsxWriter.Column("Min Qty", "minQty", XlsxWriter.CellKind.Integer, 10),
                new XlsxWriter.Column("Max Qty", "maxQty", XlsxWriter.CellKind.Integer, 10),
                new XlsxWriter.Column("Stock Status", "status"),
                new XlsxWriter.Column("Active", "isActive", XlsxWriter.CellKind.Text, 8),
            };

            var bytes = XlsxWriter.FromPayload("Products",
                JsonSerializer.SerializeToElement(ok.Value, ExportJson), columns);
            return File(bytes, XlsxWriter.ContentType, $"products-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "export the products");
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
