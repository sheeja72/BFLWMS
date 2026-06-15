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
/// LPM Manual Building business logic — Phase 2d.
///
/// Routing (post-Azure migration):
///   - Azure SQL WMS DB (WmsAzure conn) — ALL transactional writes + most reads.
///     Tables: WmsPCR, WmsOpenBox, WmsOpenBoxItem, WmsOpenBoxScan, WmsBoxSequence,
///     WmsContainerPhotoCheck, WmsOpenUSACont, WmsKNBBoxes, WmsBuildingCompletion,
///     WmsBlueToteIDMaster, WmsUPCBoxHead, WmsUPCBoxDet, WMSContBuilding.
///   - OnPremBackupDB (UAE backup conn) — read-only validation for:
///     contreceipt, upc_subclass, SubclassMaster.
///
/// Country filter: every Azure WMS query restricts by Country from user.Country,
/// because all 7 countries share one DB.
///
/// Concurrency:
///   - Box-number minted via UPDLOCK,HOLDLOCK on WmsBoxSequence — never duplicates.
///   - WmsOpenBox.ToteID UNIQUE — blocks two open boxes sharing a tote.
///   - PCR allocation uses SERIALIZABLE.
/// </summary>
public class BuildingService(IOnPremConnectionResolver resolver, ICurrentUser user, IMemoryCache cache)
{
    private string Country =>
        user.Country
        ?? throw new InvalidOperationException(
            "Current user has no Country assigned — cannot run Manual Building. " +
            "Admin must set WmsUser.Country first.");

    /// <summary>Azure SQL WMS DB — all writes and most reads.</summary>
    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    /// <summary>UAE-only backup DB — read-only validation reads (contreceipt, upc_subclass, SubclassMaster).</summary>
    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    // ==================== 1. Container validation ====================
    public async Task<ContainerCheckResult> ValidateContainerAsync(string contno, CancellationToken ct = default)
    {
        contno = (contno ?? "").Trim();
        if (string.IsNullOrEmpty(contno)) return new(false, "Container number is required.");
        if (contno.Length > 50) return new(false, "Container number too long (max 50).");

        var country = Country;

        // 1a. Has this container already been completed? (Azure WMS)
        await using (var c = OpenWms())
        {
            var built = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM dbo.WmsBuildingCompletion WITH (NOLOCK) WHERE Country = @ct AND ContNo = @c",
                new { ct = country, c = contno }, cancellationToken: ct));
            if (built == 1) return new(false, $"Container {contno} building is already completed.");
        }

        // 1b. Receipt check goes to the OnPremBackup DB (contreceipt is not migrated).
        await using (var c = OpenOnPremBackup())
        {
            var received = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM dbo.contreceipt WITH (NOLOCK) WHERE refno = @c",
                new { c = contno }, cancellationToken: ct));
            if (received != 1) return new(false, $"Container {contno} receipt is not done.");
        }

        // 1c. Is the container open in our open-container table? (Azure WMS)
        await using (var c = OpenWms())
        {
            var open = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT TOP 1 Closed FROM dbo.WmsOpenUSACont WITH (NOLOCK) WHERE Country = @ct AND contno = @c",
                new { ct = country, c = contno }, cancellationToken: ct));
            if (open is null) return new(false, $"Container {contno} is not open in WmsOpenUSACont.");
            if (string.Equals(open, "Y", StringComparison.OrdinalIgnoreCase))
                return new(false, $"Container {contno} is closed in WmsOpenUSACont.");
        }

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
        var country = Country;

        await using var c = OpenWms();
        var row = await c.QueryFirstOrDefaultAsync<(string Boxno, string? closed)>(new CommandDefinition(
            @"SELECT TOP 1 Boxno, closed FROM dbo.WmsKNBBoxes WITH (NOLOCK)
              WHERE Country = @ct AND Contno = @cont AND Boxno = @box",
            new { ct = country, cont = contno, box = bareBox }, cancellationToken: ct));

        if (row.Boxno is null) return new(false, $"Box {bareBox} not found in container {contno}.", parsed.PoNumber, parsed.HasPo);
        if (string.Equals(row.closed, "Y", StringComparison.OrdinalIgnoreCase))
            return new(false, $"Box {bareBox} is already closed.", parsed.PoNumber, parsed.HasPo);

        if (parsed.HasPo)
        {
            // PO validation now reads from WmsPCR (USAOrgFile dropped — PCR carries OraPoNO).
            var poOk = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                @"SELECT TOP 1 1 FROM dbo.WmsPCR WITH (NOLOCK)
                  WHERE Country = @ct AND Contno = @cont AND OraPoNO = @po",
                new { ct = country, cont = contno, po = parsed.PoNumber }, cancellationToken: ct));
            if (poOk != 1) return new(false, $"PO {parsed.PoNumber} on box does not match container {contno}.", parsed.PoNumber, true);
        }
        return new(true, null, parsed.PoNumber, parsed.HasPo);
    }

    // ==================== 3. One-time photo qty match ====================
    public async Task<PhotoQtyMatchResult> EnsurePhotoQtyMatchAsync(string contno, CancellationToken ct = default)
    {
        var country = Country;
        await using var c = OpenWms();

        var existing = await c.QueryFirstOrDefaultAsync<(int PhotoQty, int OrgQty, bool Matched)>(new CommandDefinition(
            @"SELECT PhotoQty, OrgQty, Matched FROM dbo.WmsContainerPhotoCheck
              WHERE Country = @ct AND Contno = @c",
            new { ct = country, c = contno }, cancellationToken: ct));
        if (existing.Matched) return new(true, null, existing.PhotoQty, existing.OrgQty, true);

        // PCR existence check
        var atLeastOne = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsPCR WITH (NOLOCK) WHERE Country = @ct AND Contno = @c",
            new { ct = country, c = contno }, cancellationToken: ct));
        if (atLeastOne != 1) return new(false, $"No WmsPCR rows for container {contno}.", 0, 0, false);

        // Photo qty = allocated so far.
        var photo = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT ISNULL(SUM(QtyIssue),0) FROM dbo.WmsPCR WITH (NOLOCK) WHERE Country = @ct AND Contno = @c",
            new { ct = country, c = contno }, cancellationToken: ct)) ?? 0;
        // Manifest qty = expected (denormalised from old usa.dbo.USAOrgFile.orgqty).
        var org = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT ISNULL(SUM(ManifestQty),0) FROM dbo.WmsPCR WITH (NOLOCK) WHERE Country = @ct AND Contno = @c",
            new { ct = country, c = contno }, cancellationToken: ct)) ?? 0;
        var matched = photo == org;

        await c.ExecuteAsync(new CommandDefinition(
            @"MERGE dbo.WmsContainerPhotoCheck AS t
              USING (SELECT @ct AS Country, @c AS Contno) s ON t.Country = s.Country AND t.Contno = s.Contno
              WHEN MATCHED THEN UPDATE SET PhotoQty=@p, OrgQty=@o, Matched=@m, CheckedTS=SYSDATETIME(), CheckedBy=@u
              WHEN NOT MATCHED THEN INSERT (Country, Contno, PhotoQty, OrgQty, Matched, CheckedTS, CheckedBy)
                VALUES (@ct, @c, @p, @o, @m, SYSDATETIME(), @u);",
            new { ct = country, c = contno, p = photo, o = org, m = matched, u = user.Name }, cancellationToken: ct));

        if (!matched) return new(false, $"Container Allocated Qty : {photo}, Container Manifest Qty : {org}, does not match, Cannot Proceed.", photo, org, false);
        return new(true, null, photo, org, false);
    }

    // ==================== 4. Item details ====================
    // Sourced from WmsPCR (denormalised — has ItemName, Style, Size, Color, Brand,
    // Season, Gender, Hscode). USAOrgFile/UPCbarcodes/itemgroup dropped.
    // Subclass details still come from OnPremBackup (upc_subclass + SubclassMaster).
    public async Task<ItemDetails?> GetItemDetailsAsync(string contno, string itemCode, CancellationToken ct = default)
    {
        var country = Country;

        await using var c = OpenWms();
        var item = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            @"SELECT TOP 1 Itemcode, ItemName, Style, Size, Color, Brand, Season, Gender, Hscode
              FROM dbo.WmsPCR WITH (NOLOCK)
              WHERE Country = @ct AND Contno = @c AND Itemcode = @i",
            new { ct = country, c = contno, i = itemCode }, cancellationToken: ct));

        var availability = ItemAvailability.NotFound;
        string? itemcode = itemCode, itemname = null, style = null, size = null, color = null,
                brand = null, season = null, gender = null, hscode = null;

        if (item is not null)
        {
            availability = ItemAvailability.InContainer;
            itemcode = item.Itemcode; itemname = item.ItemName; style = item.Style;
            size = item.Size; color = item.Color; brand = item.Brand;
            season = item.Season; gender = item.Gender; hscode = item.Hscode;
        }

        // Subclass hierarchy stays on OnPremBackup.
        string? division = null, dept = null, klass = null, family = null, subclass = null;
        await using (var b = OpenOnPremBackup())
        {
            var mh4 = await b.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 MH4ID FROM dbo.upc_subclass WITH (NOLOCK) WHERE itemcode = @i",
                new { i = itemCode }, cancellationToken: ct));
            if (mh4 is not null)
            {
                var h = await b.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                    @"SELECT TOP 1 Division, Department, Class, Family, Subclass
                      FROM dbo.SubclassMaster WITH (NOLOCK) WHERE MH4ID = @m",
                    new { m = mh4 }, cancellationToken: ct));
                if (h is not null)
                {
                    division = h.Division; dept = h.Department; klass = h.Class;
                    family = h.Family; subclass = h.Subclass;
                }
            }
        }

        // groupcode + groupName intentionally dropped (Phase 2b decision).
        return new ItemDetails(itemcode!, itemname, style, size, color, brand, season, gender,
            hscode, null, null, null, division, dept, klass, family, subclass, availability);
    }

    // ==================== 5. PCR 4-tier allocation ====================
    public async Task<AllocationResult> ResolveAllocationAsync(
        string contno, string itemCode, string? poNumber, string? style, CancellationToken ct = default)
    {
        var country = Country;
        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var t1Sql = poNumber is null
            ? @"SELECT TOP 1 IdNO, Result, LPMDT, OraPoNO, ResultType
                FROM dbo.WmsPCR WITH (UPDLOCK, ROWLOCK)
                WHERE Country = @ct AND Contno = @c AND Itemcode = @i AND ISNULL(QtyIssue,0) = 0
                ORDER BY Contno, OraPoNO, LPMDT"
            : @"SELECT TOP 1 IdNO, Result, LPMDT, OraPoNO, ResultType
                FROM dbo.WmsPCR WITH (UPDLOCK, ROWLOCK)
                WHERE Country = @ct AND Contno = @c AND Itemcode = @i AND OraPoNO = @p AND ISNULL(QtyIssue,0) = 0
                ORDER BY Contno, OraPoNO, LPMDT";
        var t1 = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            t1Sql, new { ct = country, c = contno, i = itemCode, p = poNumber }, transaction: tx, cancellationToken: ct));

        if (t1 is not null)
        {
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.WmsPCR SET QtyIssue = ISNULL(QtyIssue,0) + 1 WHERE Country = @ct AND IdNO = @id",
                new { ct = country, id = (long)t1.IdNO }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return new AllocationResult(true, (string)t1.Result, (DateTime?)t1.LPMDT, (string?)t1.OraPoNO,
                (string?)t1.ResultType, AllocationTier.Tier1_ExactPoQty0, (long)t1.IdNO, 'U');
        }

        var t2Sql = poNumber is null
            ? @"SELECT TOP 1 LPMDT, OraPoNO, ResultType FROM dbo.WmsPCR WITH (NOLOCK)
                WHERE Country = @ct AND Contno = @c AND Itemcode = @i ORDER BY LPMDT"
            : @"SELECT TOP 1 LPMDT, OraPoNO, ResultType FROM dbo.WmsPCR WITH (NOLOCK)
                WHERE Country = @ct AND Contno = @c AND Itemcode = @i AND OraPoNO = @p ORDER BY LPMDT";
        var t2 = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            t2Sql, new { ct = country, c = contno, i = itemCode, p = poNumber }, transaction: tx, cancellationToken: ct));

        if (t2 is not null)
        {
            var newId = await InsertNewPcrAsync(c, tx, country, contno, itemCode, (string?)t2.OraPoNO ?? poNumber,
                (DateTime?)t2.LPMDT ?? DateTime.Now.Date, "SHOP", (string?)t2.ResultType, ct);
            await tx.CommitAsync(ct);
            return new AllocationResult(true, "SHOP", (DateTime?)t2.LPMDT, (string?)t2.OraPoNO,
                (string?)t2.ResultType, AllocationTier.Tier2_ExactNoQty, newId, 'I');
        }

        if (!string.IsNullOrEmpty(style))
        {
            var t3sql = @"SELECT TOP 1 LPMDT, OraPoNO, ResultType, Result FROM dbo.WmsPCR WITH (NOLOCK)
                  WHERE Country = @ct AND Contno = @c AND Style = @s AND (@p IS NULL OR OraPoNO = @p)
                  ORDER BY LPMDT";
            DbOpContext.Set("PCR Tier-3 (style fallback) on dbo.WmsPCR", t3sql);
            var t3 = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                t3sql, new { ct = country, c = contno, s = style, p = poNumber },
                transaction: tx, cancellationToken: ct));
            if (t3 is not null)
            {
                var newId = await InsertNewPcrAsync(c, tx, country, contno, itemCode, (string?)t3.OraPoNO ?? poNumber,
                    (DateTime?)t3.LPMDT ?? DateTime.Now.Date, (string)t3.Result ?? "SHOP", (string?)t3.ResultType, ct);
                await tx.CommitAsync(ct);
                return new AllocationResult(true, (string?)t3.Result ?? "SHOP", (DateTime?)t3.LPMDT,
                    (string?)t3.OraPoNO, (string?)t3.ResultType, AllocationTier.Tier3_StyleMatch, newId, 'I');
            }
        }

        var earliest = await c.QueryFirstOrDefaultAsync<(DateTime? Dt, string? Po, string? Pt)>(new CommandDefinition(
            @"SELECT TOP 1 LPMDT AS Dt, OraPoNO AS Po, ResultType AS Pt
              FROM dbo.WmsPCR WITH (NOLOCK)
              WHERE Country = @ct AND Contno = @c ORDER BY LPMDT",
            new { ct = country, c = contno }, transaction: tx, cancellationToken: ct));
        var dt = earliest.Dt ?? DateTime.Now.Date;
        var po = earliest.Po ?? poNumber;
        var pt = earliest.Pt;
        var id4 = await InsertNewPcrAsync(c, tx, country, contno, itemCode, po, dt, "SHOP", pt, ct);
        await tx.CommitAsync(ct);
        return new AllocationResult(true, "SHOP", dt, po, pt, AllocationTier.Tier4_NewItem, id4, 'I');
    }

    private async Task<long> InsertNewPcrAsync(SqlConnection c, SqlTransaction tx,
        string country, string contno, string itemCode, string? po, DateTime lpmDt,
        string result, string? palletType, CancellationToken ct)
    {
        // WmsPCR.IdNO is IDENTITY by design (Phase 2c install script).
        var sql = @"
            INSERT INTO dbo.WmsPCR
              (Country, Contno, Itemcode, OraPoNO, LPMDT, Result, ResultType, QtyIssue)
            VALUES (@ct, @c, @i, @p, @d, @r, @pt, 1);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";
        DbOpContext.Set("PCR INSERT on dbo.WmsPCR (IDENTITY)", sql);
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new { ct = country, c = contno, i = itemCode, p = po, d = lpmDt, r = result, pt = palletType },
            transaction: tx, cancellationToken: ct));
        return id;
    }

    // ==================== 6a. Find a matching open box (read-only) ====================
    public async Task<string?> FindMatchingOpenBoxAsync(
        string contno, string palletType, string? division, string? season, DateTime? lpmDt,
        CancellationToken ct = default)
    {
        await using var c = OpenWms();
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
        var country = Country;

        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var inMaster = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsBlueToteIDMaster WITH (NOLOCK) WHERE Country = @ct AND ToteID = @t",
            new { ct = country, t }, transaction: tx, cancellationToken: ct));
        if (inMaster != 1) return (false, $"Tote {t} is not in WmsBlueToteIDMaster.", "");

        var inUse = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsOpenBox WITH (UPDLOCK, HOLDLOCK) WHERE ToteID = @t",
            new { t }, transaction: tx, cancellationToken: ct));
        if (inUse == 1) return (false, $"Tote {t} is already attached to another open box.", "");

        var openLpm = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsUPCBoxHead WITH (NOLOCK) WHERE Country = @ct AND ToteID = @t AND Closed = 'N'",
            new { ct = country, t }, transaction: tx, cancellationToken: ct));
        if (openLpm == 1) return (false, $"Tote {t} is attached to an open box in WmsUPCBoxHead.", "");

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
        await using var c = OpenWms();
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

    // ==================== 6d. Stage an item into a known open box ====================
    public async Task<int> StageItemToBoxAsync(
        string boxNo, string itemCode, string? result, long? pcrId, char pcrAction,
        string? size, string? color, string? style, string? groupCode, string? season,
        CancellationToken ct = default)
    {
        await using var c = OpenWms();
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
        var country = Country;

        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var ownerCheck = await c.QueryFirstOrDefaultAsync<(string Cont, string Owner, string? Tote)>(new CommandDefinition(
            "SELECT Contno, UserId, ToteID FROM dbo.WmsOpenBox WITH (UPDLOCK, ROWLOCK) WHERE BoxNo = @b",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));
        if (ownerCheck.Cont is null) return (false, $"Box {boxNo} not found.");
        if (!string.Equals(ownerCheck.Owner, user.Name, StringComparison.OrdinalIgnoreCase))
            return (false, $"Box {boxNo} belongs to {ownerCheck.Owner}, not you.");

        var inMaster = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsBlueToteIDMaster WITH (NOLOCK) WHERE Country = @ct AND ToteID = @t",
            new { ct = country, t }, transaction: tx, cancellationToken: ct));
        if (inMaster != 1) return (false, $"Tote {t} is not in WmsBlueToteIDMaster.");

        var conflict = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsOpenBox WITH (UPDLOCK, HOLDLOCK) WHERE ToteID = @t AND BoxNo <> @b",
            new { t, b = boxNo }, transaction: tx, cancellationToken: ct));
        if (conflict == 1) return (false, $"Tote {t} is already attached to another open box.");

        var openLpm = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsUPCBoxHead WITH (NOLOCK) WHERE Country = @ct AND ToteID = @t AND Closed = 'N'",
            new { ct = country, t }, transaction: tx, cancellationToken: ct));
        if (openLpm == 1) return (false, $"Tote {t} is attached to an open box in WmsUPCBoxHead.");

        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.WmsOpenBox SET ToteID = @t WHERE BoxNo = @b",
            new { t, b = boxNo }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (true, null);
    }

    // ==================== 6f. Clear a box — reverse PCR effects + delete staging ====================
    public async Task<(bool Ok, string? Error, int ScansReversed)> ClearBoxAsync(string boxNo, CancellationToken ct = default)
    {
        var country = Country;
        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var owner = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT UserId FROM dbo.WmsOpenBox WITH (UPDLOCK, ROWLOCK) WHERE BoxNo = @b",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));
        if (owner is null) return (false, $"Box {boxNo} not found.", 0);
        if (!string.Equals(owner, user.Name, StringComparison.OrdinalIgnoreCase))
            return (false, $"Box {boxNo} belongs to {owner}, not you.", 0);

        var scans = (await c.QueryAsync<(long Id, string ItemCode, char PcrAction, long? PcrId)>(new CommandDefinition(
            "SELECT Id, ItemCode, PcrAction, PcrId FROM dbo.WmsOpenBoxScan WITH (UPDLOCK, ROWLOCK) WHERE BoxNo = @b ORDER BY Id DESC",
            new { b = boxNo }, transaction: tx, cancellationToken: ct))).AsList();

        var reversed = 0;
        foreach (var s in scans)
        {
            if (s.PcrId is null) continue;
            if (s.PcrAction == 'U')
            {
                await c.ExecuteAsync(new CommandDefinition(
                    @"UPDATE dbo.WmsPCR
                         SET QtyIssue = CASE WHEN ISNULL(QtyIssue,0) > 0 THEN QtyIssue - 1 ELSE 0 END
                       WHERE Country = @ct AND IdNO = @id",
                    new { ct = country, id = s.PcrId.Value }, transaction: tx, cancellationToken: ct));
                reversed++;
            }
            else if (s.PcrAction == 'I')
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM dbo.WmsPCR WHERE Country = @ct AND IdNO = @id",
                    new { ct = country, id = s.PcrId.Value }, transaction: tx, cancellationToken: ct));
                reversed++;
            }
        }

        await c.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.WmsOpenBoxScan  WHERE BoxNo = @b;
              DELETE FROM dbo.WmsOpenBoxItem  WHERE BoxNo = @b;
              DELETE FROM dbo.WmsOpenBox      WHERE BoxNo = @b;",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return (true, null, reversed);
    }

    // ==================== 6. Stage scan into open box (or open new) — legacy entry point ====================
    public async Task<StageScanResult> StageScanAsync(StageScanRequest req, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

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
        var country = Country;
        await using var c = OpenWms();
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
            "SELECT TOP 1 1 FROM dbo.WmsBlueToteIDMaster WITH (NOLOCK) WHERE Country = @ct AND ToteID = @t",
            new { ct = country, t = toteId }, transaction: tx, cancellationToken: ct));
        if (inMaster != 1) return (false, $"Tote {toteId} is not in WmsBlueToteIDMaster.");

        var otherOpen = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsOpenBox WITH (UPDLOCK, HOLDLOCK) WHERE ToteID = @t AND BoxNo <> @b",
            new { t = toteId, b = boxNo }, transaction: tx, cancellationToken: ct));
        if (otherOpen == 1) return (false, $"Tote {toteId} is already used by another open box.");

        var openLpm = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsUPCBoxHead WITH (NOLOCK) WHERE Country = @ct AND ToteID = @t AND Closed = 'N'",
            new { ct = country, t = toteId }, transaction: tx, cancellationToken: ct));
        if (openLpm == 1) return (false, $"Tote {toteId} is already attached to an open box in WmsUPCBoxHead.");

        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.WmsOpenBox SET ToteID = @t WHERE BoxNo = @b",
            new { t = toteId, b = boxNo }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (true, null);
    }

    // ==================== 8. Checkout — write WmsUPCBoxHead/Det/Photochecking + update PCR + clear staging ====================
    public async Task<CheckoutResult> CheckoutBoxAsync(string boxNo, string toteId, CancellationToken ct = default)
    {
        var country = Country;
        await using var c = OpenWms();
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

        // 1) WmsUPCBoxHead
        var headSql = @"INSERT INTO dbo.WmsUPCBoxHead
            (Country, BoxNo, TrnDate, Time1, NewPallet, PreparedBy, Remarks, Userid, PalletType, Closed,
             GroupCode, OldBoxNo, Prepared1, Prepared2, WHouse, FWType, FPreparedBy, FPalletType,
             ISize, Gender, ToteID, LPMDT)
          VALUES
            (@Country, @BoxNo, CAST(SYSDATETIME() AS DATE), CAST(SYSDATETIME() AS TIME(0)), 'Y', @CheckOut, 'from WMS', @CheckIn, @Pallet, 'N',
             '', '', @CheckOut, @Division, @WHouse, '', @CheckOut, @Pallet,
             '', '', @Tote, @LPMDt);";
        DbOpContext.Set("INSERT dbo.WmsUPCBoxHead (checkout step 1)", headSql);
        await c.ExecuteAsync(new CommandDefinition(headSql,
            new { Country = country, BoxNo = boxNo, CheckOut = checkoutUser, CheckIn = checkInUser, Pallet = palletType,
                  Division = division, WHouse = warehouse, Tote = toteId, LPMDt = lpmDt },
            transaction: tx, cancellationToken: ct));

        // 2) WmsUPCBoxDet (one row per item)
        var detSql = @"INSERT INTO dbo.WmsUPCBoxDet
            (Country, BoxNo, Itemcode, Qty, QtyIssued, SrNo, Status, UPC, imgfile)
          VALUES (@Country, @BoxNo, @Item, @Qty, 0, @SrNo, '', @Item, @Cont);";
        foreach (var it in items)
        {
            DbOpContext.Set("INSERT dbo.WmsUPCBoxDet (checkout step 2)", detSql);
            await c.ExecuteAsync(new CommandDefinition(detSql,
                new { Country = country, BoxNo = boxNo, Item = (string)it.ItemCode, Qty = (int)it.Qty,
                      SrNo = (int)it.SrNo, Cont = contno },
                transaction: tx, cancellationToken: ct));
        }

        // 3) WMSContBuilding — ONE ROW PER SCAN
        var photoSql = @"INSERT INTO dbo.WMSContBuilding
            (Country, ContNo, TrnDate, Time1, UPC, PhotoSize, Result, CheckedBy, CmpName, BoxSize,
             Photo, Style, Color, GroupCode, ItemName, Warehouse, PhotoCheckType, RRP,
             Logistics_BoxNo, Season, ToteID, RoboStatus, BarCode)
          VALUES
            (@Country, @Cont, CAST(SYSDATETIME() AS DATE), CAST(SYSDATETIME() AS TIME(0)), @Item, @Size, @Result, @User, @Pc, @Size,
             '', @Style, @Color, @Gc, '', @WHouse, '', 0,
             @Logi, @Season, @Tote, 'N', '');";
        foreach (var it in items)
        {
            var qty = (int)it.Qty;
            for (int i = 0; i < qty; i++)
            {
                DbOpContext.Set("INSERT dbo.WMSContBuilding (checkout step 3 — one per scan)", photoSql);
                await c.ExecuteAsync(new CommandDefinition(photoSql,
                    new { Country = country, Cont = contno, Item = (string)it.ItemCode, Size = (string?)it.Size ?? "",
                          Result = (string?)it.Result ?? "SHOP", User = checkoutUser, Pc = pcName,
                          Style = (string?)it.Style ?? "", Color = (string?)it.Color ?? "",
                          Gc = (string?)it.GroupCode ?? "", WHouse = warehouse,
                          Logi = logisticsBoxNo, Season = (string?)it.Season ?? season, Tote = toteId },
                    transaction: tx, cancellationToken: ct));
            }
        }

        // 4) Update WmsPCR.BoxNo for items in this box+container
        var pcrSql = @"UPDATE dbo.WmsPCR
            SET BoxNo = @BoxNo
            WHERE Country = @Country AND Contno = @Cont AND Itemcode = @Item AND (BoxNo IS NULL OR BoxNo = '')";
        var pcrUpdated = 0;
        foreach (var it in items)
        {
            DbOpContext.Set("UPDATE dbo.WmsPCR SET BoxNo (checkout step 4)", pcrSql);
            pcrUpdated += await c.ExecuteAsync(new CommandDefinition(pcrSql,
                new { Country = country, BoxNo = boxNo, Cont = contno, Item = (string)it.ItemCode },
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
        await using var c = OpenWms();
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
        await using var c = OpenWms();
        var rows = await c.QueryAsync<StagedItemRow>(new CommandDefinition(
            @"SELECT BoxNo, ItemCode, Qty, SrNo, Result, Size, Color, Style, GroupCode, Season
              FROM dbo.WmsOpenBoxItem WHERE BoxNo = @b ORDER BY SrNo",
            new { b = boxNo }, cancellationToken: ct));
        return rows.AsList();
    }

    // Countries now come from WmsWHMaster instead of bfldata.dbo.DataSettings.
    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT Country FROM dbo.WmsWHMaster
              WHERE Active = 1 ORDER BY Country", cancellationToken: ct));
        return list.AsList();
    }
}
