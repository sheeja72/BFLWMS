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
    string?  Division,
    string?  Department,
    int      MissingQty,
    int      ExcessQty,
    int      HOStock);

/// <summary>Ported from LPMSIM. One row of the Non-LPM WH Stock report:
/// (Country, Division) with Summer + Winter Non-LPM eligible qty.</summary>
public record NonLpmWhStockRow(string Country, string Division, long Summer, long Winter);

/// <summary>Box Summary aggregated by month (yyyy-MM).</summary>
public record BoxSummaryMonthRow(string Month, int BoxCount, int MissQty, int ExcessQty);

/// <summary>Item Summary aggregated by (Division, Department).</summary>
public record ItemSummaryByDivDeptRow(string? Division, string? Department, int MissingQty, int ExcessQty, int HOStock);

/// <summary>Box Detail row with a Type label ("Missing" or "Excess") so the
/// combined export distinguishes which side the row came from.</summary>
public record BoxDetailCombinedRow(string Type, string? BoxNo, string? PreparedBy, string? ItemCode, int Qty, int QtyIssued, int Diff);
