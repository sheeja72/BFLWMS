namespace Wms.Data.Lpm;

/// <summary>Box Summary row — one per (Palletno, Trndate, closedby).</summary>
public record BoxSummaryRow(
    string?   BoxNo,
    DateTime? ClosedDt,
    string?   ClosedBy,
    int       MissQty,
    int       ExcessQty);

/// <summary>Box Detailed row — one per (BoxNo, ItemCode); used for both Missing and Excess views.</summary>
public record BoxDetailRow(
    string? BoxNo,
    string? PreparedBy,
    string? ItemCode,
    int     Qty,
    int     QtyIssued,
    int     Diff);     // Missing = Qty - QtyIssued; Excess = QtyIssued - Qty (caller sets the sign)

/// <summary>Item Summary row — aggregated per ItemCode.</summary>
public record ItemSummaryReportRow(
    string?  ItemCode,
    string?  ItemName,
    string?  Hierarchy,
    string?  Division,
    string?  Department,
    int      MissingQty,
    int      ExcessQty,
    int      HOStock);
