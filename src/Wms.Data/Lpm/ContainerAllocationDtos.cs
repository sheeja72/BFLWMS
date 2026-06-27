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
    DateTime? LPMDt,
    double? OTS = null);

/// <summary>One row in the blocked-items list: an (item, store) pair that was
/// excluded from allocation by LPM_StoreDeptAccess or LPM_StoreDivAccess.</summary>
public record BlockedItemRow(
    string  Contno,
    string  ItemCode,
    string? ItemName,
    string? Division,
    string? Department,
    string  StoreID,
    string? StoreName,
    string  Country,
    int     PoQty,
    int     DivCode,
    string  BlockReason);   // 'DeptAccess' / 'DivAccess' / 'DeptAccess+DivAccess'

/// <summary>State info shown above the buttons. Now tracks per-RunOption final
/// row counts so the page knows whether each algorithm has been run for this container.</summary>
public record AllocationStatus(
    bool HasDraft,
    bool HasFinal,
    int  DraftRows,
    int  FinalRows,
    DateTime? FinalAt,
    string? DraftRunOption,
    int  FillSkuMaxRows,
    int  RoundRobinRows);

/// <summary>How to distribute qty across eligible stores.</summary>
public enum RunOption { FillSKUMax = 0, RoundRobin = 1 }

/// <summary>What ProcessAllocationAsync returns — allocations + the
/// (item, store) pairs blocked by LPM_StoreDeptAccess / LPM_StoreDivAccess.</summary>
public record AllocationProcessResult(
    List<AllocationRow>    Allocations,
    List<BlockedItemRow>   Blocked);

/// <summary>Header row read back for the "Processed Contnos" dropdown banner.
/// Mirrors WMS_Cont_Allocation_Header columns.</summary>
public record BatchInfo(
    int       BatchNo,
    string    ContNo,
    string?   Warehouse,
    string    GenCountry,
    string    Country,            // comma-separated allocation destinations
    string    RunOption,
    int?      RowCount1,
    int?      TotalQty,
    DateTime  ProcessedTS,
    string?   ProcessedBy,
    DateTime? ApprovedDt,
    string?   ApprovedBy);
