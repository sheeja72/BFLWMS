namespace Wms.Data.Lpm;

/// <summary>One row on the Manual Allocation Upload page's preview grid —
/// the four columns supplied by the operator's Excel file (StoreID, ContNo,
/// Itemcode, AllocationQty) plus every enrichment computed from
/// LPMSIM / usa / racks / Datareporting.</summary>
public record ManualAllocationRow(
    string  StoreID,
    string  ContNo,
    string  Itemcode,
    int     AllocationQty,
    string? Division,
    int?    POQty,
    int?    eComSOH,
    int?    SkuMax,
    int?    SkuBalance,     // SkuMax - eComSOH
    int?    QualifiedQty,   // MIN(SkuBalance, AllocationQty)
    int?    DivEOM,
    int?    DivSOH,
    int?    EomBalance);    // DivEOM - DivSOH

public record ManualAllocationStoreRow(string StoreID, string StoreName);

public record ManualAllocationValidateResult(bool Ok, string? Error);

public record ManualAllocationSaveResult(bool Ok, string? Error, int RowsSaved);

/// <summary>Raw shape parsed from the operator's Excel file. Enrichment
/// happens server-side (EnrichRowsAsync) against LPMSIM / usa / racks
/// via OnPremBackup.</summary>
public record ManualAllocationUploadRow(
    string StoreID,
    string ContNo,
    string Itemcode,
    int    AllocationQty);
