namespace vizo_backend.Models;

public partial class SalesOrderItem
{
    /// <summary>
    /// What actually left the shelf for this line, set when the order is
    /// dispatched. NULL for a line never dispatched -- including every one
    /// dispatched before this column existed, which reads correctly as
    /// "before this was tracked" rather than as zero.
    ///
    /// Never more than <see cref="Quantity"/>: the Packing screen lets the
    /// order desk reduce what a salesperson asked for, never raise it. See
    /// backend/database/25_packing_and_claims_removed.sql.
    /// </summary>
    public int? DispatchedQty { get; set; }
}
