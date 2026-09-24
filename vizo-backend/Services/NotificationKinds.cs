namespace vizo_backend.Services;

/// <summary>
/// Every kind of notification this application sends, in one list.
///
/// Two things depend on this being the single source:
///
///   1. The per-user on/off screen is built from it. A kind that is sent but
///      not listed here is one nobody can switch off.
///   2. The Kind string is stored in NotificationPreference. Renaming one
///      silently switches it back on for everybody who had turned it off, so
///      treat these as permanent once shipped.
///
/// The `Severe` flag is the one that decides whether a phone vibrates. Keep it
/// for money and exceptions only. Admin is on the receiving end of all forty of
/// these, and an Admin whose phone buzzes for every counter sale turns
/// notifications off within two days -- taking the credit-limit alert with them.
/// </summary>
public static class NotificationKinds
{
    public record Kind(string Key, string Group, string Label, string Description, bool Severe = false);

    // ─────────────────────────── A. the sale journey ───────────────────────
    public const string OrderCreated       = "ORDER_CREATED";
    public const string OrderConfirmed     = "ORDER_CONFIRMED";
    public const string TransferRequested  = "TRANSFER_REQUESTED";
    public const string TransferSent       = "TRANSFER_SENT";
    public const string TransferReceived   = "TRANSFER_RECEIVED";
    public const string OrderPacked        = "ORDER_PACKED";
    public const string OrderDispatched    = "ORDER_DISPATCHED";
    /// <summary>
    /// The Packing screen sent less of something than the salesperson ordered.
    /// Added 23 September; goes to the Super Admin, the accountant and the
    /// salesperson who wrote the order, by the owner's own words.
    /// </summary>
    public const string DispatchShortage   = "DISPATCH_SHORTAGE";
    public const string OrderDelivered     = "ORDER_DELIVERED";
    public const string CodSettled         = "COD_SETTLED";

    // ─────────────────────────── B. sales edge cases ───────────────────────
    public const string CreditHold         = "CREDIT_HOLD";
    public const string CreditHoldCleared  = "CREDIT_HOLD_CLEARED";
    public const string InvoiceRaised      = "INVOICE_RAISED";
    public const string InvoiceDirect      = "INVOICE_DIRECT";
    public const string CounterSale        = "COUNTER_SALE";
    public const string ReturnRequested    = "RETURN_REQUESTED";
    public const string ReturnDecided      = "RETURN_DECIDED";

    // ─────────────────────────── C. accounting ─────────────────────────────
    public const string ExpenseCreated     = "EXPENSE_CREATED";
    public const string ExpenseDecided     = "EXPENSE_DECIDED";
    public const string ExpenseReversed    = "EXPENSE_REVERSED";
    public const string JournalPosted      = "JOURNAL_POSTED";
    public const string JournalReversed    = "JOURNAL_REVERSED";
    public const string VoucherPosted      = "VOUCHER_POSTED";
    public const string VoucherCancelled   = "VOUCHER_CANCELLED";
    public const string CollectionConfirmed = "COLLECTION_CONFIRMED";
    public const string PeriodChanged      = "PERIOD_CHANGED";

    // ─────────────────────────── D. purchasing and stock ───────────────────
    public const string PoCreated          = "PO_CREATED";
    public const string PoApproved         = "PO_APPROVED";
    public const string GrnCreated         = "GRN_CREATED";
    public const string PurchaseInvoice    = "PURCHASE_INVOICE";
    public const string PurchaseReturn     = "PURCHASE_RETURN";
    public const string StockAdjusted      = "STOCK_ADJUSTED";
    public const string LowStock           = "LOW_STOCK";

    /* The catalogue. A product appearing, changing price, or a category or
       brand being added is somebody changing what the whole company sells --
       the owner asked to be told, by name, who did it and to what. */
    public const string ProductAdded       = "PRODUCT_ADDED";
    public const string ProductChanged     = "PRODUCT_CHANGED";
    public const string CatalogChanged     = "CATALOG_CHANGED";

    // ─────────────────────────── E. claims and delivery ────────────────────
    public const string ClaimCreated       = "CLAIM_CREATED";
    public const string ClaimSent          = "CLAIM_SENT";
    public const string ClaimReminded      = "CLAIM_REMINDED";
    public const string ClaimSettled       = "CLAIM_SETTLED";

    // ─────────────────────────── F. setup and security ─────────────────────
    /* Customers and suppliers. A rep opening an account, or changing a
       credit-relevant detail on one, is the owner's business. */
    public const string PartyAdded         = "PARTY_ADDED";
    public const string PartyChanged       = "PARTY_CHANGED";

    public const string UserChanged        = "USER_CHANGED";
    public const string RoleChanged        = "ROLE_CHANGED";
    public const string BackupDone         = "BACKUP_DONE";
    public const string LoginBlocked       = "LOGIN_BLOCKED";

    // ─────────────────────────── other ─────────────────────────────────────
    public const string Anomaly            = "ANOMALY";
    public const string Test               = "TEST";

    public static readonly IReadOnlyList<Kind> All = new List<Kind>
    {
        new(OrderCreated,       "Orders", "New order",            "A salesperson has taken an order."),
        new(OrderConfirmed,     "Orders", "Order confirmed",      "The order department has accepted an order."),
        new(OrderPacked,        "Orders", "Order packed",         "An order is packed and ready to send."),
        new(OrderDispatched,    "Orders", "Order dispatched",     "An order has left with a courier."),
        new(DispatchShortage,   "Orders", "Sent short",            "The order department sent less of something than was ordered.", Severe: true),
        new(OrderDelivered,     "Orders", "Order delivered",      "An order reached the customer."),

        new(CreditHold,         "Money",  "Credit limit crossed", "An order is stuck because the customer is over their limit.", Severe: true),
        new(CreditHoldCleared,  "Money",  "Limit cleared",        "Somebody released an order that was over its limit."),
        new(CodSettled,         "Money",  "COD received",         "Cash-on-delivery money has come in.", Severe: true),
        new(VoucherPosted,      "Money",  "Payment received",     "A receipt or payment was posted to the ledger."),
        new(VoucherCancelled,   "Money",  "Payment cancelled",    "A posted receipt or payment was cancelled -- the invoice is owing again.", Severe: true),
        new(CollectionConfirmed,"Money",  "Collection confirmed", "Money a rep collected has been confirmed by accounts."),

        new(InvoiceRaised,      "Invoices", "Invoice raised",     "An invoice was generated for an order."),
        new(InvoiceDirect,      "Invoices", "Direct invoice",     "An invoice was created without an order."),
        new(CounterSale,        "Invoices", "Counter sale",       "A walk-in sale was rung up. These happen many times a day."),

        new(ReturnRequested,    "Returns", "Return requested",    "A customer wants to send something back."),
        new(ReturnDecided,      "Returns", "Return decided",      "A return was approved or refused."),

        new(ExpenseCreated,     "Accounting", "Expense filed",    "Somebody has filed an expense for approval."),
        new(ExpenseDecided,     "Accounting", "Expense decided",  "An expense was approved or rejected."),
        new(ExpenseReversed,    "Accounting", "Expense reversed", "A posted expense was undone.", Severe: true),
        new(JournalPosted,      "Accounting", "Entry posted",     "A journal entry went into the ledger."),
        new(JournalReversed,    "Accounting", "Entry reversed",   "A posted journal entry was undone.", Severe: true),
        new(PeriodChanged,      "Accounting", "Period opened or closed", "A fiscal period was closed or reopened."),

        new(TransferRequested,  "Stock", "Stock requested",       "One location has asked another for stock."),
        new(TransferSent,       "Stock", "Stock sent",            "A stock transfer is on its way."),
        new(TransferReceived,   "Stock", "Stock received",        "A stock transfer arrived."),
        new(StockAdjusted,      "Stock", "Stock corrected",       "Recorded stock was changed to match a count.", Severe: true),
        new(LowStock,           "Stock", "Running out",           "Items have fallen below their minimum. Sent once a day."),

        new(ProductAdded,       "Catalogue", "Item added",        "Somebody put a new item in the catalogue."),
        new(ProductChanged,     "Catalogue", "Item changed",      "An item's price, tax or details were edited."),
        new(CatalogChanged,     "Catalogue", "Category or brand changed", "A category or brand was added, renamed or removed."),

        new(PartyAdded,         "Customers", "Account opened",    "A customer or supplier account was created."),
        new(PartyChanged,       "Customers", "Account changed",   "A customer or supplier's details were edited."),

        new(PoCreated,          "Purchasing", "Purchase order raised", "A purchase order needs approval."),
        new(PoApproved,         "Purchasing", "Purchase order approved", "A purchase order can go to the supplier."),
        new(GrnCreated,         "Purchasing", "Goods received",   "Stock has arrived from a supplier."),
        new(PurchaseInvoice,    "Purchasing", "Supplier bill",    "A supplier's invoice was entered."),
        new(PurchaseReturn,     "Purchasing", "Return to supplier", "Something is going back to a supplier."),

        new(ClaimCreated,       "Claims", "Claim raised",         "A damaged or faulty item was booked in."),
        new(ClaimSent,          "Claims", "Claim sent",           "A claim went to the supplier."),
        new(ClaimReminded,      "Claims", "Claim chased",         "A supplier was reminded about an open claim."),
        new(ClaimSettled,       "Claims", "Claim settled",        "A claim was credited, replaced or written off."),

        new(UserChanged,        "Setup", "User changed",          "Somebody was added, edited or deactivated."),
        new(RoleChanged,        "Setup", "Permissions changed",   "A role gained or lost a permission.", Severe: true),
        new(BackupDone,         "Setup", "Backup finished",       "A database backup completed."),
        new(LoginBlocked,       "Setup", "Sign-in blocked",       "An account was locked after repeated wrong passwords.", Severe: true),

        new(Anomaly,            "Insights", "Something looks wrong", "The nightly check found a figure well outside its usual range."),
        new(Test,               "Insights", "Test",               "Only ever sent when somebody presses Test."),
    };

    /// <summary>Whether this kind is one that should buzz a phone.</summary>
    public static bool IsSevere(string kind) =>
        All.FirstOrDefault(k => k.Key == kind)?.Severe ?? false;
}
