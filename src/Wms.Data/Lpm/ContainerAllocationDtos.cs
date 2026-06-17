namespace Wms.Data.Lpm;

/// <summary>One row in the "Load PO Data" grid on the Container Allocation page.</summary>
public record PoDataRow(
    string    Contno,
    DateTime? ContReceiptDT,
    string?   PONO,
    string?   LPM,
    string?   Buyer,
    string?   Division,
    string?   Brand,
    int       Qty,
    string?   DestCountry);

/// <summary>
/// Outcome of the Phase-1 validation. Each check that ran produces a step
/// entry. Ok = true means every step passed; Phase-2 process can then start.
/// </summary>
public record ContainerAllocationValidationResult(
    bool                       Ok,
    IReadOnlyList<ValidationStep> Steps);

public record ValidationStep(string Label, bool Ok, string? Detail);

/// <summary>Progress event from ProcessAllocationAsync.</summary>
public record AllocationProgress(int Current, int Total, string? CurrentItem);

/// <summary>One row in the allocation preview / output. Each row = one PO line item distributed to one destination store.</summary>
public record AllocationRow(
    string  Contno,
    string  OraPONo,
    string  ItemCode,
    string? ItemName,
    string? Brand,
    int     PoQty,
    string  StoreID,
    string? StoreName,
    string  Country,
    string? Division,
    string  VolumeGroup,
    int     SkuMax,
    int     AllocQty,
    int     MerchNeedMonth,
    int     DivCode,
    int     RoundRobinExtra,
    string? LPM,
    DateTime? LPMDt);

/// <summary>State info shown above the buttons.</summary>
public record AllocationStatus(bool HasDraft, bool HasFinal, int DraftRows, int FinalRows, DateTime? FinalAt);
