using System.Globalization;

namespace vizo_backend.Documents;

/// <summary>
/// Renders one sale invoice onto a <see cref="PdfCanvas"/>.
///
/// Nothing on this page is invented. Every field is passed in from the row the
/// API just read -- the letterhead comes from the "Company" table, the buyer
/// from "Party", the lines from "SalesInvoiceItem". If a value is missing in
/// the database its line is left out rather than filled with a plausible
/// placeholder, because a bill is the one document nobody ever re-checks.
///
/// It is a static renderer, not a service: no interface, nothing in DI,
/// consistent with the rest of this codebase.
/// </summary>
public static class InvoicePdf
{
    /* Brand palette, the same hexes the web app uses (globals.css). */
    /* ─────────────────────────── THE PALETTE ───────────────────────────

        Cleaner and simpler than it was, without becoming a different company's
        invoice. The brand navy and the brand yellow both stay; what changed is
        how much of the page they cover.

          - The navy was #031833, which is very nearly black. On a laser printer
            a full-width band of it comes out as a slab of toner with the white
            text struggling inside it. #0B2545 is unmistakably the same colour
            family and reads as navy rather than as a blackout.
          - The table header was a SECOND navy, almost but not quite the header
            band's. Two nearly-identical darks on one page look like a mistake
            rather than a decision, so the header is now one clear step lighter
            and deliberately different.
          - Zebra striping and the panel fill were #F7F9FC and #F0F4FA -- close
            enough to be indistinguishable on screen and invisible in print.
            The stripe is now the faintest of tints and the panel a definite
            one, so each is doing a job.
          - The yellow is unchanged but is now used only as a 2-3pt accent rule.
            It was never meant to be a fill; it is a highlighter.               */

    private const string Navy = "#0B2545";       // header band
    private const string NavySoft = "#1B3A66";   // table header, one step lighter
    private const string Yellow = "#EDC705";     // accent rules only
    private const string Ink = "#111827";
    private const string Muted = "#5B6B80";
    private const string Faint = "#93A1B5";
    private const string Hair = "#E6EBF1";
    private const string ZebraFill = "#F6F8FB";
    private const string PanelFill = "#EDF2F9";
    private const string Success = "#047857";
    private const string Danger = "#B91C1C";
    private const string White = "#FFFFFF";
    private const string OnNavy = "#C3D3E9";

    private const double Left = 42;
    private const double Right = 553.28;

    /* Column right edges / left edges for the line table. */
    private const double ColNo = 52;      // centred
    private const double ColDesc = 68;
    private const double ColPack = 300;   // right -- packing count, before qty
    private const double ColQty = 344;    // right
    private const double ColRate = 410;   // right
    private const double ColDisc = 452;   // right
    private const double ColTax = 492;    // right
    private const double ColAmount = Right; // right

    public sealed record Line(
        int LineNo, string Name, string? Sku, int Packing,
        int Qty, decimal Rate, decimal DiscountPercent, decimal TaxPercent, decimal LineTotal);

    /// <summary>
    /// One line of the appended "Dispatching" page -- what was asked for
    /// against what actually left the shelf. Added 23 September for the
    /// Packing screen; see SalesController.RebuildBillWithDispatch.
    /// </summary>
    public sealed record DispatchLine(string Name, string? Sku, int Requested, int Dispatched);

    public sealed record Data(
        // seller -- straight off the "Company" row
        string CompanyName, string CompanyLegalName, string CompanyAddress, string CompanyCity,
        string CompanyCountry, string CompanyPhone, string CompanyEmail,
        string CompanyNtn, string CompanyStrn, string CurrencySymbol,
        // the document
        string InvoiceNo, DateOnly InvoiceDate, DateOnly DueDate,
        string? OrderNo, string PaymentMethod, string LocationName, string StatusName,
        // the buyer
        string CustomerName, string? CustomerCode, string? CustomerAddress,
        string? CustomerCity, string? CustomerPhone, string? CustomerNtn, bool IsWalkIn,
        // money
        decimal Subtotal, decimal Discount, decimal Tax, decimal Total,
        decimal Paid, decimal Balance,
        string? PreparedBy, string? Notes,
        IReadOnlyList<Line> Lines,
        // whoever wrote the order this invoice came from
        string? Salesman = null,
        /* Present only when this invoice has been through the Packing screen.
           Appends one page after the ordinary invoice pages -- the bill itself
           is never rewritten, because it is what the customer was actually
           charged; this is the separate record of what physically went out. */
        IReadOnlyList<DispatchLine>? DispatchManifest = null,
        DateOnly? DispatchedOn = null);

    public static byte[] Render(Data d)
    {
        var pdf = new PdfCanvas();

        /* Split the lines across pages before drawing anything, so the footer
           can honestly say "Page 1 of 3". */
        const int firstPageRows = 12;
        const int laterPageRows = 22;
        var pages = Paginate(d.Lines, firstPageRows, laterPageRows);

        var hasManifest = d.DispatchManifest is { Count: > 0 };
        var pageCount = pages.Count + (hasManifest ? 1 : 0);

        for (var p = 0; p < pages.Count; p++)
        {
            if (p > 0) pdf.NewPage();

            var y = p == 0 ? DrawFirstPageHead(pdf, d) : DrawContinuationHead(pdf, d, p + 1);
            y = DrawTable(pdf, d, pages[p], y, startingLineNo: pages.Take(p).Sum(x => x.Count) + 1);

            var last = p == pages.Count - 1;
            if (last) DrawTotals(pdf, d, y);

            DrawFoot(pdf, d, p + 1, pageCount);
        }

        if (hasManifest)
        {
            pdf.NewPage();
            DrawDispatchPage(pdf, d);
            DrawFoot(pdf, d, pageCount, pageCount);
        }

        return pdf.Build();
    }

    /* ─────────────────────── the dispatch record ─────────────────────── */

    /// <summary>
    /// One extra page: what was ordered against what actually left the shelf.
    /// Drawn in the same palette and column rhythm as the invoice table so it
    /// reads as part of the same document, not a pasted-in report.
    /// </summary>
    private static void DrawDispatchPage(PdfCanvas pdf, Data d)
    {
        var manifest = d.DispatchManifest!;
        var anyShort = manifest.Any(m => m.Dispatched < m.Requested);

        const double bandTop = PdfCanvas.A4Height;
        const double bandHeight = 60;
        var bandBottom = bandTop - bandHeight;

        pdf.Rect(0, bandBottom, PdfCanvas.A4Width, bandHeight, Navy);
        pdf.Rect(0, bandBottom - 5, PdfCanvas.A4Width, 5, Yellow);

        pdf.Text(Left, bandBottom + 34, "DISPATCH RECORD", 15, White, bold: true);
        pdf.Text(Left, bandBottom + 16, "Against invoice " + d.InvoiceNo, 9, OnNavy);
        pdf.TextRight(Right, bandBottom + 34, Day(d.DispatchedOn ?? d.InvoiceDate), 10, Yellow, bold: true);
        pdf.TextRight(Right, bandBottom + 16,
            anyShort ? "SENT SHORT OF WHAT WAS ORDERED" : "SENT IN FULL", 9,
            anyShort ? Yellow : OnNavy, bold: anyShort);

        var y = bandBottom - 24;

        /* The same idea as InvoicePdf's own table, moved left since there is
           no rate or tax on this page -- only counts. */
        const double colName = ColDesc;
        const double colReq = 380;
        const double colSent = 460;
        const double colShort = Right;

        const double headHeight = 21;
        pdf.Rect(Left, y - headHeight, Right - Left, headHeight, NavySoft);
        var ty = y - headHeight + 7;
        pdf.TextCenter(ColNo, ty, "#", 7.5, White, bold: true);
        pdf.Text(colName, ty, "ITEM", 7.5, White, bold: true);
        pdf.TextRight(colReq, ty, "REQUESTED", 7.5, White, bold: true);
        pdf.TextRight(colSent, ty, "DISPATCHED", 7.5, White, bold: true);
        pdf.TextRight(colShort, ty, "SHORT BY", 7.5, White, bold: true);
        y -= headHeight;

        const double rowHeight = 25;
        var n = 1;
        var totalReq = 0;
        var totalSent = 0;

        foreach (var m in manifest)
        {
            var rowBottom = y - rowHeight;
            var shortBy = m.Requested - m.Dispatched;
            if (n % 2 == 0) pdf.Rect(Left, rowBottom, Right - Left, rowHeight, ZebraFill);

            pdf.TextCenter(ColNo, rowBottom + 10, n.ToString(), 8, Faint);
            var nameMax = colReq - colName - 20;
            pdf.Text(colName, rowBottom + 14, pdf.Ellipsis(m.Name, 8.6, nameMax), 8.6, Ink);
            pdf.Text(colName, rowBottom + 4, pdf.Ellipsis(m.Sku ?? "", 7.2, nameMax), 7.2, Faint);

            pdf.TextRight(colReq, rowBottom + 10, m.Requested.ToString("N0", Pk), 8.6, Ink);
            pdf.TextRight(colSent, rowBottom + 10, m.Dispatched.ToString("N0", Pk), 8.6, Ink, bold: true);
            pdf.TextRight(colShort, rowBottom + 10, shortBy > 0 ? shortBy.ToString("N0", Pk) : "-", 8.6,
                shortBy > 0 ? Danger : Faint, bold: shortBy > 0);

            pdf.Line(Left, rowBottom, Right, rowBottom, Hair, 0.5);

            totalReq += m.Requested;
            totalSent += m.Dispatched;
            y = rowBottom;
            n++;
        }

        y -= 14;
        pdf.TextRight(colReq, y, totalReq.ToString("N0", Pk), 9, Muted, bold: true);
        pdf.TextRight(colSent, y, totalSent.ToString("N0", Pk), 9, Ink, bold: true);
        if (totalReq > totalSent)
            pdf.TextRight(colShort, y, (totalReq - totalSent).ToString("N0", Pk), 9, Danger, bold: true);

        y -= 24;
        pdf.Text(Left, y,
            anyShort
                ? "Some items were not on hand in full. The salesperson, the accountant and the Super Admin were notified when this was dispatched."
                : "Every item on the invoice was dispatched in full.",
            8, Muted);
    }

    private static List<List<Line>> Paginate(IReadOnlyList<Line> lines, int first, int rest)
    {
        var pages = new List<List<Line>>();
        var i = 0;
        while (i < lines.Count || pages.Count == 0)
        {
            var take = pages.Count == 0 ? first : rest;
            pages.Add(lines.Skip(i).Take(take).ToList());
            i += take;
            if (i >= lines.Count) break;
        }
        return pages;
    }

    /* ─────────────────────────── page head ─────────────────────────── */

    private static double DrawFirstPageHead(PdfCanvas pdf, Data d)
    {
        const double bandTop = PdfCanvas.A4Height;
        const double bandHeight = 78;
        var bandBottom = bandTop - bandHeight;

        pdf.Rect(0, bandBottom, PdfCanvas.A4Width, bandHeight, Navy);
        pdf.Rect(0, bandBottom - 5, PdfCanvas.A4Width, 5, Yellow);

        /* Logo mark: the initial of the trading name in a yellow tile. No image
           is embedded -- a raster logo would triple the file size and the shop
           prints these in mono anyway. */
        pdf.Rect(Left, bandBottom + 26, 30, 30, Yellow);
        pdf.TextCenter(Left + 15, bandBottom + 35,
            d.CompanyName.Length > 0 ? d.CompanyName[..1].ToUpperInvariant() : "V", 18, Navy, bold: true);

        pdf.Text(Left + 40, bandBottom + 44, d.CompanyName, 16, White, bold: true);
        pdf.Text(Left + 40, bandBottom + 31, d.CompanyLegalName, 8.5, OnNavy);

        pdf.TextRight(Right, bandBottom + 46, "SALES TAX INVOICE", 13, Yellow, bold: true);
        pdf.TextRight(Right, bandBottom + 31, d.InvoiceNo, 11.5, White, bold: true);
        pdf.TextRight(Right, bandBottom + 18, d.StatusName.ToUpperInvariant(), 7.5, OnNavy, bold: true);

        /* ── seller block, left ── */
        var y = bandBottom - 24;
        pdf.Text(Left, y, "FROM", 7, Faint, bold: true);
        y -= 13;
        pdf.Text(Left, y, d.CompanyLegalName, 9.5, Ink, bold: true);
        y -= 12;
        foreach (var row in SellerLines(d))
        {
            pdf.Text(Left, y, row, 8.2, Muted);
            y -= 11;
        }

        /* The rep who wrote the order, directly under the seller block. The
           customer rings a person, not a company, and the shop needs to know
           whose sale it was without going back to the system. */
        if (!string.IsNullOrWhiteSpace(d.Salesman))
        {
            y -= 3;
            pdf.Text(Left, y, "SALESMAN:", 7, Faint, bold: true);
            pdf.Text(Left + 46, y, d.Salesman!, 8.6, Ink, bold: true);
            y -= 12;
        }

        /* ── document meta panel, right ── */
        var meta = new List<(string, string)>
        {
            ("Invoice No", d.InvoiceNo),
            ("Invoice Date", Day(d.InvoiceDate)),
            ("Due Date", Day(d.DueDate)),
        };
        if (!string.IsNullOrWhiteSpace(d.OrderNo)) meta.Add(("Order Ref", d.OrderNo!));
        meta.Add(("Payment", Pretty(d.PaymentMethod)));
        meta.Add(("Issued From", d.LocationName));

        const double panelLeft = 348;
        const double panelWidth = Right - panelLeft;
        var panelTop = bandBottom - 18;
        var panelHeight = 16 + meta.Count * 13;
        pdf.Rect(panelLeft, panelTop - panelHeight, panelWidth, panelHeight, PanelFill);
        pdf.Rect(panelLeft, panelTop - panelHeight, 2.5, panelHeight, Yellow);

        var my = panelTop - 17;
        foreach (var (label, value) in meta)
        {
            pdf.Text(panelLeft + 12, my, label, 7.8, Muted);
            pdf.TextRight(Right - 10, my, value, 8.4, Ink, bold: true);
            my -= 13;
        }

        /* ── bill-to panel, full width ── */
        var billTop = Math.Min(y, panelTop - panelHeight) - 12;
        const double billHeight = 62;
        pdf.Rect(Left, billTop - billHeight, Right - Left, billHeight, ZebraFill);
        pdf.Rect(Left, billTop - billHeight, 3, billHeight, Yellow);

        pdf.Text(Left + 14, billTop - 15, d.IsWalkIn ? "BILL TO (WALK-IN)" : "BILL TO", 7, Faint, bold: true);
        pdf.Text(Left + 14, billTop - 30, d.CustomerName, 11, Navy, bold: true);

        var buyer = BuyerLines(d);
        var by = billTop - 43;
        foreach (var row in buyer.Take(2))
        {
            pdf.Text(Left + 14, by, row, 8.2, Muted);
            by -= 11;
        }

        return billTop - billHeight - 22;
    }

    private static double DrawContinuationHead(PdfCanvas pdf, Data d, int pageNo)
    {
        const double bandTop = PdfCanvas.A4Height;
        const double bandHeight = 44;
        var bandBottom = bandTop - bandHeight;

        pdf.Rect(0, bandBottom, PdfCanvas.A4Width, bandHeight, Navy);
        pdf.Rect(0, bandBottom - 4, PdfCanvas.A4Width, 4, Yellow);

        pdf.Text(Left, bandBottom + 17, d.CompanyName, 12, White, bold: true);
        pdf.TextRight(Right, bandBottom + 24, $"{d.InvoiceNo}  (continued)", 10, Yellow, bold: true);
        pdf.TextRight(Right, bandBottom + 11, d.CustomerName, 8, OnNavy);

        return bandBottom - 30;
    }

    /* ─────────────────────────── line table ─────────────────────────── */

    private static double DrawTable(PdfCanvas pdf, Data d, List<Line> lines, double y, int startingLineNo)
    {
        const double headHeight = 21;
        pdf.Rect(Left, y - headHeight, Right - Left, headHeight, NavySoft);

        var ty = y - headHeight + 7;
        pdf.TextCenter(ColNo, ty, "#", 7.5, White, bold: true);
        pdf.Text(ColDesc, ty, "DESCRIPTION", 7.5, White, bold: true);
        pdf.TextRight(ColPack, ty, "PACKING", 7.5, White, bold: true);
        pdf.TextRight(ColQty, ty, "QTY", 7.5, White, bold: true);
        pdf.TextRight(ColRate, ty, "RATE", 7.5, White, bold: true);
        pdf.TextRight(ColDisc, ty, "DISC%", 7.5, White, bold: true);
        pdf.TextRight(ColTax, ty, "TAX%", 7.5, White, bold: true);
        pdf.TextRight(ColAmount, ty, "AMOUNT", 7.5, White, bold: true);

        y -= headHeight;

        const double rowHeight = 25;
        var n = startingLineNo;

        foreach (var l in lines)
        {
            var rowBottom = y - rowHeight;
            if (n % 2 == 0) pdf.Rect(Left, rowBottom, Right - Left, rowHeight, ZebraFill);

            pdf.TextCenter(ColNo, rowBottom + 10, n.ToString(), 8, Faint);

            var nameMax = ColPack - ColDesc - 20;
            pdf.Text(ColDesc, rowBottom + 14, pdf.Ellipsis(l.Name, 8.6, nameMax), 8.6, Ink);

            /* The SKU alone now. "pack of N" used to be tacked on here; it has
               its own column, and saying it twice on one line is noise. */
            pdf.Text(ColDesc, rowBottom + 4, pdf.Ellipsis(l.Sku ?? "", 7.2, nameMax), 7.2, Faint);

            /* Product.Packing is how many units are in a carton, so 0 or 1
               means the item is sold loose and there is no pack to count. */
            pdf.TextRight(ColPack, rowBottom + 10,
                l.Packing > 1 ? l.Packing.ToString("N0", Pk) : "-", 8.6,
                l.Packing > 1 ? Ink : Faint);

            pdf.TextRight(ColQty, rowBottom + 10, l.Qty.ToString("N0", Pk), 8.6, Ink);
            pdf.TextRight(ColRate, rowBottom + 10, Money(l.Rate), 8.6, Ink);
            pdf.TextRight(ColDisc, rowBottom + 10, l.DiscountPercent == 0 ? "-" : Pct(l.DiscountPercent), 8.2, Muted);
            pdf.TextRight(ColTax, rowBottom + 10, l.TaxPercent == 0 ? "-" : Pct(l.TaxPercent), 8.2, Muted);
            pdf.TextRight(ColAmount, rowBottom + 10, Money(l.LineTotal), 8.8, Ink, bold: true);

            pdf.Line(Left, rowBottom, Right, rowBottom, Hair, 0.5);

            y = rowBottom;
            n++;
        }

        return y;
    }

    /* ───────────────────────────── totals ───────────────────────────── */

    private static void DrawTotals(PdfCanvas pdf, Data d, double y)
    {
        const double boxLeft = 330;
        const double rowStep = 14;
        var cur = d.CurrencySymbol;

        /* The panel is MEASURED before anything is drawn. Painting a filled
           rectangle after the rows and trusting it to land below them is how
           the last line item ended up hidden underneath the subtotal. */
        var summaryRows = 2 + (d.Discount > 0 ? 1 : 0);
        var panelHeight = 10 + summaryRows * rowStep;

        var top = y - 16;               // panel top: clear of the last table row
        var line = top - 16;            // first baseline inside it

        pdf.Rect(boxLeft, top - panelHeight, Right - boxLeft, panelHeight, PanelFill);

        void Pair(string label, decimal value, string colour = Ink, bool bold = false, string prefix = "")
        {
            pdf.Text(boxLeft + 10, line, label, 8.4, Muted);
            pdf.TextRight(Right - 10, line, prefix + cur + " " + Money(value), 8.8, colour, bold);
            line -= rowStep;
        }

        Pair("Subtotal", d.Subtotal);
        if (d.Discount > 0) Pair("Discount", d.Discount, Danger, prefix: "- ");
        Pair("Sales Tax", d.Tax);

        /* The one number the shop actually reads. */
        const double bandHeight = 26;
        var bandTop = top - panelHeight - 6;
        pdf.Rect(boxLeft, bandTop - bandHeight, Right - boxLeft, bandHeight, Navy);
        pdf.Text(boxLeft + 10, bandTop - 17, "TOTAL", 10, Yellow, bold: true);
        pdf.TextRight(Right - 10, bandTop - 18, cur + " " + Money(d.Total), 13, White, bold: true);

        if (d.Paid > 0)
        {
            line = bandTop - bandHeight - 16;
            Pair("Paid", d.Paid, Success);
            Pair("Balance", d.Balance, d.Balance > 0 ? Danger : Success, bold: true);
        }

        /* Amount in words, in the empty column to the left of the totals. */
        var wordsY = pdf.TextWrapped(Left, top - 10, "AMOUNT IN WORDS", 7, boxLeft - Left - 16, Faint, bold: true);
        wordsY = pdf.TextWrapped(Left, wordsY - 2, Words(d.Total), 8.4, boxLeft - Left - 16, Ink);

        if (!string.IsNullOrWhiteSpace(d.Notes))
            pdf.TextWrapped(Left, wordsY - 8, "Note: " + d.Notes, 7.6, boxLeft - Left - 16, Muted);
    }

    /* ───────────────────────────── footer ───────────────────────────── */

    private static void DrawFoot(PdfCanvas pdf, Data d, int pageNo, int pageCount)
    {
        const double y = 62;
        pdf.Line(Left, y, Right, y, Hair, 0.7);

        pdf.Text(Left, y - 14,
            $"Payment due {Day(d.DueDate)}. Goods once sold are accepted back only in resalable condition, within 7 days, against this invoice.",
            7.2, Muted);
        pdf.Text(Left, y - 24,
            $"{d.CompanyLegalName}  ·  NTN {d.CompanyNtn}  ·  STRN {d.CompanyStrn}  ·  {d.CompanyPhone}  ·  {d.CompanyEmail}",
            7.2, Faint);

        pdf.Text(Left, y - 38,
            string.IsNullOrWhiteSpace(d.PreparedBy)
                ? "This is a computer-generated invoice and needs no signature."
                : $"Prepared by {d.PreparedBy}.  This is a computer-generated invoice and needs no signature.",
            7, Faint);

        pdf.TextRight(Right, y - 38, $"Page {pageNo} of {pageCount}", 7, Faint);
        pdf.Rect(0, 0, PdfCanvas.A4Width, 5, Yellow);
    }

    /* ──────────────────────────── helpers ──────────────────────────── */

    private static readonly CultureInfo Pk = CultureInfo.GetCultureInfo("en-US");

    private static IEnumerable<string> SellerLines(Data d)
    {
        if (!string.IsNullOrWhiteSpace(d.CompanyAddress)) yield return d.CompanyAddress;
        var place = string.Join(", ", new[] { d.CompanyCity, d.CompanyCountry }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (place.Length > 0) yield return place;

        var contact = string.Join("   ", new[]
        {
            string.IsNullOrWhiteSpace(d.CompanyPhone) ? null : d.CompanyPhone,
            string.IsNullOrWhiteSpace(d.CompanyEmail) ? null : d.CompanyEmail
        }.Where(s => s is not null)!);
        if (contact.Length > 0) yield return contact;

        var tax = string.Join("   ", new[]
        {
            string.IsNullOrWhiteSpace(d.CompanyNtn) ? null : $"NTN {d.CompanyNtn}",
            string.IsNullOrWhiteSpace(d.CompanyStrn) ? null : $"STRN {d.CompanyStrn}"
        }.Where(s => s is not null)!);
        if (tax.Length > 0) yield return tax;
    }

    private static List<string> BuyerLines(Data d)
    {
        var rows = new List<string>();

        var one = string.Join("   ", new[]
        {
            string.IsNullOrWhiteSpace(d.CustomerCode) ? null : d.CustomerCode,
            string.IsNullOrWhiteSpace(d.CustomerAddress) ? null : d.CustomerAddress,
            string.IsNullOrWhiteSpace(d.CustomerCity) ? null : d.CustomerCity
        }.Where(s => s is not null)!);
        if (one.Length > 0) rows.Add(one);

        var two = string.Join("   ", new[]
        {
            string.IsNullOrWhiteSpace(d.CustomerPhone) ? null : $"Phone {d.CustomerPhone}",
            string.IsNullOrWhiteSpace(d.CustomerNtn) ? null : $"NTN {d.CustomerNtn}"
        }.Where(s => s is not null)!);
        if (two.Length > 0) rows.Add(two);

        return rows;
    }

    private static string Money(decimal v) => v.ToString("N2", Pk);
    private static string Pct(decimal v) => v == Math.Floor(v) ? $"{v:0}%" : $"{v:0.##}%";
    private static string Day(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    private static string Pretty(string key) =>
        key switch
        {
            "CASH" => "Cash",
            "BANK" => "Bank transfer",
            "CREDIT" => "On account",
            "CHEQUE" => "Cheque",
            "JAZZCASH" => "JazzCash",
            "EASYPAISA" => "Easypaisa",
            "CREDIT_NOTE" => "Credit note",
            "PETTY_CASH" => "Petty cash",
            _ => key
        };

    /* ── amount in words ──
       Pakistani convention: lakh and crore, not million. An accountant reading
       "one crore twelve lakh" checks it against the figure in a second; "eleven
       million two hundred thousand" makes them stop and count zeros. */

    private static readonly string[] Ones =
    {
        "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
        "seventeen", "eighteen", "nineteen"
    };
    private static readonly string[] Tens =
    {
        "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"
    };

    private static string UnderHundred(long n) =>
        n < 20 ? Ones[n]
        : n % 10 == 0 ? Tens[n / 10]
        : $"{Tens[n / 10]}-{Ones[n % 10]}";

    private static string UnderThousand(long n) =>
        n < 100 ? UnderHundred(n)
        : n % 100 == 0 ? $"{Ones[n / 100]} hundred"
        : $"{Ones[n / 100]} hundred {UnderHundred(n % 100)}";

    public static string Words(decimal amount)
    {
        var whole = (long)Math.Floor(Math.Abs(amount));
        var paisa = (int)Math.Round((Math.Abs(amount) - whole) * 100, MidpointRounding.AwayFromZero);
        if (paisa == 100) { whole++; paisa = 0; }

        var parts = new List<string>();

        void Chunk(long divisor, string name)
        {
            var q = whole / divisor;
            if (q <= 0) return;
            parts.Add($"{UnderThousand(q)} {name}");
            whole %= divisor;
        }

        Chunk(10_000_000, "crore");
        Chunk(100_000, "lakh");
        Chunk(1_000, "thousand");
        if (whole > 0) parts.Add(UnderThousand(whole));

        var rupees = parts.Count == 0 ? "zero" : string.Join(" ", parts);
        var text = $"Rupees {rupees}";
        if (paisa > 0) text += $" and {UnderHundred(paisa)} paisa";
        text += " only";

        if (amount < 0) text = "Minus " + text;
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
