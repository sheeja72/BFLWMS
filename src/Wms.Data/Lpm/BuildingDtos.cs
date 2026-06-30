namespace Wms.Data.Lpm;

public record ContainerCheckResult(bool Ok, string? Error);

public record BoxCheckResult(
    bool Ok,
    string? Error,
    string? PoNumber,
    bool PoAvailable);

public record PhotoQtyMatchResult(
    bool Ok,
    string? Error,
    int PhotoQty,
    int OrgQty,
    bool AlreadyVerified);

public record ItemDetails(
    string ItemCode,
    string? ItemName,
    string? Style,
    string? Size,
    string? Color,
    string? Brand,
    string? Season,
    string? Gender,
    string? HsCode,
    string? Lpm,
    string? GroupCode,
    string? GroupName,
    string? Division,
    string? Department,
    string? Class,
    string? Family,
    string? Subclass,
    ItemAvailability Availability);

public enum ItemAvailability
{
    NotFound,        // not in container, not in itemmaster — must use Item Encoding
    InContainer,     // found in usaorgfile for this contno
    InItemMaster,    // found in upcbarcodes only
}

public record AllocationResult(
    bool Found,
    string Result,           // SHOP / TERR / etc
    DateTime? LpmDt,
    string? PoNumber,
    string? PalletType,
    AllocationTier Tier,
    int? AllocationIdNo,     // dbo.WMS_ContAllocationData.IdNo (the row updated or inserted)
    char Action = 'I',       // 'U' = QtyIssue+=1 on existing row; 'I' = inserted new row
    string? StoreId = null,
    string? StoreName = null,// PBFullname from dbo.WMS_DataSettings, looked up by StoreId
    string? Division = null,
    bool Manual = false,
    string? Error = null);

/// <summary>One row in the LPM Manual Building "My Activity Today" grid —
/// today's scans by the current user, newest first.</summary>
public record TodayScanRow(
    long      ScanId,
    DateTime  ScannedTS,
    string    ContNo,
    string    Itemcode,
    string?   Result,
    string?   StoreID,
    string?   StoreName,    // PBFullname
    string?   Division,
    string    BoxNo,
    string?   ToteID,
    byte      Tier,
    string?   Manual);

public enum AllocationTier
{
    Tier1_HasCapacity   = 1,   // existing row, QtyIssue < Qty, incremented
    Tier2_OtsOverflow   = 2,   // item in container but all rows full; OTS pick + new row
    Tier3_ManualNewItem = 3,   // item NOT in container; usa.upcbarcodes lookup + OTS pick
}

public record CheckInResult(bool Ok, string? Error, string? BoxNumber);

public record CheckoutResult(bool Ok, string? Error, int RowsUpdated);

public record OpenBoxRow(
    string BoxNumber,
    string? Division,
    string? PalletType,
    string? Season,
    DateTime? LpmDt,
    string? ToteId,
    int ItemQty);

public record StageScanRequest(
    string Contno,
    string ItemCode,
    string PalletType,
    string? Division,
    string? Season,
    DateTime? LpmDt,
    string? Result,
    long? PCRowId,
    string? Size,
    string? Color,
    string? Style,
    string? GroupCode,
    string? LogisticsBoxNo);

public record StageScanResult(
    bool Ok,
    string? Error,
    string BoxNo,
    bool NewBoxCreated,
    int SrNo);

public record StagedItemRow(
    string BoxNo,
    string ItemCode,
    int Qty,
    int SrNo,
    string? Result,
    string? Size,
    string? Color,
    string? Style,
    string? GroupCode,
    string? Season);
