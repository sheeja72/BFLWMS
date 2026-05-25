namespace Aiwms.Data.ContainerProcess;

public record ContProcessItem(
    string Upc,
    string Itemcode,
    string Itemname,
    string GroupCode,
    string Season,
    string Department,
    string Division,
    string Vendor,
    int OrgQty,
    string? BuildingCategory,
    DateTime? LpmDt,
    string? OraPoNo,
    string? Style);

public record ContProcessResultRow(
    string Contno,
    string Upc,
    string Itemcode,
    string GroupCode,
    string Season,
    string Department,
    string Division,
    string Result,
    string? FinalResult,
    string? ResultType,
    string? Itemname,
    string? BuildingCategory,
    DateTime? LpmDt,
    string? OraPoNo,
    string? Style,
    // export enrichment — filled by EnrichExportResultsAsync
    string Company     = "",
    string ShopCode    = "",
    string RefNo       = "",
    string Mark        = "",
    string Barcode     = "",
    double SalesPrice  = 0);

public record ContProcessSummaryRow(string Result, string? FinalResult, string? PalletType, int Qty);

public record ContProcessValidation(bool Ok, string? Error, List<string> MissingItems, List<ContProcessItem>? Items = null);

public record ContProcessOutcome(
    bool Ok,
    string? Error,
    int TotalSku,
    int TotalQty,
    List<ContProcessSummaryRow> Summary);

public record ContProcessProgress(int Done, int Total, string CurrentItemcode, string? CurrentItemname = null, string? CurrentResult = null, string? CurrentBc = null, string? CurrentSeason = null, DateTime? CurrentLpmDt = null);

public record ChuteLocationRow(string Result, string LpmDt, string ChuteLocation, int Qty);

public record ContProcessPrecheck(bool Ok, string? Error, bool IsRamadanCont = false, string ContType = "USA", bool IsAlreadyProcessed = false);
