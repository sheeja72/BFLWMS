using System.Data;
using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace Wms.Data.Lpm;

/// <summary>
/// Tracks the most recent SQL operation so error displays can show it
/// (Dapper / SqlException don't carry the original SQL text by default).
/// </summary>
public static class DbOpContext
{
    private static readonly AsyncLocal<string?> _op = new();
    private static readonly AsyncLocal<string?> _sql = new();
    public static string? CurrentOp { get => _op.Value; set => _op.Value = value; }
    public static string? CurrentSql { get => _sql.Value; set => _sql.Value = value; }
    public static void Set(string op, string sql) { _op.Value = op; _sql.Value = sql; }
    public static void Clear() { _op.Value = null; _sql.Value = null; }
}

/// <summary>
/// All LPM Manual Building business logic.
/// Cross-DB: bfldata, usa, lpm, hodata, datareporting, LPMSIM, WMS — Dapper + 3-part naming.
///
/// Lifecycle:
///   1. Container/Box validation only (no writes) — Photo qty match cached in WMS.
///   2. Item scan: validates → resolves allocation in PCR (SERIALIZABLE) → upserts WMS staging.
///   3. Check-in tote: writes WmsOpenBox + items (no lpm.* writes yet). UNIQUE(ToteID) blocks dupes.
///   4. Checkout: SERIALIZABLE — INSERT lpm.UPCBoxHeadLPM / UPCBoxDetLPM / PhotocheckingLPM,
///      UPDATE LPMSIM.PCR.BoxNo, DELETE WMS staging. Same user enforced.
///
/// Concurrency:
///   - Box-number minted via UPDLOCK,HOLDLOCK on WmsBoxSequence — never duplicates.
///   - WmsOpenBox.ToteID UNIQUE — blocks two open boxes sharing a tote.
///   - PCR allocation uses SERIALIZABLE so two users can't both claim same qty=0 row.
/// </summary>
public class BuildingService(IOnPremConnectionResolver resolver, ICurrentUser user, IMemoryCache cache)
{
    // Cached once per app lifetime: is lpm.dbo.PhotoCheckingResultLPM.IdNO an IDENTITY column?
    // Determines the INSERT shape used by InsertNewPcrAsync. Reset on app restart
    // — so after running migrate_pcr_idno_identity.sql, restart the app to pick it up.
    private static bool? _idnoIsIdentity;

    // Per-country on-prem connection. WMS staging tables, lpm.*, bfldata.*, usa.*,
    // hodata.*, datareporting.* all live on this single per-country server today.
    // Until 2d migrates writes to Azure WMS, BuildingService keeps using this.
    private SqlConnection Open(string? _ = null)
    {
        var country = user.Country
            ?? throw new InvalidOperationException(
                "Current user has no Country assigned — cannot resolve on-prem connection. " +
                "Admin must set WmsUser.Country before this user runs Manual Building.");
        var c = new SqlConnection(resolver.GetCountryConnectionString(country));
        c.Open();
        return c;
    }

    private async Task<bool> IsIdNoIdentityAsync(SqlConnection c, SqlTransaction? tx, CancellationToken ct)
    {
        if (_idnoIsIdentity is bool b) return b;
        var v = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            @"SELECT CAST(is_identity AS INT)
              FROM sys.columns
              WHERE object_id = OBJECT_ID('lpm.dbo.PhotoCheckingResultLPM') AND name = 'IdNO'",
            transaction: tx, cancellationToken: ct)) ?? 0;
        _idnoIsIdentity = (v == 1);
        return _idnoIsIdentity.Value;
    }

    // ==================== 1. Container validation ====================
    public async Task<ContainerCheckResult> ValidateContainerAsync(string contno, CancellationToken ct = default)
    {
        contno = (contno ?? "").Trim();
        if (string.IsNullOrEmpty(contno)) return new(false, "Container number is required.");
        if (contno.Length > 50) return new(false, "Container number too long (max 50).");

        await using var c = Open();
        var built = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM bfldata.dbo.buildingcompletion WITH (NOLOCK) WHERE Contno = @c",
            new { c = contno }, cancellationToken: ct));
        if (built == 1) return new(false, $"Container {contno} building is already completed.");

        var received = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM bfldata.dbo.contreceipt WITH (NOLOCK) WHERE refno = @c",
            new { c = contno }, cancellationToken: ct));
        if (received != 1) return new(false, $"Container {contno} receipt is not done.");

        var open = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP 1 closed FROM usa.dbo.OpenUSACont WITH (NOLOCK) WHERE contno = @c",
            new { c = contno }, cancellationToken: ct));
        if (open is null) return new(false, $"Container {contno} is not open in usa.dbo.OpenUSACont.");
        if (string.Equals(open, "Y", StringComparison.OrdinalIgnoreCase))
            return new(false, $"Container {contno} is closed in usa.dbo.OpenUSACont.");

        return new(true, null);
    }

    // ==================== 2. Box validation + PO parse ====================
    public async Task<BoxCheckResult> ValidateBoxAsync(string contno, string boxLabel, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(boxLabel)) return new(false, "Box number/label is required.", null, false);
        var parsed = Wms.Core.Validation.BoxLabelParser.Parse(boxLabel);

        if (!string.IsNullOrEmpty(parsed.Contno) &&
            !string.Equals(parsed.Contno, contno, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, $"Box label container ({parsed.Contno}) doesn't match scanned container ({contno}).",
                parsed.PoNumber, parsed.HasPo);
        }

        var bareBox = boxLabel.Trim();
        await using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync<(string boxno, string? closed)>(new CommandDefinition(
            @"SELECT TOP 1 boxno, closed FROM usa.dbo.KNBBoxes WITH (NOLOCK)
              WHERE contno = @cont AND boxno = @box",
            new { cont = contno, box = bareBox }, cancellationToken: ct));

        if (row.boxno is null) return new(false, $"Box {bareBox} not found in container {contno}.", parsed.PoNumber, parsed.HasPo);
        if (string.Equals(row.closed, "Y", StringComparison.OrdinalIgnoreCase))
            return new(false, $"Box {bareBox} is already closed.", parsed.PoNumber, parsed.HasPo);

        if (parsed.HasPo)
        {
            var poOk = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                @"SELECT TOP 1 1 FROM usa.dbo.USAOrgFile WITH (NOLOCK)
                  WHERE contno = @cont AND OraPONo = @po",
                new { cont = contno, po = parsed.PoNumber }, cancellationToken: ct));
            if (poOk != 1) return new(false, $"PO {parsed.PoNumber} on box does not match container {contno}.", parsed.PoNumber, true);
        }
        return new(true, null, parsed.PoNumber, parsed.HasPo);
    }

    // ==================== 3. One-time photo qty match ====================
    public async Task<PhotoQtyMatchResult> EnsurePhotoQtyMatchAsync(string contno, CancellationToken ct = default)
    {
        await using var c = Open();
        var existing = await c.QueryFirstOrDefaultAsync<(int PhotoQty, int OrgQty, bool Matched)>(new CommandDefinition(
            "SELECT PhotoQty, OrgQty, Matched FROM dbo.WmsContainerPhotoCheck WHERE Contno = @c",
            new { c = contno }, cancellationToken: ct));
        if (existing.Matched) return new(true, null, existing.PhotoQty, existing.OrgQty, true);

        var atLeastOne = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM lpm.dbo.PhotoCheckingResultLPM WITH (NOLOCK) WHERE Contno = @c",
            new { c = contno }, cancellationToken: ct));
        if (atLeastOne != 1) return new(false, $"No PhotoCheckingResultLPM rows for container {contno}.", 0, 0, false);

        var photo = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT ISNULL(SUM(qty),0) FROM lpm.dbo.PhotoCheckingResultLPM WITH (NOLOCK) WHERE Contno = @c",
            new { c = contno }, cancellationToken: ct)) ?? 0;
        var org = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT ISNULL(SUM(orgqty),0) FROM usa.dbo.USAOrgFile WITH (NOLOCK) WHERE contno = @c",
            new { c = contno }, cancellationToken: ct)) ?? 0;
        var matched = photo == org;

        await c.ExecuteAsync(new CommandDefinition(
            @"MERGE dbo.WmsContainerPhotoCheck AS t
              USING (SELECT @c AS Contno) s ON t.Contno = s.Contno
              WHEN MATCHED THEN UPDATE SET PhotoQty=@p, OrgQty=@o, Matched=@m, CheckedTS=SYSDATETIME(), CheckedBy=@u
              WHEN NOT MATCHED THEN INSERT (Contno, PhotoQty, OrgQty, Matched, CheckedTS, CheckedBy)
                VALUES (@c, @p, @o, @m, SYSDATETIME(), @u);",
            new { c = contno, p = photo, o = org, m = matched, u = user.Name }, cancellationToken: ct));

        if (!matched) return new(false, $"Container Allocated Qty : {photo}, Container Manifest Qty : {org}, does not match, Cannot Proceed.", photo, org, false);
        return new(true, null, photo, org, false);
    }

    // ==================== 4. Item details ====================
    public async Task<ItemDetails?> GetItemDetailsAsync(string contno, string itemCode, CancellationToken ct = default)
    {
        await using var c = Open();
        var item = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            @"SELECT TOP 1 itemcode, itemname, style, size, color, vendor AS brand, season,
                     gender, hscode, lpm, groupcode
              FROM usa.dbo.USAOrgFile WITH (NOLOCK)
              WHERE contno = @c AND itemcode = @i",
            new { c = contno, i = itemCode }, cancellationToken: ct));

        var availability = ItemAvailability.NotFound;
        string? itemcode = itemCode, itemname = null, style = null, size = null, color = null,
                brand = null, season = null, gender = null, hscode = null, lpm = null, groupcode = null;

        if (item is not null)
        {
            availability = ItemAvailability.InContainer;
            itemcode = item.itemcode; itemname = item.itemname; style = item.style; size = item.size;
            color = item.color; brand = item.brand; season = item.season; gender = item.gender;
            hscode = item.hscode; lpm = item.lpm?.ToString(); groupcode = item.groupcode;
        }
        else
        {
            var inMaster = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM usa.dbo.UPCbarcodes WITH (NOLOCK) WHERE itemcode = @i",
                new { i = itemCode }, cancellationToken: ct));
            availability = inMaster == 1 ? ItemAvailability.InItemMaster : ItemAvailability.NotFound;
        }

        string? groupName = null;
        if (!string.IsNullOrEmpty(groupcode))
        {
            groupName = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT TOP 1 Description FROM hodata.dbo.itemgroup WITH (NOLOCK) WHERE groupcode = @g",
                new { g = groupcode }, cancellationToken: ct));
        }

        string? division = null, dept = null, klass = null, family = null, subclass = null;
        var mh4 = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 MH4ID FROM datareporting.dbo.upc_subclass WITH (NOLOCK) WHERE itemcode = @i",
            new { i = itemCode }, cancellationToken: ct));
        if (mh4 is not null)
        {
            var h = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                @"SELECT TOP 1 Division, Department, Class, Family, Subclass
                  FROM datareporting.dbo.SubclassMaster WITH (NOLOCK) WHERE MH4ID = @m",
                new { m = mh4 }, cancellationToken: ct));
            if (h is not null)
            {
                division = h.Division; dept = h.Department; klass = h.Class;
                family = h.Family; subclass = h.Subclass;
            }
        }

        return new ItemDetails(itemcode!, itemname, style, size, color, brand, season, gender,
            hscode, lpm, groupcode, groupName, division, dept, klass, family, subclass, availability);
    }

    // ==================== 5. PCR 4-tier allocation (real columns) ====================
    public async Task<AllocationResult> ResolveAllocationAsync(
        string contno, string itemCode, string? poNumber, string? style, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var t1Sql = poNumber is null
            ? @"SELECT TOP 1 IdNO, Result, LPMDT, OraPoNO, ResultType
                FROM lpm.dbo.PhotoCheckingResultLPM WITH (UPDLOCK, ROWLOCK)
                WHERE Contno = @c AND Itemcode = @i AND ISNULL(QtyIssue,0) = 0
                ORDER BY Contno, OraPoNO, LPMDT"
            : @"SELECT TOP 1 IdNO, Result, LPMDT, OraPoNO, ResultType
                FROM lpm.dbo.PhotoCheckingResultLPM WITH (UPDLOCK, ROWLOCK)
                WHERE Contno = @c AND Itemcode = @i AND OraPoNO = @p AND ISNULL(QtyIssue,0) = 0
                ORDER BY Contno, OraPoNO, LPMDT";
        var t1 = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            t1Sql, new { c = contno, i = itemCode, p = poNumber }, transaction: tx, cancellationToken: ct));

        if (t1 is not null)
        {
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE lpm.dbo.PhotoCheckingResultLPM SET QtyIssue = ISNULL(QtyIssue,0) + 1 WHERE IdNO = @id",
                new { id = (long)t1.IdNO }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return new AllocationResult(true, (string)t1.Result, (DateTime?)t1.LPMDT, (string?)t1.OraPoNO,
                (string?)t1.ResultType, AllocationTier.Tier1_ExactPoQty0, (long)t1.IdNO, 'U');
        }

        var t2Sql = poNumber is null
            ? @"SELECT TOP 1 LPMDT, OraPoNO, ResultType FROM lpm.dbo.PhotoCheckingResultLPM WITH (NOLOCK)
                WHERE Contno = @c AND Itemcode = @i ORDER BY LPMDT"
            : @"SELECT TOP 1 LPMDT, OraPoNO, ResultType FROM lpm.dbo.PhotoCheckingResultLPM WITH (NOLOCK)
                WHERE Contno = @c AND Itemcode = @i AND OraPoNO = @p ORDER BY LPMDT";
        var t2 = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            t2Sql, new { c = contno, i = itemCode, p = poNumber }, transaction: tx, cancellationToken: ct));

        if (t2 is not null)
        {
            var newId = await InsertNewPcrAsync(c, tx, contno, itemCode, (string?)t2.OraPoNO ?? poNumber,
                (DateTime?)t2.LPMDT ?? DateTime.Now.Date, "SHOP", (string?)t2.ResultType, ct);
            await tx.CommitAsync(ct);
            return new AllocationResult(true, "SHOP", (DateTime?)t2.LPMDT, (string?)t2.OraPoNO,
                (string?)t2.ResultType, AllocationTier.Tier2_ExactNoQty, newId, 'I');
        }

        if (!string.IsNullOrEmpty(style))
        {
            var t3sql = @"SELECT TOP 1 LPMDT, OraPoNO, ResultType, Result FROM lpm.dbo.PhotoCheckingResultLPM WITH (NOLOCK)
                  WHERE Contno = @c AND Style = @s AND (@p IS NULL OR OraPoNO = @p)
                  ORDER BY LPMDT";
            DbOpContext.Set("PCR Tier-3 (style fallback) on lpm.dbo.PhotoCheckingResultLPM", t3sql);
            var t3 = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                t3sql,
                new { c = contno, s = style, p = poNumber }, transaction: tx, cancellationToken: ct));
            if (t3 is not null)
            {
                var newId = await InsertNewPcrAsync(c, tx, contno, itemCode, (string?)t3.OraPoNO ?? poNumber,
                    (DateTime?)t3.LPMDT ?? DateTime.Now.Date, (string)t3.Result ?? "SHOP", (string?)t3.ResultType, ct);
                await tx.CommitAsync(ct);
                return new AllocationResult(true, (string?)t3.Result ?? "SHOP", (DateTime?)t3.LPMDT,
                    (string?)t3.OraPoNO, (string?)t3.ResultType, AllocationTier.Tier3_StyleMatch, newId, 'I');
            }
        }

        var earliest = await c.QueryFirstOrDefaultAsync<(DateTime? Dt, string? Po, string? Pt)>(new CommandDefinition(
            @"SELECT TOP 1 LPMDT AS Dt, OraPoNO AS Po, ResultType AS Pt
              FROM lpm.dbo.PhotoCheckingResultLPM WITH (NOLOCK)
              WHERE Contno = @c ORDER BY LPMDT",
            new { c = contno }, transaction: tx, cancellationToken: ct));
        var dt = earliest.Dt ?? DateTime.Now.Date;
        var po = earliest.Po ?? poNumber;
        var pt = earliest.Pt;
        var id4 = await InsertNewPcrAsync(c, tx, contno, itemCode, po, dt, "SHOP", pt, ct);
        await tx.CommitAsync(ct);
        return new AllocationResult(true, "SHOP", dt, po, pt, AllocationTier.Tier4_NewItem, id4, 'I');
    }

    private async Task<long> InsertNewPcrAsync(SqlConnection c, SqlTransaction tx,
        string contno, string itemCode, string? po, DateTime lpmDt, string result, string? palletType, CancellationToken ct)
    {
        var isIdentity = await IsIdNoIdentityAsync(c, tx, ct);
        string sql;
        if (isIdentity)
        {
            // After migrate_pcr_idno_identity.sql has been run — IDENTITY autogenerates IdNO.
            sql = @"
                INSERT INTO lpm.dbo.PhotoCheckingResultLPM
                  (Contno, Itemcode, OraPoNO, LPMDT, Result, ResultType, QtyIssue)
                VALUES (@c, @i, @p, @d, @r, @pt, 1);
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";
        }
        else
        {
            // Pre-migration fallback — compute next IdNO inside this SERIALIZABLE tx.
            // UPDLOCK,HOLDLOCK prevents two concurrent sessions from picking the same id.
            sql = @"
                DECLARE @newId BIGINT;
                SELECT @newId = ISNULL(MAX(IdNO),0) + 1
                  FROM lpm.dbo.PhotoCheckingResultLPM WITH (UPDLOCK, HOLDLOCK);
                INSERT INTO lpm.dbo.PhotoCheckingResultLPM
                  (IdNO, Contno, Itemcode, OraPoNO, LPMDT, Result, ResultType, QtyIssue)
                VALUES (@newId, @c, @i, @p, @d, @r, @pt, 1);
                SELECT @newId;";
        }

        DbOpContext.Set(
            $"PCR INSERT (IdNO {(isIdentity ? "IDENTITY" : "computed")}) on lpm.dbo.PhotoCheckingResultLPM",
            sql);
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new { c = contno, i = itemCode, p = po, d = lpmDt, r = result, pt = palletType },
            transaction: tx, cancellationToken: ct));
        return id;
    }

    // ==================== 6a. Find a matching open box (read-only) ====================
    public async Task<string?> FindMatchingOpenBoxAsync(
        string contno, string palletType, string? division, string? season, DateTime? lpmDt,
        CancellationToken ct = default)
    {
        await using var c = Open();
        return await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            @"SELECT TOP 1 BoxNo FROM dbo.WmsOpenBox WITH (NOLOCK)
              WHERE Contno = @c AND UserId = @u AND PalletType = @p
                AND ISNULL(Division,'') = ISNULL(@d,'')
                AND ISNULL(Season,'')   = ISNULL(@s,'')
                AND ISNULL(LPMDt,'1900-01-01') = ISNULL(@l,'1900-01-01')
              ORDER BY BoxNo",
            new { c = contno, u = user.Name, p = palletType, d = division, s = season, l = lpmDt?.Date },
            cancellationToken: ct));
    }

    // ==================== 6b. Open a new box with tote attached up front ====================
    public async Task<(bool Ok, string? Error, string BoxNo)> CreateNewOpenBoxAsync(
        string contno, string palletType, string? division, string? season, DateTime? lpmDt,
        string? logisticsBoxNo, string toteId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toteId)) return (false, "Tote ID is required.", "");
        var t = toteId.Trim();

        await using var c = Open();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var inMaster = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM bfldata.dbo.BlueToteIDMaster WITH (NOLOCK) WHERE ToteID = @t",
            new { t }, transaction: tx, cancellationToken: ct));
        if (inMaster != 1) return (false, $"Tote {t} is not in BlueToteIDMaster.", "");

        var inUse = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsOpenBox WITH (UPDLOCK, HOLDLOCK) WHERE ToteID = @t",
            new { t }, transaction: tx, cancellationToken: ct));
        if (inUse == 1) return (false, $"Tote {t} is already attached to another open box.", "");

        var openLpm = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM lpm.dbo.UPCBoxHeadLPM WITH (NOLOCK) WHERE ToteID = @t AND Closed = 'N'",
            new { t }, transaction: tx, cancellationToken: ct));
        if (openLpm == 1) return (false, $"Tote {t} is attached to an open box in UPCBoxHeadLPM.", "");

        var nextSeq = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            @"MERGE dbo.WmsBoxSequence WITH (HOLDLOCK) AS tg
              USING (SELECT @c AS Contno) src ON tg.Contno = src.Contno
              WHEN MATCHED THEN UPDATE SET NextSeq = NextSeq + 1, UpdatedTS = SYSDATETIME()
              WHEN NOT MATCHED THEN INSERT (Contno, NextSeq) VALUES (@c, 2)
              OUTPUT CASE WHEN $action = 'INSERT' THEN 1 ELSE inserted.NextSeq - 1 END;",
            new { c = contno }, transaction: tx, cancellationToken: ct));
        var boxNo = $"{contno}-{nextSeq:D4}";

        await c.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.WmsOpenBox
                (BoxNo, Contno, UserId, PalletType, Division, Season, LPMDt, ToteID, LogisticsBoxNo)
              VALUES (@b, @c, @u, @p, @d, @s, @l, @t, @lb);",
            new { b = boxNo, c = contno, u = user.Name, p = palletType, d = division, s = season,
                  l = lpmDt?.Date, t = t, lb = logisticsBoxNo },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return (true, null, boxNo);
    }

    // ==================== 6c. Find existing OR create a new open box (no tote required up front) ====================
    public async Task<string> FindOrCreateOpenBoxAsync(
        string contno, string palletType, string? division, string? season, DateTime? lpmDt,
        string? logisticsBoxNo, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var existing = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            @"SELECT TOP 1 BoxNo FROM dbo.WmsOpenBox WITH (UPDLOCK, HOLDLOCK)
              WHERE Contno = @c AND UserId = @u AND PalletType = @p
                AND ISNULL(Division,'') = ISNULL(@d,'')
                AND ISNULL(Season,'')   = ISNULL(@s,'')
                AND ISNULL(LPMDt,'1900-01-01') = ISNULL(@l,'1900-01-01')
              ORDER BY BoxNo",
            new { c = contno, u = user.Name, p = palletType, d = division, s = season, l = lpmDt?.Date },
            transaction: tx, cancellationToken: ct));

        if (existing is not null) { await tx.CommitAsync(ct); return existing; }

        var nextSeq = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            @"MERGE dbo.WmsBoxSequence WITH (HOLDLOCK) AS tg
              USING (SELECT @c AS Contno) src ON tg.Contno = src.Contno
              WHEN MATCHED THEN UPDATE SET NextSeq = NextSeq + 1, UpdatedTS = SYSDATETIME()
              WHEN NOT MATCHED THEN INSERT (Contno, NextSeq) VALUES (@c, 2)
              OUTPUT CASE WHEN $action = 'INSERT' THEN 1 ELSE inserted.NextSeq - 1 END;",
            new { c = contno }, transaction: tx, cancellationToken: ct));
        var boxNo = $"{contno}-{nextSeq:D4}";

        await c.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.WmsOpenBox
                (BoxNo, Contno, UserId, PalletType, Division, Season, LPMDt, ToteID, LogisticsBoxNo)
              VALUES (@b, @c, @u, @p, @d, @s, @l, NULL, @lb);",
            new { b = boxNo, c = contno, u = user.Name, p = palletType, d = division, s = season,
                  l = lpmDt?.Date, lb = logisticsBoxNo },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return boxNo;
    }

    // ==================== 6d. Stage an item into a known open box (logs PCR effect for Clear) ====================
    public async Task<int> StageItemToBoxAsync(
        string boxNo, string itemCode, string? result, long? pcrId, char pcrAction,
        string? size, string? color, string? style, string? groupCode, string? season,
        CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var stageSql = @"DECLARE @sr INT;
                    SELECT @sr = SrNo FROM dbo.WmsOpenBoxItem WHERE BoxNo = @b AND ItemCode = @i;
                    IF @sr IS NULL
                    BEGIN
                        SELECT @sr = ISNULL(MAX(SrNo),0) + 1 FROM dbo.WmsOpenBoxItem WHERE BoxNo = @b;
                        INSERT INTO dbo.WmsOpenBoxItem
                          (BoxNo, ItemCode, Qty, SrNo, Result, PCRowId, Size, Color, Style, GroupCode, Season)
                        VALUES (@b, @i, 1, @sr, @r, @pcr, @sz, @co, @st, @gc, @se);
                    END
                    ELSE
                    BEGIN
                        UPDATE dbo.WmsOpenBoxItem SET Qty = Qty + 1, ScannedTS = SYSDATETIME()
                         WHERE BoxNo = @b AND ItemCode = @i;
                    END
                    SELECT @sr;";
        DbOpContext.Set("Stage item to existing WmsOpenBox", stageSql);
        var srNo = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            stageSql,
            new { b = boxNo, i = itemCode, r = result, pcr = pcrId,
                  sz = size, co = color, st = style, gc = groupCode, se = season },
            transaction: tx, cancellationToken: ct));

        // Log the PCR effect of THIS scan so Clear can reverse it.
        await c.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.WmsOpenBoxScan (BoxNo, ItemCode, PcrAction, PcrId)
              VALUES (@b, @i, @a, @id);",
            new { b = boxNo, i = itemCode, a = pcrAction, id = pcrId },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return srNo;
    }

    // ==================== 6e. Attach a tote to an existing open box ====================
    public async Task<(bool Ok, string? Error)> AttachToteToBoxAsync(string boxNo, string toteId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toteId)) return (false, "Tote ID is required.");
        var t = toteId.Trim();
        await using var c = Open();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var ownerCheck = await c.QueryFirstOrDefaultAsync<(string Cont, string Owner, string? Tote)>(new CommandDefinition(
            "SELECT Contno, UserId, ToteID FROM dbo.WmsOpenBox WITH (UPDLOCK, ROWLOCK) WHERE BoxNo = @b",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));
        if (ownerCheck.Cont is null) return (false, $"Box {boxNo} not found.");
        if (!string.Equals(ownerCheck.Owner, user.Name, StringComparison.OrdinalIgnoreCase))
            return (false, $"Box {boxNo} belongs to {ownerCheck.Owner}, not you.");

        var inMaster = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM bfldata.dbo.BlueToteIDMaster WITH (NOLOCK) WHERE ToteID = @t",
            new { t }, transaction: tx, cancellationToken: ct));
        if (inMaster != 1) return (false, $"Tote {t} is not in BlueToteIDMaster.");

        var conflict = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsOpenBox WITH (UPDLOCK, HOLDLOCK) WHERE ToteID = @t AND BoxNo <> @b",
            new { t, b = boxNo }, transaction: tx, cancellationToken: ct));
        if (conflict == 1) return (false, $"Tote {t} is already attached to another open box.");

        var openLpm = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM lpm.dbo.UPCBoxHeadLPM WITH (NOLOCK) WHERE ToteID = @t AND Closed = 'N'",
            new { t }, transaction: tx, cancellationToken: ct));
        if (openLpm == 1) return (false, $"Tote {t} is attached to an open box in UPCBoxHeadLPM.");

        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.WmsOpenBox SET ToteID = @t WHERE BoxNo = @b",
            new { t, b = boxNo }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (true, null);
    }

    // ==================== 6f. Clear a box — reverse all PCR effects + delete staging ====================
    public async Task<(bool Ok, string? Error, int ScansReversed)> ClearBoxAsync(string boxNo, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var owner = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT UserId FROM dbo.WmsOpenBox WITH (UPDLOCK, ROWLOCK) WHERE BoxNo = @b",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));
        if (owner is null) return (false, $"Box {boxNo} not found.", 0);
        if (!string.Equals(owner, user.Name, StringComparison.OrdinalIgnoreCase))
            return (false, $"Box {boxNo} belongs to {owner}, not you.", 0);

        // Read all scans for this box, newest first (so we reverse in reverse order of operations)
        var scans = (await c.QueryAsync<(long Id, string ItemCode, char PcrAction, long? PcrId)>(new CommandDefinition(
            "SELECT Id, ItemCode, PcrAction, PcrId FROM dbo.WmsOpenBoxScan WITH (UPDLOCK, ROWLOCK) WHERE BoxNo = @b ORDER BY Id DESC",
            new { b = boxNo }, transaction: tx, cancellationToken: ct))).AsList();

        var reversed = 0;
        foreach (var s in scans)
        {
            if (s.PcrId is null) continue;
            if (s.PcrAction == 'U')
            {
                // Tier-1 increment — decrement by 1 (clamped at 0)
                await c.ExecuteAsync(new CommandDefinition(
                    @"UPDATE lpm.dbo.PhotoCheckingResultLPM
                         SET QtyIssue = CASE WHEN ISNULL(QtyIssue,0) > 0 THEN QtyIssue - 1 ELSE 0 END
                       WHERE IdNO = @id",
                    new { id = s.PcrId.Value }, transaction: tx, cancellationToken: ct));
                reversed++;
            }
            else if (s.PcrAction == 'I')
            {
                // Tier-2/3/4 insert — delete the row WE inserted
                await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM lpm.dbo.PhotoCheckingResultLPM WHERE IdNO = @id",
                    new { id = s.PcrId.Value }, transaction: tx, cancellationToken: ct));
                reversed++;
            }
        }

        // Delete staging children (cascade FK on Box deletion will also handle Items + Scans, but be explicit for clarity)
        await c.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.WmsOpenBoxScan  WHERE BoxNo = @b;
              DELETE FROM dbo.WmsOpenBoxItem  WHERE BoxNo = @b;
              DELETE FROM dbo.WmsOpenBox      WHERE BoxNo = @b;",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return (true, null, reversed);
    }

    // ==================== 6. Stage scan into open box (or open new) — legacy ====================
    public async Task<StageScanResult> StageScanAsync(StageScanRequest req, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // Find an open box for this user that matches the combo (Contno, PalletType, Division, Season, LPMDt).
        var existing = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            @"SELECT TOP 1 BoxNo FROM dbo.WmsOpenBox WITH (UPDLOCK, ROWLOCK)
              WHERE Contno = @c AND UserId = @u AND PalletType = @p
                AND ISNULL(Division,'') = ISNULL(@d,'') AND ISNULL(Season,'') = ISNULL(@s,'')
                AND ISNULL(LPMDt,'1900-01-01') = ISNULL(@l,'1900-01-01')
              ORDER BY BoxNo",
            new { c = req.Contno, u = user.Name, p = req.PalletType, d = req.Division, s = req.Season, l = req.LpmDt?.Date },
            transaction: tx, cancellationToken: ct));

        string boxNo;
        bool newBox;
        if (existing is not null)
        {
            boxNo = (string)existing.BoxNo;
            newBox = false;
        }
        else
        {
            var nextSeq = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                @"MERGE dbo.WmsBoxSequence WITH (HOLDLOCK) AS t
                  USING (SELECT @c AS Contno) s ON t.Contno = s.Contno
                  WHEN MATCHED THEN UPDATE SET NextSeq = NextSeq + 1, UpdatedTS = SYSDATETIME()
                  WHEN NOT MATCHED THEN INSERT (Contno, NextSeq) VALUES (@c, 2)
                  OUTPUT CASE WHEN $action = 'INSERT' THEN 1 ELSE inserted.NextSeq - 1 END;",
                new { c = req.Contno }, transaction: tx, cancellationToken: ct));
            boxNo = $"{req.Contno}-{nextSeq:D4}";
            await c.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.WmsOpenBox (BoxNo, Contno, UserId, PalletType, Division, Season, LPMDt, ToteID, LogisticsBoxNo)
                  VALUES (@b, @c, @u, @p, @d, @s, @l, @t, @lb);",
                new { b = boxNo, c = req.Contno, u = user.Name, p = req.PalletType, d = req.Division,
                      s = req.Season, l = req.LpmDt?.Date, t = "", lb = req.LogisticsBoxNo },
                transaction: tx, cancellationToken: ct));
            newBox = true;
        }

        // Upsert item line (aggregate qty per BoxNo+ItemCode), assign SrNo if new.
        var srNo = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            @"DECLARE @sr INT;
              SELECT @sr = SrNo FROM dbo.WmsOpenBoxItem WHERE BoxNo = @b AND ItemCode = @i;
              IF @sr IS NULL
              BEGIN
                  SELECT @sr = ISNULL(MAX(SrNo),0) + 1 FROM dbo.WmsOpenBoxItem WHERE BoxNo = @b;
                  INSERT INTO dbo.WmsOpenBoxItem
                    (BoxNo, ItemCode, Qty, SrNo, Result, PCRowId, Size, Color, Style, GroupCode, Season)
                  VALUES (@b, @i, 1, @sr, @r, @pcr, @sz, @co, @st, @gc, @se);
              END
              ELSE
              BEGIN
                  UPDATE dbo.WmsOpenBoxItem SET Qty = Qty + 1, ScannedTS = SYSDATETIME()
                   WHERE BoxNo = @b AND ItemCode = @i;
              END
              SELECT @sr;",
            new { b = boxNo, i = req.ItemCode, r = req.Result, pcr = req.PCRowId,
                  sz = req.Size, co = req.Color, st = req.Style, gc = req.GroupCode, se = req.Season },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new StageScanResult(true, null, boxNo, newBox, srNo);
    }

    // ==================== 7. Check-in tote on the staged box ====================
    public async Task<(bool Ok, string? Error)> CheckInToteAsync(string boxNo, string toteId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var box = await c.QueryFirstOrDefaultAsync<(string Contno, string UserId, string ToteID)>(new CommandDefinition(
            "SELECT Contno, UserId, ToteID FROM dbo.WmsOpenBox WITH (UPDLOCK, ROWLOCK) WHERE BoxNo = @b",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));
        if (box.Contno is null) return (false, $"Open box {boxNo} not found.");
        if (!string.Equals(box.UserId, user.Name, StringComparison.OrdinalIgnoreCase))
            return (false, $"Box {boxNo} belongs to {box.UserId}, not you.");

        if (!string.IsNullOrEmpty(box.ToteID) && !string.Equals(box.ToteID, toteId, StringComparison.OrdinalIgnoreCase))
            return (false, $"Box {boxNo} already checked-in to tote {box.ToteID}; scan that tote to check out.");

        var inMaster = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM bfldata.dbo.BlueToteIDMaster WITH (NOLOCK) WHERE ToteID = @t",
            new { t = toteId }, transaction: tx, cancellationToken: ct));
        if (inMaster != 1) return (false, $"Tote {toteId} is not in BlueToteIDMaster.");

        // Block other users' open boxes on same tote
        var otherOpen = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsOpenBox WITH (UPDLOCK, HOLDLOCK) WHERE ToteID = @t AND BoxNo <> @b",
            new { t = toteId, b = boxNo }, transaction: tx, cancellationToken: ct));
        if (otherOpen == 1) return (false, $"Tote {toteId} is already used by another open box.");

        // Block tote that is open elsewhere in lpm.UPCBoxHeadLPM (e.g., another active container)
        var openLpm = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM lpm.dbo.UPCBoxHeadLPM WITH (NOLOCK) WHERE ToteID = @t AND Closed = 'N'",
            new { t = toteId }, transaction: tx, cancellationToken: ct));
        if (openLpm == 1) return (false, $"Tote {toteId} is already attached to an open box in UPCBoxHeadLPM.");

        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.WmsOpenBox SET ToteID = @t WHERE BoxNo = @b",
            new { t = toteId, b = boxNo }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (true, null);
    }

    // ==================== 8. Checkout — write all 3 lpm.* tables + update PCR + clear staging ====================
    public async Task<CheckoutResult> CheckoutBoxAsync(string boxNo, string toteId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var box = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            @"SELECT BoxNo, Contno, UserId, PalletType, Division, Season, LPMDt, ToteID, LogisticsBoxNo
              FROM dbo.WmsOpenBox WITH (UPDLOCK, ROWLOCK) WHERE BoxNo = @b",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));
        if (box is null) return new(false, $"Open box {boxNo} not found.", 0);
        if (!string.Equals((string)box.UserId, user.Name, StringComparison.OrdinalIgnoreCase))
            return new(false, $"Same user must check-in and check-out. Box belongs to {box.UserId}.", 0);
        if (!string.Equals((string?)box.ToteID, toteId, StringComparison.OrdinalIgnoreCase))
            return new(false, $"Tote scanned ({toteId}) does not match check-in tote ({box.ToteID}).", 0);

        var items = (await c.QueryAsync<dynamic>(new CommandDefinition(
            @"SELECT Id, ItemCode, Qty, SrNo, Result, PCRowId, Size, Color, Style, GroupCode, Season
              FROM dbo.WmsOpenBoxItem WITH (UPDLOCK, ROWLOCK) WHERE BoxNo = @b ORDER BY SrNo",
            new { b = boxNo }, transaction: tx, cancellationToken: ct))).AsList();
        if (items.Count == 0) return new(false, "Cannot check out an empty box.", 0);

        var contno = (string)box.Contno;
        var palletType = (string)box.PalletType;
        var division = (string?)box.Division ?? "";
        var season = (string?)box.Season ?? "";
        var lpmDt = (DateTime?)box.LPMDt ?? DateTime.Now.Date;
        var logisticsBoxNo = (string?)box.LogisticsBoxNo ?? "";
        var checkInUser = (string)box.UserId;
        var checkoutUser = user.Name;
        var warehouse = user.Warehouse ?? "";
        var pcName = user.ClientPcName ?? "";

        // 1) lpm.UPCBoxHeadLPM
        var headSql = @"INSERT INTO lpm.dbo.UPCBoxHeadLPM
            (BoxNo, TrnDate, Time1, NewPallet, PreparedBy, Remarks, Userid, PalletType, Closed,
             GroupCode, OldBoxNo, Prepared1, Prepared2, WHouse, FWType, FPreparedBy, FPalletType,
             ISize, Gender, ToteID, LPMDT)
          VALUES
            (@BoxNo, CAST(SYSDATETIME() AS DATE), CAST(SYSDATETIME() AS TIME(0)), 'Y', @CheckOut, 'from WMS', @CheckIn, @Pallet, 'N',
             '', '', @CheckOut, @Division, @WHouse, '', @CheckOut, @Pallet,
             '', '', @Tote, @LPMDt);";
        DbOpContext.Set("INSERT lpm.dbo.UPCBoxHeadLPM (checkout step 1)", headSql);
        await c.ExecuteAsync(new CommandDefinition(headSql,
            new { BoxNo = boxNo, CheckOut = checkoutUser, CheckIn = checkInUser, Pallet = palletType,
                  Division = division, WHouse = warehouse, Tote = toteId, LPMDt = lpmDt },
            transaction: tx, cancellationToken: ct));

        // 2) lpm.UPCBoxDetLPM (one row per item; SrNo from staging; UPC=Itemcode; imgfile=Contno)
        var detSql = @"INSERT INTO lpm.dbo.UPCBoxDetLPM
            (BoxNo, Itemcode, Qty, QtyIssued, SrNo, Status, UPC, imgfile)
          VALUES (@BoxNo, @Item, @Qty, 0, @SrNo, '', @Item, @Cont);";
        foreach (var it in items)
        {
            DbOpContext.Set("INSERT lpm.dbo.UPCBoxDetLPM (checkout step 2)", detSql);
            await c.ExecuteAsync(new CommandDefinition(detSql,
                new { BoxNo = boxNo, Item = (string)it.ItemCode, Qty = (int)it.Qty,
                      SrNo = (int)it.SrNo, Cont = contno },
                transaction: tx, cancellationToken: ct));
        }

        // 3) lpm.PhotocheckingLPM — ONE ROW PER SCAN (qty rows per item)
        var photoSql = @"INSERT INTO lpm.dbo.PhotocheckingLPM
            (ContNo, TrnDate, Time1, UPC, PhotoSize, Result, CheckedBy, CmpName, BoxSize,
             Photo, Style, Color, GroupCode, ItemName, Warehouse, PhotoCheckType, RRP,
             Logistics_BoxNo, Season, ToteID, RoboStatus, BarCode)
          VALUES
            (@Cont, CAST(SYSDATETIME() AS DATE), CAST(SYSDATETIME() AS TIME(0)), @Item, @Size, @Result, @User, @Pc, @Size,
             '', @Style, @Color, @Gc, '', @WHouse, '', 0,
             @Logi, @Season, @Tote, 'N', '');";
        var photoRows = 0;
        foreach (var it in items)
        {
            var qty = (int)it.Qty;
            for (int i = 0; i < qty; i++)
            {
                DbOpContext.Set("INSERT lpm.dbo.PhotocheckingLPM (checkout step 3 — one per scan)", photoSql);
                await c.ExecuteAsync(new CommandDefinition(photoSql,
                    new { Cont = contno, Item = (string)it.ItemCode, Size = (string?)it.Size ?? "",
                          Result = (string?)it.Result ?? "SHOP", User = checkoutUser, Pc = pcName,
                          Style = (string?)it.Style ?? "", Color = (string?)it.Color ?? "",
                          Gc = (string?)it.GroupCode ?? "", WHouse = warehouse,
                          Logi = logisticsBoxNo, Season = (string?)it.Season ?? season, Tote = toteId },
                    transaction: tx, cancellationToken: ct));
                photoRows++;
            }
        }

        // 4) Update PCR.BoxNo for items in this box+container
        var pcrSql = @"UPDATE lpm.dbo.PhotoCheckingResultLPM
            SET BoxNo = @BoxNo
            WHERE Contno = @Cont AND Itemcode = @Item AND (BoxNo IS NULL OR BoxNo = '')";
        var pcrUpdated = 0;
        foreach (var it in items)
        {
            DbOpContext.Set("UPDATE lpm.dbo.PhotoCheckingResultLPM SET BoxNo (checkout step 4)", pcrSql);
            pcrUpdated += await c.ExecuteAsync(new CommandDefinition(pcrSql,
                new { BoxNo = boxNo, Cont = contno, Item = (string)it.ItemCode },
                transaction: tx, cancellationToken: ct));
        }

        // 5) Clear staging
        DbOpContext.Set("DELETE WMS staging (checkout step 5)", "DELETE WmsOpenBoxItem/Scan/Box");
        await c.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.WmsOpenBoxScan  WHERE BoxNo = @b;
              DELETE FROM dbo.WmsOpenBoxItem  WHERE BoxNo = @b;
              DELETE FROM dbo.WmsOpenBox      WHERE BoxNo = @b;",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new(true, null, pcrUpdated);
    }

    // ==================== 9. Read open boxes (resume after reload) ====================
    public async Task<List<OpenBoxRow>> GetOpenBoxesForUserAsync(string contno, CancellationToken ct = default)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<OpenBoxRow>(new CommandDefinition(
            @"SELECT b.BoxNo AS BoxNumber, b.Division, b.PalletType, b.Season, b.LPMDt AS LpmDt, b.ToteID AS ToteId,
                     ISNULL((SELECT SUM(Qty) FROM dbo.WmsOpenBoxItem i WHERE i.BoxNo = b.BoxNo),0) AS ItemQty
              FROM dbo.WmsOpenBox b
              WHERE b.Contno = @c AND b.UserId = @u
              ORDER BY b.BoxNo",
            new { c = contno, u = user.Name }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<StagedItemRow>> GetStagedItemsAsync(string boxNo, CancellationToken ct = default)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<StagedItemRow>(new CommandDefinition(
            @"SELECT BoxNo, ItemCode, Qty, SrNo, Result, Size, Color, Style, GroupCode, Season
              FROM dbo.WmsOpenBoxItem WHERE BoxNo = @b ORDER BY SrNo",
            new { b = boxNo }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT Country FROM bfldata.dbo.DataSettings WITH (NOLOCK)
              WHERE Country IS NOT NULL AND ActiveStore = 'Y' ORDER BY Country", cancellationToken: ct));
        return list.AsList();
    }
}
