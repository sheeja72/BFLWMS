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
/// LPM Manual Building business logic — Phase C (ContAllocationData-driven).
///
/// Source of truth for allocation is now dbo.WMS_ContAllocationData on the
/// Azure WMS DB (synced from LPMSIM via the Data Sync feature). WmsPCR is
/// retired.
///
/// Every scan is recorded in dbo.WMSContBuildScanData (the scan ledger) —
/// one row per piece, carrying BoxNo + ToteID + StoreID + Tier + Manual flag
/// for full audit + Clear-Box reversal.
///
/// Allocation has 3 tiers:
///   - Tier 1: existing WMS_ContAllocationData row with QtyIssue &lt; Qty;
///             increment QtyIssue.
///   - Tier 2: item is in container but every row is full; pick the
///             StoreID with the highest remaining OTS (Division-scoped,
///             recomputed live against the scan ledger), insert a new row
///             with Qty=1, QtyIssue=1, Manual='N'.
///   - Tier 3: item NOT in container at all; look up usa.dbo.upcbarcodes on
///             OnPremBackup (UAE only). If not found, error. If found, pick
///             StoreID via OTS, insert with Manual='Y',
///             ItemSource='usa.upcbarcodes'.
///
/// OTS pick is "recompute on the fly" (Q6): the OTS column is never
/// decremented; each pick subtracts the count of non-reversed scans for that
/// StoreID + Division from the column value.
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

    /// <summary>UAE on-prem backup DB — read-only fallbacks (usa.dbo.upcbarcodes for
    /// Tier-3 new-item lookup; datareporting.dbo.vupc_subclass / SubclassMaster for
    /// hierarchy on items that aren't in the container).</summary>
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
        await using var c = OpenWms();

        var built = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WmsBuildingCompletion WITH (NOLOCK) WHERE Country = @ct AND ContNo = @c",
            new { ct = country, c = contno }, cancellationToken: ct));
        if (built == 1) return new(false, $"Container {contno} building is already completed.");

        var open = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP 1 Closed FROM dbo.WmsOpenUSACont WITH (NOLOCK) WHERE Country = @ct AND contno = @c",
            new { ct = country, c = contno }, cancellationToken: ct));
        if (open is null) return new(false, $"Container {contno} is not open in WmsOpenUSACont.");
        if (string.Equals(open, "Y", StringComparison.OrdinalIgnoreCase))
            return new(false, $"Container {contno} is closed in WmsOpenUSACont.");

        var hasAlloc = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WMS_ContAllocationData WITH (NOLOCK) WHERE ContNo = @c",
            new { c = contno }, cancellationToken: ct));
        if (hasAlloc != 1)
            return new(false, $"Container {contno} has no allocation data on Azure — run Data Sync first.");

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
            // PO validation now reads from WMS_ContAllocationData (sync source of truth).
            var poOk = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                @"SELECT TOP 1 1 FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
                  WHERE ContNo = @cont AND ORAPONo = @po",
                new { cont = contno, po = parsed.PoNumber }, cancellationToken: ct));
            if (poOk != 1) return new(false, $"PO {parsed.PoNumber} on box does not match container {contno}.", parsed.PoNumber, true);
        }
        return new(true, null, parsed.PoNumber, parsed.HasPo);
    }

    // ==================== 3. Item details ====================
    // Primary source = WMS_ContAllocationData (denormalised — has ItemName,
    // Style, Size, Color, Brand, Season, Gender, HsCode, Division, Department,
    // Class, Family, Subclass after Phase B enrichment).
    // Fallback for items not in the container = usa.dbo.upcbarcodes on
    // OnPremBackup, with hierarchy from datareporting.
    public async Task<ItemDetails?> GetItemDetailsAsync(string contno, string itemCode, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var inAlloc = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            @"SELECT TOP 1 Itemcode, Itemname, Style, [Size], Color, Brand, Season, Gender, HsCode,
                           GroupCode, Division, Department, [Class], Family, Subclass
              FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
              WHERE ContNo = @c AND Itemcode = @i",
            new { c = contno, i = itemCode }, cancellationToken: ct));

        if (inAlloc is not null)
        {
            return new ItemDetails(
                (string)inAlloc.Itemcode,
                (string?)inAlloc.Itemname,
                (string?)inAlloc.Style,
                (string?)inAlloc.Size,
                (string?)inAlloc.Color,
                (string?)inAlloc.Brand,
                (string?)inAlloc.Season,
                (string?)inAlloc.Gender,
                (string?)inAlloc.HsCode,
                Lpm: null,
                (string?)inAlloc.GroupCode,
                GroupName: null,
                (string?)inAlloc.Division,
                (string?)inAlloc.Department,
                (string?)inAlloc.Class,
                (string?)inAlloc.Family,
                (string?)inAlloc.Subclass,
                ItemAvailability.InContainer);
        }

        // Not in container — try the item master.
        var master = await LookupItemMasterAsync(itemCode, ct);
        if (master is null)
        {
            return new ItemDetails(itemCode, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, ItemAvailability.NotFound);
        }

        return new ItemDetails(
            itemCode,
            master.Itemname, master.Style, master.Size, master.Color,
            master.Brand, master.Season, master.Gender, master.HsCode,
            Lpm: null, GroupCode: null, GroupName: null,
            master.Division, master.Department, master.Class, master.Family, master.Subclass,
            ItemAvailability.InItemMaster);
    }

    // ==================== 4. Allocation resolution (3 tiers) ====================
    public async Task<AllocationResult> ResolveAllocationAsync(
        string contno, string itemCode, string? poNumber, string? style, CancellationToken ct = default)
    {
        // ----- Tier 1 + Tier 2 inside a single Azure WMS connection/tx -----
        await using (var c = OpenWms())
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var t1Sql = @"
                SELECT TOP 1 IdNo, Result, ResultType, LPMDt, ORAPONo, StoreID, Division
                  FROM dbo.WMS_ContAllocationData WITH (UPDLOCK, ROWLOCK)
                 WHERE ContNo = @c AND Itemcode = @i
                   AND (@p IS NULL OR ORAPONo = @p)
                   AND ISNULL(QtyIssue,0) < ISNULL(Qty,0)
                 ORDER BY ORAPONo, LPMDt, StoreID, ISNULL(OTS,0) DESC";
            DbOpContext.Set("Tier-1 lookup on dbo.WMS_ContAllocationData", t1Sql);
            var t1 = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                t1Sql, new { c = contno, i = itemCode, p = poNumber },
                transaction: tx, cancellationToken: ct));

            if (t1 is not null)
            {
                var id1 = (int)t1.IdNo;
                await c.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.WMS_ContAllocationData SET QtyIssue = ISNULL(QtyIssue,0) + 1 WHERE IdNo = @id",
                    new { id = id1 }, transaction: tx, cancellationToken: ct));
                var storeId1   = (string?)t1.StoreID;
                var storeName1 = await StoreNameByIdAsync(c, tx, storeId1, ct);
                var palletT1   = (string?)t1.ResultType;
                var palletN1   = await PalletTypeNameByCodeAsync(c, tx, palletT1, ct);
                await tx.CommitAsync(ct);
                return new AllocationResult(
                    Found: true,
                    Result: (string?)t1.Result ?? "SHOP",
                    LpmDt: (DateTime?)t1.LPMDt,
                    PoNumber: (string?)t1.ORAPONo,
                    PalletType: palletT1,
                    Tier: AllocationTier.Tier1_HasCapacity,
                    AllocationIdNo: id1,
                    Action: 'U',
                    StoreId: storeId1,
                    StoreName: storeName1,
                    Division: (string?)t1.Division,
                    Manual: false,
                    PalletTypeName: palletN1);
            }

            var anyInContainer = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                @"SELECT TOP 1 Division, Brand, Season, Style, [Size], Color, Itemname, GroupCode,
                               ResultType, LPMDt, ORAPONo, Department, HsCode, Gender,
                               [Class], Family, Subclass, SalesPrice, BuildingCategory, Country
                    FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
                   WHERE ContNo = @c AND Itemcode = @i
                   ORDER BY IdNo",
                new { c = contno, i = itemCode }, transaction: tx, cancellationToken: ct));

            if (anyInContainer is not null)
            {
                var division2 = (string?)anyInContainer.Division;
                var store2 = await PickBestStoreAsync(c, tx, contno, division2, ct);
                if (store2 is null)
                {
                    await tx.RollbackAsync(ct);
                    return new AllocationResult(false, "", null, null, null,
                        AllocationTier.Tier2_OtsOverflow, null, 'I', null, null, null, false,
                        Error: $"No stores with available OTS in container {contno} for division={division2 ?? "(null)"}. Cannot place overflow piece.");
                }

                var inserted2 = await InsertOverflowRowAsync(
                    c, tx, contno, itemCode, poNumber, store2, division2,
                    country:    (string?)anyInContainer.Country,
                    itemname:   (string?)anyInContainer.Itemname,
                    style:      (string?)anyInContainer.Style,
                    size:       (string?)anyInContainer.Size,
                    color:      (string?)anyInContainer.Color,
                    brand:      (string?)anyInContainer.Brand,
                    season:     (string?)anyInContainer.Season,
                    gender:     (string?)anyInContainer.Gender,
                    hsCode:     (string?)anyInContainer.HsCode,
                    groupCode:  (string?)anyInContainer.GroupCode,
                    klass:      (string?)anyInContainer.Class,
                    family:     (string?)anyInContainer.Family,
                    subclass:   (string?)anyInContainer.Subclass,
                    department: (string?)anyInContainer.Department,
                    resultType: (string?)anyInContainer.ResultType,
                    lpmDt:      (DateTime?)anyInContainer.LPMDt,
                    salesPrice: (decimal?)anyInContainer.SalesPrice,
                    buildingCategory: (string?)anyInContainer.BuildingCategory,
                    result: "SHOP",
                    manual: 'N',
                    itemSource: null,
                    ct: ct);

                var storeName2 = await StoreNameByIdAsync(c, tx, store2, ct);
                var palletT2   = (string?)anyInContainer.ResultType;
                var palletN2   = await PalletTypeNameByCodeAsync(c, tx, palletT2, ct);
                await tx.CommitAsync(ct);
                return new AllocationResult(
                    Found: true, Result: "SHOP",
                    LpmDt: (DateTime?)anyInContainer.LPMDt,
                    PoNumber: poNumber,
                    PalletType: palletT2,
                    Tier: AllocationTier.Tier2_OtsOverflow,
                    AllocationIdNo: inserted2,
                    Action: 'I',
                    StoreId: store2,
                    StoreName: storeName2,
                    Division: division2,
                    Manual: false,
                    PalletTypeName: palletN2);
            }

            // No item in this container at all — fall through to Tier 3 outside this tx.
            await tx.RollbackAsync(ct);
        }

        // ----- Tier 3: item NOT in container; look up usa.upcbarcodes -----
        var master = await LookupItemMasterAsync(itemCode, ct);
        if (master is null)
        {
            return new AllocationResult(false, "", null, null, null,
                AllocationTier.Tier3_ManualNewItem, null, 'I', null, null, null, false,
                Error: $"Item {itemCode} is not in container {contno} and not found in item master. Create it via Item Encoding before scanning.");
        }

        await using (var c3 = OpenWms())
        await using (var tx3 = (SqlTransaction)await c3.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var store3 = await PickBestStoreAsync(c3, tx3, contno, master.Division, ct);
            if (store3 is null)
            {
                await tx3.RollbackAsync(ct);
                return new AllocationResult(false, "", null, null, null,
                    AllocationTier.Tier3_ManualNewItem, null, 'I', null, null, null, true,
                    Error: $"Container {contno} has no store eligible for OTS allocation.");
            }

            var inserted3 = await InsertOverflowRowAsync(
                c3, tx3, contno, itemCode, poNumber, store3, master.Division,
                country:    Country,
                itemname:   master.Itemname,
                style:      master.Style,
                size:       master.Size,
                color:      master.Color,
                brand:      master.Brand,
                season:     master.Season,
                gender:     master.Gender,
                hsCode:     master.HsCode,
                groupCode:  null,
                klass:      master.Class,
                family:     master.Family,
                subclass:   master.Subclass,
                department: master.Department,
                resultType: null,
                lpmDt:      null,
                salesPrice: null,
                buildingCategory: master.Division,
                result: "SHOP",
                manual: 'Y',
                itemSource: "usa.upcbarcodes",
                ct: ct);

            var storeName3 = await StoreNameByIdAsync(c3, tx3, store3, ct);
            await tx3.CommitAsync(ct);
            return new AllocationResult(
                Found: true, Result: "SHOP",
                LpmDt: null, PoNumber: poNumber, PalletType: null,
                Tier: AllocationTier.Tier3_ManualNewItem,
                AllocationIdNo: inserted3,
                Action: 'I',
                StoreId: store3,
                StoreName: storeName3,
                Division: master.Division,
                Manual: true);
        }
    }

    /// <summary>PBFullname lookup on dbo.WMS_DataSettings for a StoreID. Used
    /// by ResolveAllocationAsync to enrich the AllocationResult and by
    /// GetTodayScansAsync via an OUTER APPLY on the activity grid query.</summary>
    private async Task<string?> StoreNameByIdAsync(SqlConnection c, SqlTransaction? tx, string? storeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storeId)) return null;
        return await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            @"SELECT TOP 1 PBFullname FROM dbo.WMS_DataSettings WITH (NOLOCK)
              WHERE StoreID = @s AND PBFullname IS NOT NULL AND LTRIM(RTRIM(PBFullname)) <> ''",
            new { s = storeId }, transaction: tx, cancellationToken: ct));
    }

    /// <summary>TypeName lookup on dbo.WmsPalletType for a PalletType code.
    /// Used by ResolveAllocationAsync to enrich the AllocationResult and by
    /// the open-box / today-scan queries via OUTER APPLY.</summary>
    private async Task<string?> PalletTypeNameByCodeAsync(SqlConnection c, SqlTransaction? tx, string? palletType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(palletType)) return null;
        return await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            @"SELECT TOP 1 TypeName FROM dbo.WmsPalletType WITH (NOLOCK)
              WHERE PalletType = @p AND TypeName IS NOT NULL AND LTRIM(RTRIM(TypeName)) <> ''",
            new { p = palletType }, transaction: tx, cancellationToken: ct));
    }

    // ----- helper: OTS pick (recompute on the fly, division-scoped with container-wide fallback) -----
    private async Task<string?> PickBestStoreAsync(
        SqlConnection c, SqlTransaction tx, string contno, string? division, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(division))
        {
            var divSql = @"
                ;WITH StoreOts AS (
                    SELECT a.StoreID, MAX(ISNULL(a.OTS, 0)) AS OtsSeed
                      FROM dbo.WMS_ContAllocationData a WITH (NOLOCK)
                     WHERE a.ContNo = @c AND a.Division = @d AND a.StoreID IS NOT NULL
                     GROUP BY a.StoreID
                ),
                DivScans AS (
                    SELECT s.StoreID, COUNT_BIG(*) AS DivScanned
                      FROM dbo.WMSContBuildScanData s WITH (NOLOCK)
                     WHERE s.ContNo = @c AND s.Division = @d AND s.Reversed = 'N'
                     GROUP BY s.StoreID
                ),
                AnyScans AS (
                    SELECT s.StoreID, COUNT_BIG(*) AS TotalScanned
                      FROM dbo.WMSContBuildScanData s WITH (NOLOCK)
                     WHERE s.ContNo = @c AND s.Reversed = 'N'
                     GROUP BY s.StoreID
                )
                SELECT TOP 1 o.StoreID
                  FROM StoreOts o
                  LEFT JOIN DivScans d ON d.StoreID = o.StoreID
                  LEFT JOIN AnyScans a ON a.StoreID = o.StoreID
                 ORDER BY (o.OtsSeed - ISNULL(d.DivScanned,0)) DESC,
                          ISNULL(a.TotalScanned,0) ASC,
                          o.StoreID ASC;";
            DbOpContext.Set("OTS pick (division-scoped)", divSql);
            var divPick = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                divSql, new { c = contno, d = division },
                transaction: tx, cancellationToken: ct));
            if (divPick is not null) return divPick;
        }

        var fbSql = @"
            ;WITH StoreOts AS (
                SELECT a.StoreID, MAX(ISNULL(a.OTS, 0)) AS OtsSeed
                  FROM dbo.WMS_ContAllocationData a WITH (NOLOCK)
                 WHERE a.ContNo = @c AND a.StoreID IS NOT NULL
                 GROUP BY a.StoreID
            ),
            AnyScans AS (
                SELECT s.StoreID, COUNT_BIG(*) AS TotalScanned
                  FROM dbo.WMSContBuildScanData s WITH (NOLOCK)
                 WHERE s.ContNo = @c AND s.Reversed = 'N'
                 GROUP BY s.StoreID
            )
            SELECT TOP 1 o.StoreID
              FROM StoreOts o
              LEFT JOIN AnyScans a ON a.StoreID = o.StoreID
             ORDER BY (o.OtsSeed - ISNULL(a.TotalScanned,0)) DESC,
                      ISNULL(a.TotalScanned,0) ASC,
                      o.StoreID ASC;";
        DbOpContext.Set("OTS pick (container-wide fallback)", fbSql);
        return await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            fbSql, new { c = contno },
            transaction: tx, cancellationToken: ct));
    }

    // ----- helper: insert a Tier 2/3 overflow row into WMS_ContAllocationData -----
    private async Task<int> InsertOverflowRowAsync(
        SqlConnection c, SqlTransaction tx,
        string contno, string itemCode, string? poNumber,
        string storeId, string? division,
        string? country,
        string? itemname, string? style, string? size, string? color,
        string? brand, string? season, string? gender, string? hsCode,
        string? groupCode, string? klass, string? family, string? subclass,
        string? department, string? resultType, DateTime? lpmDt,
        decimal? salesPrice, string? buildingCategory,
        string result, char manual, string? itemSource,
        CancellationToken ct)
    {
        var sql = @"
            INSERT INTO dbo.WMS_ContAllocationData
                (ContNo, Country, TrnDate, Itemcode, Itemname, Style, [Size], Color, Brand, Season, Gender,
                 HsCode, GroupCode, Division, Department, [Class], Family, Subclass, ORAPONo,
                 ResultType, LPMDt, StoreID, Qty, AllocatedQty, QtyIssue, Result, SalesPrice,
                 BuildingCategory, Manual, ItemSource)
            OUTPUT INSERTED.IdNo
            VALUES
                (@c, @country, CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE), @i, @itemname, @style, @size, @color, @brand, @season, @gender,
                 @hsCode, @groupCode, @division, @department, @klass, @family, @subclass, @p,
                 @resultType, @lpmDt, @store, 1, 1, 1, @result, @salesPrice,
                 @buildingCategory, @manual, @itemSource);";

        DbOpContext.Set("INSERT overflow row on dbo.WMS_ContAllocationData", sql);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new
            {
                c = contno, country, i = itemCode, itemname, style, size, color, brand, season,
                gender, hsCode, groupCode, division, department, klass, family, subclass, p = poNumber,
                resultType, lpmDt, store = storeId,
                result, salesPrice, buildingCategory,
                manual = manual.ToString(),
                itemSource
            },
            transaction: tx, cancellationToken: ct));
    }

    // ----- helper: usa.upcbarcodes + datareporting hierarchy lookup (Tier-3 fallback) -----
    private async Task<ItemMasterRow?> LookupItemMasterAsync(string itemCode, CancellationToken ct)
    {
        await using var b = OpenOnPremBackup();
        var head = await b.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            @"SELECT TOP 1 ItemName = itemname, Style = style, [Size] = size, Color = color,
                           Brand = brand, Season = season, Gender = GENDER, HsCode = hscode
                FROM usa.dbo.upcbarcodes WITH (NOLOCK)
               WHERE itemcode = @i",
            new { i = itemCode }, cancellationToken: ct));

        if (head is null) return null;

        string? division = null, dept = null, klass = null, family = null, subclass = null;
        var sub = await b.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            @"SELECT TOP 1 sm.Division, sm.Department, sm.[Class], sm.Family, sm.Subclass
                FROM datareporting.dbo.vupc_subclass v WITH (NOLOCK)
                LEFT JOIN datareporting.dbo.SubclassMaster sm WITH (NOLOCK) ON sm.MH4ID = v.MH4ID
               WHERE v.itemcode = @i",
            new { i = itemCode }, cancellationToken: ct));
        if (sub is not null)
        {
            division = (string?)sub.Division;
            dept     = (string?)sub.Department;
            klass    = (string?)sub.Class;
            family   = (string?)sub.Family;
            subclass = (string?)sub.Subclass;
        }

        return new ItemMasterRow(
            Itemname:   (string?)head.ItemName,
            Style:      (string?)head.Style,
            Size:       (string?)head.Size,
            Color:      (string?)head.Color,
            Brand:      (string?)head.Brand,
            Season:     (string?)head.Season,
            Gender:     (string?)head.Gender,
            HsCode:     (string?)head.HsCode,
            Division:   division,
            Department: dept,
            Class:      klass,
            Family:     family,
            Subclass:   subclass);
    }

    private sealed record ItemMasterRow(
        string? Itemname, string? Style, string? Size, string? Color,
        string? Brand, string? Season, string? Gender, string? HsCode,
        string? Division, string? Department, string? Class, string? Family, string? Subclass);

    // ==================== 5. Find a matching open box (read-only) ====================
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

    // ==================== 6. Open a new box with tote attached up front ====================
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
              WHEN MATCHED THEN UPDATE SET NextSeq = NextSeq + 1, UpdatedTS = DATEADD(hour, 4, SYSUTCDATETIME())
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

    // ==================== 7. Find existing OR create a new open box (no tote required up front) ====================
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
              WHEN MATCHED THEN UPDATE SET NextSeq = NextSeq + 1, UpdatedTS = DATEADD(hour, 4, SYSUTCDATETIME())
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

    // ==================== 8. Stage an item into a known open box + record in scan ledger ====================
    public async Task<int> StageItemToBoxAsync(
        string contno, string boxNo, string itemCode,
        AllocationResult alloc, ItemDetails item,
        CancellationToken ct = default)
    {
        if (alloc.AllocationIdNo is null)
            throw new InvalidOperationException("StageItemToBoxAsync called with AllocationResult.AllocationIdNo = null.");

        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // 1) Per-(box,item) aggregate for the UI grid.
        var stageSql = @"
            DECLARE @sr INT;
            SELECT @sr = SrNo FROM dbo.WmsOpenBoxItem WHERE BoxNo = @b AND ItemCode = @i;
            IF @sr IS NULL
            BEGIN
                SELECT @sr = ISNULL(MAX(SrNo),0) + 1 FROM dbo.WmsOpenBoxItem WHERE BoxNo = @b;
                INSERT INTO dbo.WmsOpenBoxItem
                  (BoxNo, ItemCode, Qty, SrNo, Result, PCRowId, Size, Color, Style, GroupCode, Season)
                VALUES (@b, @i, 1, @sr, @r, @alloc, @sz, @co, @st, @gc, @se);
            END
            ELSE
            BEGIN
                UPDATE dbo.WmsOpenBoxItem SET Qty = Qty + 1, ScannedTS = DATEADD(hour, 4, SYSUTCDATETIME())
                 WHERE BoxNo = @b AND ItemCode = @i;
            END
            SELECT @sr;";
        DbOpContext.Set("Stage item — WmsOpenBoxItem upsert", stageSql);
        var srNo = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            stageSql,
            new
            {
                b = boxNo, i = itemCode, r = alloc.Result,
                alloc = alloc.AllocationIdNo.Value,
                sz = item.Size, co = item.Color, st = item.Style,
                gc = item.GroupCode, se = item.Season
            },
            transaction: tx, cancellationToken: ct));

        // 2) Scan ledger row (one per piece — the audit + reversal source).
        var tote = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP 1 ToteID FROM dbo.WmsOpenBox WITH (NOLOCK) WHERE BoxNo = @b",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));

        var ledgerSql = @"
            INSERT INTO dbo.WMSContBuildScanData
                (Country, ContNo, Itemcode, StoreID, Result, Division, BoxNo, ToteID,
                 AllocationIdNo, Tier, Manual, ScannedBy)
            VALUES
                (@country, @c, @i, @store, @result, @division, @b, @tote,
                 @alloc, @tier, @manual, @user);";
        DbOpContext.Set("Insert scan ledger row on dbo.WMSContBuildScanData", ledgerSql);
        await c.ExecuteAsync(new CommandDefinition(
            ledgerSql,
            new
            {
                country  = Country,
                c        = contno,
                i        = itemCode,
                store    = alloc.StoreId,
                result   = alloc.Result,
                division = alloc.Division,
                b        = boxNo,
                tote,
                alloc    = alloc.AllocationIdNo.Value,
                tier     = (byte)alloc.Tier,
                manual   = alloc.Manual ? "Y" : (string?)null,
                user     = user.Name
            },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return srNo;
    }

    // ==================== 9. Attach a tote to an existing open box ====================
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

        // Backfill ToteID on this box's unreversed ledger rows so the audit shows
        // the tote that actually held the piece.
        await c.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.WMSContBuildScanData
                 SET ToteID = @t
               WHERE BoxNo = @b AND Reversed = 'N' AND (ToteID IS NULL OR ToteID = '')",
            new { t, b = boxNo }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return (true, null);
    }

    // ==================== 10. Clear a box — reverse scan ledger effects + delete staging ====================
    public async Task<(bool Ok, string? Error, int ScansReversed)> ClearBoxAsync(string boxNo, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var owner = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT UserId FROM dbo.WmsOpenBox WITH (UPDLOCK, ROWLOCK) WHERE BoxNo = @b",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));
        if (owner is null) return (false, $"Box {boxNo} not found.", 0);
        if (!string.Equals(owner, user.Name, StringComparison.OrdinalIgnoreCase))
            return (false, $"Box {boxNo} belongs to {owner}, not you.", 0);

        var scans = (await c.QueryAsync<(long ScanId, int AllocationIdNo, byte Tier)>(new CommandDefinition(
            @"SELECT ScanId, AllocationIdNo, Tier
                FROM dbo.WMSContBuildScanData WITH (UPDLOCK, ROWLOCK)
               WHERE BoxNo = @b AND Reversed = 'N'
               ORDER BY ScanId DESC",
            new { b = boxNo }, transaction: tx, cancellationToken: ct))).AsList();

        var reversed = 0;
        foreach (var s in scans)
        {
            if (s.Tier == (byte)AllocationTier.Tier1_HasCapacity)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    @"UPDATE dbo.WMS_ContAllocationData
                         SET QtyIssue = CASE WHEN ISNULL(QtyIssue,0) > 0 THEN QtyIssue - 1 ELSE 0 END
                       WHERE IdNo = @id",
                    new { id = s.AllocationIdNo }, transaction: tx, cancellationToken: ct));
            }
            else // Tier 2 / Tier 3 — overflow row was inserted with Qty=1, QtyIssue=1.
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM dbo.WMS_ContAllocationData WHERE IdNo = @id",
                    new { id = s.AllocationIdNo }, transaction: tx, cancellationToken: ct));
            }
            reversed++;
        }

        await c.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.WMSContBuildScanData
                 SET Reversed = 'Y', ReversedTS = DATEADD(hour, 4, SYSUTCDATETIME()), ReversedBy = @u
               WHERE BoxNo = @b AND Reversed = 'N'",
            new { b = boxNo, u = user.Name }, transaction: tx, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.WmsOpenBoxItem WHERE BoxNo = @b;
              DELETE FROM dbo.WmsOpenBox     WHERE BoxNo = @b;",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return (true, null, reversed);
    }

    // ==================== 11. Checkout — write LPM tables + drop staging ====================
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
        var lpmDt = (DateTime?)box.LPMDt ?? DateTime.UtcNow.AddHours(4).Date;
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
            (@Country, @BoxNo, CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE), CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS TIME(0)), 'Y', @CheckOut, 'from WMS', @CheckIn, @Pallet, 'N',
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
            (@Country, @Cont, CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE), CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS TIME(0)), @Item, @Size, @Result, @User, @Pc, @Size,
             '', @Style, @Color, @Gc, '', @WHouse, '', 0,
             @Logi, @Season, @Tote, 'N', '');";
        var photoRows = 0;
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
                photoRows++;
            }
        }

        // 4) Clear local staging. Scan ledger rows stay — they're the permanent audit trail.
        DbOpContext.Set("DELETE WMS staging (checkout step 4)", "DELETE WmsOpenBoxItem/Box");
        await c.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.WmsOpenBoxItem WHERE BoxNo = @b;
              DELETE FROM dbo.WmsOpenBox     WHERE BoxNo = @b;",
            new { b = boxNo }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new(true, null, photoRows);
    }

    // ==================== 12. Read open boxes (resume after reload) ====================
    public async Task<List<OpenBoxRow>> GetOpenBoxesForUserAsync(string contno, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<OpenBoxRow>(new CommandDefinition(
            @"SELECT b.BoxNo AS BoxNumber, b.Division, b.PalletType, pt.TypeName AS PalletTypeName,
                     b.Season, b.LPMDt AS LpmDt, b.ToteID AS ToteId,
                     ISNULL((SELECT SUM(Qty) FROM dbo.WmsOpenBoxItem i WHERE i.BoxNo = b.BoxNo),0) AS ItemQty
              FROM dbo.WmsOpenBox b
              OUTER APPLY (
                   SELECT TOP 1 TypeName FROM dbo.WmsPalletType WITH (NOLOCK) WHERE PalletType = b.PalletType
              ) pt
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

    // Countries from WmsWHMaster (Azure WMS).
    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT Country FROM dbo.WmsWHMaster
              WHERE Active = 1 ORDER BY Country", cancellationToken: ct));
        return list.AsList();
    }

    /// <summary>All SIM countries from bfldata.dbo.DataSettings via OnPremBackup —
    /// used by admin pages (WH Master, Users) that need the full list before
    /// any warehouse has been registered for a country.</summary>
    public async Task<List<string>> GetAllSimCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT SIMCountry
                FROM bfldata.dbo.DataSettings
               WHERE SIMCountry IS NOT NULL AND LTRIM(RTRIM(SIMCountry)) <> ''
               ORDER BY SIMCountry", cancellationToken: ct));
        return list.AsList();
    }

    // ==================== 13. My Activity Today (LPM Manual Building) ====================
    /// <summary>Today's scans by the current user across all containers.
    /// Joins WMS_DataSettings.PBFullname (StoreID) for the StoreName column.
    /// Newest scan first; reversed rows excluded. When `contno` is provided,
    /// filters to that container (no day filter — full history for the
    /// container). When `contno` is null/empty, keeps the legacy behaviour of
    /// "today's scans across all containers" so callers don't break.</summary>
    public async Task<List<TodayScanRow>> GetTodayScansAsync(int top = 200, string? contno = null, CancellationToken ct = default)
    {
        var hasContno = !string.IsNullOrWhiteSpace(contno);
        var filterClause = hasContno
            ? "AND s.ContNo = @c"
            : "AND CAST(s.ScannedTS AS DATE) = CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE)";

        await using var c = OpenWms();
        var rows = await c.QueryAsync<TodayScanRow>(new CommandDefinition($@"
            SELECT TOP ({top})
                   s.ScanId, s.ScannedTS, s.ContNo, s.Itemcode, s.Result,
                   s.StoreID, ds.PBFullname AS StoreName, s.Division,
                   s.BoxNo, s.ToteID, s.Tier, s.Manual,
                   ob.PalletType, pt.TypeName AS PalletTypeName,
                   ob.LogisticsBoxNo
              FROM dbo.WMSContBuildScanData s WITH (NOLOCK)
              OUTER APPLY (
                   SELECT TOP 1 PBFullname FROM dbo.WMS_DataSettings WITH (NOLOCK)
                    WHERE StoreID = s.StoreID
                      AND PBFullname IS NOT NULL AND LTRIM(RTRIM(PBFullname)) <> ''
              ) ds
              OUTER APPLY (
                   SELECT TOP 1 PalletType, LogisticsBoxNo FROM dbo.WmsOpenBox WITH (NOLOCK)
                    WHERE BoxNo = s.BoxNo
              ) ob
              OUTER APPLY (
                   SELECT TOP 1 TypeName FROM dbo.WmsPalletType WITH (NOLOCK)
                    WHERE PalletType = ob.PalletType
              ) pt
             WHERE s.ScannedBy = @u
               AND s.Reversed = 'N'
               {filterClause}
             ORDER BY s.ScanId DESC",
            new { u = user.Name, c = contno }, cancellationToken: ct));
        return rows.AsList();
    }

    // ==================== 14. Close Logistics Box ====================
    /// <summary>How many non-reversed pieces have been scanned into WmsOpenBox(es)
    /// whose LogisticsBoxNo matches the given logistics-box label for this
    /// container. Used by the LpmManualBuilding "Close Logistics" confirm dialog.</summary>
    public async Task<int> GetLogisticsBoxScanCountAsync(string contno, string logisticsBoxNo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(logisticsBoxNo)) return 0;
        await using var c = OpenWms();
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            @"SELECT COUNT_BIG(*)
                FROM dbo.WMSContBuildScanData s WITH (NOLOCK)
                JOIN dbo.WmsOpenBox b WITH (NOLOCK) ON b.BoxNo = s.BoxNo
               WHERE s.ContNo = @c AND b.LogisticsBoxNo = @lb AND s.Reversed = 'N'",
            new { c = contno, lb = logisticsBoxNo }, cancellationToken: ct));
    }

    /// <summary>Close the SIM-side logistics box: writes one audit row to
    /// dbo.WmsLogisticsBoxClosure_Log and flips dbo.WmsKNBBoxes.closed='Y'
    /// for (Country + Contno + Boxno). Returns the piece count that was
    /// recorded in the log row.</summary>
    public async Task<CloseLogisticsResult> CloseLogisticsBoxAsync(string contno, string logisticsBoxNo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno) || string.IsNullOrWhiteSpace(logisticsBoxNo))
            return new(false, "Container and logistics box are required.", 0);

        var country = Country;
        logisticsBoxNo = logisticsBoxNo.Trim();
        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var current = await c.QueryFirstOrDefaultAsync<(string? Boxno, string? closed)>(new CommandDefinition(
            @"SELECT Boxno, closed FROM dbo.WmsKNBBoxes WITH (UPDLOCK, ROWLOCK)
              WHERE Country = @ct AND Contno = @c AND Boxno = @b",
            new { ct = country, c = contno, b = logisticsBoxNo }, transaction: tx, cancellationToken: ct));
        if (current.Boxno is null) return new(false, $"Logistics box {logisticsBoxNo} not found in WmsKNBBoxes.", 0);
        if (string.Equals(current.closed, "Y", StringComparison.OrdinalIgnoreCase))
            return new(false, $"Logistics box {logisticsBoxNo} is already closed.", 0);

        var pcs = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            @"SELECT COUNT_BIG(*)
                FROM dbo.WMSContBuildScanData s WITH (NOLOCK)
                JOIN dbo.WmsOpenBox b WITH (NOLOCK) ON b.BoxNo = s.BoxNo
               WHERE s.ContNo = @c AND b.LogisticsBoxNo = @b AND s.Reversed = 'N'",
            new { c = contno, b = logisticsBoxNo }, transaction: tx, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.WmsLogisticsBoxClosure_Log
                  (Country, ContNo, Boxno, PcsScanned, ClosedBy)
              VALUES (@ct, @c, @b, @pcs, @u)",
            new { ct = country, c = contno, b = logisticsBoxNo, pcs, u = user.Name },
            transaction: tx, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.WmsKNBBoxes SET closed = 'Y'
               WHERE Country = @ct AND Contno = @c AND Boxno = @b",
            new { ct = country, c = contno, b = logisticsBoxNo },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new(true, null, pcs);
    }
}
