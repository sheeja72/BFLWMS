namespace Wms.Data.Lpm;

public enum LpmPresence { Any = 0, HasLpm = 1, NoLpm = 2 }

public enum WhGroupBy { Box = 0, Division = 1, Department = 2, Brand = 3 }

public record WhBoxFilter(
    IReadOnlyList<string>? Warehouse,
    IReadOnlyList<string>? TypeName,
    IReadOnlyList<string>? PalletCategory,
    IReadOnlyList<string>? Lpm,
    string? Search,
    LpmPresence LpmStatus = LpmPresence.Any,
    IReadOnlyList<string>? Division = null,
    IReadOnlyList<string>? Department = null,
    IReadOnlyList<string>? Brand = null,
    bool IncludeNonPurchased = false,
    IReadOnlyList<string>? ContNo = null,
    string? Country = "UAE",
    DateTime? TrnDateFrom = null,
    DateTime? TrnDateTo = null,
    bool MixedSeasonOnly = false,
    IReadOnlyList<string>? Season = null);

public record WhBoxRow(
    string  Country,
    string  Warehouse,
    string  PalletNo,
    string  BoxNo,
    string  PalletType,
    string? TypeName,
    string? PalletCategory,
    long    Qty,
    string? LPM,
    string? Division,
    string? Department,
    string? Brand,
    string? Rack,
    string? Purchased,
    string? ContNo,
    DateTime? TrnDate,
    DateTime? CurrDate,
    long    SummerQty,
    long    WinterQty,
    string? OraPoNo);

public record WhDivisionRow(string? Division, long LPMCurrentQty, long LPMFutureQty, long NonLPMQty);
public record WhDepartmentRow(string? Division, string? Department, long LPMCurrentQty, long LPMFutureQty, long NonLPMQty);
public record WhBrandRow(string? Division, string? Department, string? Brand, long LPMCurrentQty, long LPMFutureQty, long NonLPMQty);
public record CountrySummaryRow(string Country, string Season, long LPMCurrentQty, long LPMFutureQty, long NonLPMQty, long TotalQty);
