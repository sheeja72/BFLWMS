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
