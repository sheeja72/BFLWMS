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

/// <summary>Ported from LPMSIM. One cell of the LPM WH Stock report:
/// purchased LPM qty for (Country, Division, Season, LPMDt Year/Month).
/// Razor rolls these into Allocation / Season / Month views.</summary>
public record LpmWhStockCell(string Country, string Division, string Season, int Year, int Month, long Qty);

/// <summary>Box Summary aggregated by month (yyyy-MM).</summary>
public record BoxSummaryMonthRow(string Month, int BoxCount, int MissQty, int ExcessQty);

/// <summary>Item Summary aggregated by (Division, Department).</summary>
public record ItemSummaryByDivDeptRow(string? Division, string? Department, int MissingQty, int ExcessQty, int HOStock);

/// <summary>Box Detail row with Missing + Excess as separate columns so each
/// item appears once with both contributions.
/// Missing = qty − QtyIssued when Status='' and QtyIssued &lt; qty.
/// Excess  = QtyIssued when Status&lt;&gt;'' (issued qty IS the excess).</summary>
public record BoxDetailCombinedRow(string? BoxNo, string? PreparedBy, string? ItemCode, int Qty, int QtyIssued, int MissingQty, int ExcessQty);
