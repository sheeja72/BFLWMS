namespace Wms.Data.Lpm;

/// <summary>One row of the Nightly Batches admin table.</summary>
public record RptJobRunRow(
    long      RunId,
    string    JobName,
    string?   Country,
    string    Mode,
    DateTime  StartTS,
    DateTime? EndTS,
    string    Status,
    int?      RowsProcessed,
    int?      DatesProcessed,
    string?   ErrorMessage,
    string?   TriggeredBy);

/// <summary>Country toggle row.</summary>
public record RptCountryConfigRow(string Country, bool IsActive, DateTime UpdatedTS, string UpdatedBy);
