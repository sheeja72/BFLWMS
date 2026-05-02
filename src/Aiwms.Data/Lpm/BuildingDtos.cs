namespace Aiwms.Data.Lpm;

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
    string Result,        // SHOP / TERR / etc
    DateTime? LpmDt,
    string? PoNumber,
    string? PalletType,
    AllocationTier Tier,
    long? PcrId,           // identity of lpm.dbo.PhotoCheckingResultLPM row updated/inserted
    char PcrAction = 'I'); // 'U' = QtyIssue+=1 on existing row; 'I' = inserted new row (tier 2/3/4)

public enum AllocationTier
{
    Tier1_ExactPoQty0 = 1,
    Tier2_ExactNoQty  = 2,
    Tier3_StyleMatch  = 3,
    Tier4_NewItem     = 4,
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
