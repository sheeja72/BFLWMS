using System.Data;
using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

public record AvailableContnoRow(
    string ContNo,
    int    BatchCount,
    int    TotalAllocatedQty,
    DateTime? LatestTrnDate);

public record OpenContainerRow(
    string  ContNo,
    string? ContDesc,
    string? OpenReason,
    string? UserId,
    DateTime? Trndate,
    TimeSpan? Time1,
    string? Whouse);

public record OpenContainerResult(bool Ok, string? Message);

/// <summary>
/// "Open Container" page on the Manual Building track.
///
/// Lists ContNos that have allocation data on Azure WMS
/// (dbo.WMS_ContAllocationData) but are not yet open in dbo.WmsOpenUSACont
/// (or are present there with Closed='Y'). Operator picks a row, enters an
/// optional reason, and clicks Open — the row is inserted into
/// WmsOpenUSACont with Closed='N' and the user's Country / Warehouse / Now.
///
/// Closing a container is NOT done here — it happens via Manual Building
/// completion, by design.
/// </summary>
public class OpenContainerService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
    private string Country =>
        user.Country
        ?? throw new InvalidOperationException(
            "Current user has no Country assigned — cannot open containers.");

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    /// <summary>Containers with allocation data on Azure that aren't currently open.</summary>
    public async Task<List<AvailableContnoRow>> GetAvailableToOpenAsync(CancellationToken ct = default)
    {
        var country = Country;
        await using var c = OpenWms();
        var rows = await c.QueryAsync<AvailableContnoRow>(new CommandDefinition(@"
            SELECT a.ContNo,
                   BatchCount        = COUNT(DISTINCT a.BatchNo),
                   TotalAllocatedQty = ISNULL(SUM(a.AllocatedQty), 0),
                   LatestTrnDate     = MAX(a.TrnDate)
              FROM dbo.WMS_ContAllocationData a WITH (NOLOCK)
             WHERE NOT EXISTS (
                       SELECT 1 FROM dbo.WmsOpenUSACont o WITH (NOLOCK)
                        WHERE o.Country = @country
                          AND o.contno  = a.ContNo
                          AND ISNULL(o.Closed,'N') = 'N')
             GROUP BY a.ContNo
             ORDER BY MAX(a.TrnDate) DESC, a.ContNo",
            new { country }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Currently-open containers for this user's country.</summary>
    public async Task<List<OpenContainerRow>> GetCurrentlyOpenAsync(CancellationToken ct = default)
    {
        var country = Country;
        await using var c = OpenWms();
        var rows = await c.QueryAsync<OpenContainerRow>(new CommandDefinition(@"
            SELECT ContNo     = contno,
                   ContDesc   = contDesc,
                   OpenReason,
                   UserId     = Userid,
                   Trndate,
                   Time1,
                   Whouse
              FROM dbo.WmsOpenUSACont WITH (NOLOCK)
             WHERE Country = @country
               AND ISNULL(Closed,'N') = 'N'
             ORDER BY Trndate DESC, Time1 DESC, contno",
            new { country }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Open a container: insert (or re-open) the row in WmsOpenUSACont with Closed='N'.</summary>
    public async Task<OpenContainerResult> OpenAsync(string contno, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno))
            return new(false, "Container number is required.");
        contno = contno.Trim();
        var country  = Country;
        var whouse   = user.Warehouse;
        var userid   = user.Name;

        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // Guard rail — must actually have allocation data on Azure.
        var hasAlloc = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WMS_ContAllocationData WITH (NOLOCK) WHERE ContNo = @c",
            new { c = contno }, transaction: tx, cancellationToken: ct));
        if (hasAlloc != 1)
        {
            await tx.RollbackAsync(ct);
            return new(false, $"Container {contno} has no allocation data on Azure — sync it first.");
        }

        // Already open?
        var alreadyOpen = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            @"SELECT TOP 1 1 FROM dbo.WmsOpenUSACont WITH (UPDLOCK, HOLDLOCK)
              WHERE Country = @country AND contno = @c AND ISNULL(Closed,'N') = 'N'",
            new { country, c = contno }, transaction: tx, cancellationToken: ct));
        if (alreadyOpen == 1)
        {
            await tx.RollbackAsync(ct);
            return new(false, $"Container {contno} is already open.");
        }

        // UPSERT: re-open if a Closed='Y' row exists; otherwise insert fresh.
        var sql = @"
            MERGE dbo.WmsOpenUSACont WITH (HOLDLOCK) AS tg
            USING (SELECT @country AS Country, @c AS contno) AS src
               ON tg.Country = src.Country AND tg.contno = src.contno
            WHEN MATCHED THEN
                UPDATE SET Closed     = 'N',
                           Trndate    = CAST(SYSDATETIME() AS DATE),
                           Time1      = CAST(SYSDATETIME() AS TIME(0)),
                           Userid     = @userid,
                           Whouse     = @whouse,
                           OpenReason = @reason
            WHEN NOT MATCHED THEN
                INSERT (Country, contno, Closed, Trndate, Time1, Userid, Whouse, OpenReason)
                VALUES (@country, @c,    'N',    CAST(SYSDATETIME() AS DATE), CAST(SYSDATETIME() AS TIME(0)), @userid, @whouse, @reason);";
        await c.ExecuteAsync(new CommandDefinition(
            sql, new { country, c = contno, userid, whouse, reason },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new(true, $"Container {contno} opened.");
    }
}
