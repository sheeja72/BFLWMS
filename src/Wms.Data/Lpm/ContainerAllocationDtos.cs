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

/// <summary>Progress event from ProcessAllocationAsync. Current/Total are PO line items processed.</summary>
public record AllocationProgress(int Current, int Total, string? CurrentItem);

/// <summary>
/// One row in the allocation preview / output. Each row = one PO line item
/// distributed to one destination store.
/// </summary>
public record AllocationRow(
    string  Contno,
    string  OraPONo,
    string  ItemCode,
    int     PoQty,           // original qty on the PO line
    string  ShopCode,        // destination StoreID
    string  Country,
    string  VolumeGroup,
    int     SkuMax,          // the rule cap that applied
    int     AllocQty,        // qty assigned to this store
    int     MerchNeedMonth,
    int     DivCode,
    int     RoundRobinExtra, // pieces given over and above SkuMax via round-robin
    string? LPM,
    DateTime? LPMDt);
