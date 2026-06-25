using System.Data;
using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Container Allocation Process service.
///
/// Phase 1 (this file): UI inputs (Country, WH, Contno), "Load PO Data"
/// grid from usa.usaorgfile_LPM, and a multi-step Process validation
/// (Contreceipt + KNBBoxes + 3-way qty match + not-yet-building + not-yet-completed).
///
/// Phase 2 (TBD): the actual process — writes to LPMSIM.dbo.WMS_ContAllocationData.
///
/// Connections used:
///   - OnPremBackupDB (the UAE backup) — hosts usa, bfldata, hodata, LPMSIM
///     databases via 3-part naming. Used for every validation read + the
///     Phase-2 insert.
///   - Azure WMS DB — for WmsOpenBox (is anyone already building?) and
///     WmsBuildingCompletion (is the container already completed?).
/// </summary>
public class ContainerAllocationService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
    // Default 15s post-login timeout is too tight when the on-prem SQL is busy
    // (saw a 14003ms post-login on a real Process call). Bump to 60s for this
    // service only — every other caller stays on the configured default.
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 60;

    private static string WithConnectTimeout(string cs)
    {
        var b = new SqlConnectionStringBuilder(cs) { ConnectTimeout = ConnectTimeoutSeconds };
        return b.ConnectionString;
    }

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetOnPremBackupConnectionString()));
        c.Open();
        return c;
    }

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetWmsAzureConnectionString()));
        c.Open();
        return c;
    }

    // ===================== Load PO Data =====================
    public async Task<List<PoDataRow>> LoadPoDataAsync(string contno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return new();
        contno = contno.Trim();

        await using var c = OpenOnPremBackup();
        // ContReceiptDT comes from bfldata.dbo.Contreceipt joined on TCMNo.
        // Buyer / LPM / Division / orgqty come from usa.dbo.usaorgfile_LPM.
        // Column names follow the existing on-prem schema — adjust if any
        // are slightly different (e.g. Vendor vs Buyer).
        // Sources:
        //   usaorgfile_LPM   — ContNo, OraPONo, LPM, ItemCode, orgqty
        //   Contreceipt      — ReceiptDt (via TCMNo)
        //   vUSAOrder        — OthersPath = Buyer, country = DestCountry (subqueries)
        //   vupc_subclass    — Division (via itemcode)
        //   USAOrgFile       — vendor = Brand (via ContNo + itemcode)
        var rows = await c.QueryAsync<PoDataRow>(new CommandDefinition(@"
            SELECT
                u.ContNo                              AS Contno,
                MAX(cr.ReceiptDt)                     AS ContReceiptDT,
                u.OraPONo                             AS PONO,
                u.LPM                                 AS LPM,
                (SELECT TOP 1 OthersPath FROM hodata.dbo.vUSAOrder WHERE refno = u.ContNo)  AS Buyer,
                MAX(sub.Division)                     AS Division,
                MAX(org.vendor)                       AS Brand,
                CAST(ISNULL(SUM(u.orgqty), 0) AS INT) AS Qty,
                (SELECT TOP 1 country     FROM hodata.dbo.vUSAOrder WHERE refno = u.ContNo) AS DestCountry
            FROM usa.dbo.usaorgfile_LPM u WITH (NOLOCK)
            LEFT JOIN bfldata.dbo.Contreceipt cr           WITH (NOLOCK) ON cr.TCMNo    = u.ContNo
            LEFT JOIN datareporting.dbo.vupc_subclass sub  WITH (NOLOCK) ON sub.itemcode = u.ItemCode
            LEFT JOIN usa.dbo.USAOrgFile org               WITH (NOLOCK) ON org.ContNo  = u.ContNo AND org.itemcode = u.ItemCode
            WHERE u.ContNo = @contno
            GROUP BY u.ContNo, u.OraPONo, u.LPM
            ORDER BY u.OraPONo, u.LPM",
            new { contno }, cancellationToken: ct));
        return rows.AsList();
    }

    // ===================== Validate (Process button — Phase 1) =====================
    // 5 logical steps, but only 4 round-trips:
    //   1) Contreceipt.TCMNo                              — 1 round-trip (gate)
    //   2) usa.KNBBoxes                                   — 1 round-trip (gate)
    //   3) 3-way qty sums                                 — 1 round-trip (3 sub-selects)
    //   4+5) WmsOpenBox + WmsBuildingCompletion           — 1 round-trip (combined)
    public async Task<ContainerAllocationValidationResult> ValidateAsync(
        string country, string contno,
        IProgress<AllocationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var steps = new List<ValidationStep>();
        if (string.IsNullOrWhiteSpace(contno))
        {
            steps.Add(new ValidationStep("Inputs", false, "Container number is required."));
            return new ContainerAllocationValidationResult(false, steps);
        }
        if (string.IsNullOrWhiteSpace(country))
        {
            steps.Add(new ValidationStep("Inputs", false, "Country is required."));
            return new ContainerAllocationValidationResult(false, steps);
        }
        contno = contno.Trim();
        const int TOTAL = 5;

        await using (var c = OpenOnPremBackup())
        {
            // 1. Contreceipt.TCMNo
            progress?.Report(new AllocationProgress(1, TOTAL, "Validating: Contreceipt"));
            var ok = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM bfldata.dbo.Contreceipt WITH (NOLOCK) WHERE TCMNo = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)) == 1;
            steps.Add(new ValidationStep(
                "Container exists in bfldata.Contreceipt (TCMNo)",
                ok,
                ok ? null : $"No row in bfldata.Contreceipt with TCMNo = '{contno}'."));
            if (!ok) return new ContainerAllocationValidationResult(false, steps);

            // 2. usa.KNBBoxes
            progress?.Report(new AllocationProgress(2, TOTAL, "Validating: KNBBoxes"));
            var ok2 = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM usa.dbo.KNBBoxes WITH (NOLOCK) WHERE contno = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)) == 1;
            steps.Add(new ValidationStep(
                "Container exists in usa.KNBBoxes",
                ok2,
                ok2 ? null : $"No row in usa.KNBBoxes with contno = '{contno}'."));
            if (!ok2) return new ContainerAllocationValidationResult(false, steps);

            // 3. Three-way qty match — combined into ONE round-trip (3 sub-selects).
            progress?.Report(new AllocationProgress(3, TOTAL, "Validating: three-way qty match"));
            var qty = await c.QueryFirstAsync<(int Q1, int Q2, int Q3)>(new CommandDefinition(@"
                SELECT
                    (SELECT ISNULL(SUM(orgqty),0) FROM usa.dbo.USAOrgFile     WITH (NOLOCK) WHERE ContNo = @c) AS Q1,
                    (SELECT ISNULL(SUM(orgqty),0) FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK) WHERE ContNo = @c) AS Q2,
                    (SELECT ISNULL(SUM(qty),0)    FROM hodata.dbo.vUSAOrder   WITH (NOLOCK) WHERE refno  = @c) AS Q3",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            var qtyOk = qty.Q1 > 0 && qty.Q1 == qty.Q2 && qty.Q2 == qty.Q3;
            steps.Add(new ValidationStep(
                "Three-way qty match (USAOrgFile = usaorgfile_LPM = hodata.vUSAOrder)",
                qtyOk,
                qtyOk ? $"All three = {qty.Q1}." : $"USAOrgFile={qty.Q1}, usaorgfile_LPM={qty.Q2}, vUSAOrder={qty.Q3} — must be > 0 and equal."));
            if (!qtyOk) return new ContainerAllocationValidationResult(false, steps);
        }

        // 4+5: WmsOpenBox + WmsBuildingCompletion — combined into ONE round-trip.
        progress?.Report(new AllocationProgress(4, TOTAL, "Validating: build status"));
        await using (var w = OpenWms())
        {
            var status = await w.QueryFirstAsync<(int Building, int Completed)>(new CommandDefinition(@"
                SELECT
                    (SELECT COUNT(*) FROM dbo.WmsOpenBox            WITH (NOLOCK) WHERE Contno = @c)                                 AS Building,
                    (SELECT COUNT(*) FROM dbo.WmsBuildingCompletion WITH (NOLOCK) WHERE Country = @ct AND ContNo = @c)                AS Completed",
                new { ct = country, c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var building = status.Building > 0;
            steps.Add(new ValidationStep(
                "Container not yet being built (no WmsOpenBox row)",
                !building,
                building ? $"Container {contno} already has open box(es) in WmsOpenBox." : null));
            if (building) return new ContainerAllocationValidationResult(false, steps);

            progress?.Report(new AllocationProgress(5, TOTAL, "Validating: completion status"));
            var completed = status.Completed > 0;
            steps.Add(new ValidationStep(
                "Container not yet completed (no WmsBuildingCompletion row)",
                !completed,
                completed ? $"Container {contno} already completed for country {country}." : null));
            if (completed) return new ContainerAllocationValidationResult(false, steps);
        }

        return new ContainerAllocationValidationResult(true, steps);
    }

    // ===================== Process — preview allocation =====================
    // Walks each PO line item, looks up DivCode from vupc_subclass, finds
    // eligible stores via LPM_EOM_Output × LPM_SKUMaxRule (current month),
    // orders by VolumeGroup A→B→C… then MerchNeedMonth DESC, and assigns
    // min(SKUMax, remaining) per store. If qty remains after all stores
    // hit cap, does round-robin one piece per store in same order until
    // qty hits zero.
    public async Task<List<AllocationRow>> ProcessAllocationAsync(
        string contno,
        IProgress<AllocationProgress>? progress = null,
        RunOption runOption = RunOption.FillSKUMax,
        CancellationToken ct = default)
    {
        var result = new List<AllocationRow>();
        if (string.IsNullOrWhiteSpace(contno)) return result;
        contno = contno.Trim();

        await using var c = OpenOnPremBackup();

        // 1. Load all PO line items for this container (one row per line — no GROUP BY).
        var lines = (await c.QueryAsync<(string ContNo, string OraPONo, string ItemCode, int Qty, string? LPM, DateTime? LPMDt)>(
            new CommandDefinition(@"
                SELECT ContNo, OraPONo, ItemCode,
                       CAST(ISNULL(orgqty,0) AS INT) AS Qty,
                       LPM, LPMDt
                FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK)
                WHERE ContNo = @c
                ORDER BY OraPONo, LPM, ItemCode",
                new { c = contno }, cancellationToken: ct))).AsList();

        if (lines.Count == 0) return result;

        // 2. Resolve DivCode + Division name per item via vupc_subclass
        var itemMeta = (await c.QueryAsync<(string itemcode, int? DivID, string? Division)>(new CommandDefinition(@"
            SELECT itemcode, DivID, Division
            FROM datareporting.dbo.vupc_subclass WITH (NOLOCK)
            WHERE itemcode IN @items",
            new { items = lines.Select(l => l.ItemCode).Distinct().ToArray() }, cancellationToken: ct)))
            .GroupBy(r => r.itemcode)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var divByItem = itemMeta.ToDictionary(kv => kv.Key, kv => kv.Value.DivID ?? 0, StringComparer.OrdinalIgnoreCase);

        // 2b. ItemName + Brand from usa.USAOrgFile (joined on ContNo + ItemCode)
        var orgByItem = (await c.QueryAsync<(string itemcode, string? itemname, string? vendor)>(new CommandDefinition(@"
            SELECT itemcode, MAX(itemname) AS itemname, MAX(vendor) AS vendor
            FROM usa.dbo.USAOrgFile WITH (NOLOCK)
            WHERE contno = @c AND itemcode IN @items
            GROUP BY itemcode",
            new { c = contno, items = lines.Select(l => l.ItemCode).Distinct().ToArray() }, cancellationToken: ct)))
            .ToDictionary(r => r.itemcode, r => (r.itemname, r.vendor), StringComparer.OrdinalIgnoreCase);

        // 2c. StoreName lookup from bfldata.DataSettings.PBFullname (key column: StoreID)
        var storeNameById = (await c.QueryAsync<(string StoreID, string? PBFullname)>(new CommandDefinition(@"
            SELECT StoreID, MAX(PBFullname) AS PBFullname
            FROM bfldata.dbo.DataSettings WITH (NOLOCK)
            WHERE PBFullname IS NOT NULL
            GROUP BY StoreID",
            cancellationToken: ct)))
            .ToDictionary(r => r.StoreID, r => r.PBFullname, StringComparer.OrdinalIgnoreCase);

        // 3. For each PO line, query eligible stores+rules in priority order
        //    (VolumeGroup ASC = A first, then MerchNeedMonth DESC).
        progress?.Report(new AllocationProgress(0, lines.Count, null));
        var idxLine = 0;
        foreach (var line in lines)
        {
            idxLine++;
            progress?.Report(new AllocationProgress(idxLine, lines.Count, line.ItemCode));
            if (line.Qty <= 0) continue;
            if (!divByItem.TryGetValue(line.ItemCode, out var divCode) || divCode == 0) continue;

            var rawStores = (await c.QueryAsync<(string StoreID, string Country, string VolumeGroup, int MerchNeedMonth, int SKUMax)>(
                new CommandDefinition(@"
                    SELECT s.StoreID, s.Country, s.VolumeGroup,
                           ISNULL(s.MerchNeedMonth, 0) AS MerchNeedMonth,
                           r.SKUMax
                    FROM dbo.LPM_EOM_Output s WITH (NOLOCK)
                    INNER JOIN dbo.LPM_SKUMaxRule r WITH (NOLOCK)
                       ON r.Country   = s.Country
                      AND r.DivCode   = s.DivCode
                      AND r.GroupCode = s.VolumeGroup
                      AND r.IsActive  = 1
                      AND @q BETWEEN r.WHStockFrom AND r.WHStockTo
                    WHERE s.DivCode = @d
                      AND s.Month1  = MONTH(SYSDATETIME())
                      AND s.Year1   = YEAR(SYSDATETIME())
                      AND s.VolumeGroup IS NOT NULL
                    ORDER BY s.VolumeGroup, s.MerchNeedMonth DESC",
                    new { q = line.Qty, d = divCode }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

            // CRITICAL: LPM_SKUMaxRule may have multiple active rules with overlapping
            // stock ranges for the same (Country, DivCode, GroupCode). That causes the
            // join to return DUPLICATE rows per store. If we iterate raw, Round Robin
            // gives the same store N pieces per pass (over-allocation) and Fill SKUMax
            // overwrites its own dictionary entry while still decrementing remaining
            // (under-allocation). Dedupe by StoreID, keeping the first row per the
            // ORDER BY priority (VolumeGroup A→B→C, MerchNeedMonth DESC).
            var stores = rawStores
                .GroupBy(s => s.StoreID, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (stores.Count == 0) continue;

            // Pass 1: walk in order. Fill SKUMax = give each store min(SKUMax, remaining)
            //         Round Robin = give 1 pc per pass, respecting SKUMax cap; loop until
            //         qty=0 or all stores at cap.
            var allocs = new Dictionary<string, AllocationRow>(StringComparer.OrdinalIgnoreCase);
            var remaining = line.Qty;

            AllocationRow MakeRow(string sid, string sn, string country, string vg, int merch, int cap, int take)
            {
                orgByItem.TryGetValue(line.ItemCode, out var meta);
                itemMeta.TryGetValue(line.ItemCode, out var iMeta);
                return new AllocationRow(
                    Contno: line.ContNo, OraPONo: line.OraPONo, ItemCode: line.ItemCode,
                    ItemName: meta.itemname, Brand: meta.vendor, PoQty: line.Qty,
                    StoreID: sid, StoreName: sn, Country: country, Division: iMeta.Division,
                    VolumeGroup: vg, SkuMax: cap, AllocQty: take, MerchNeedMonth: merch,
                    DivCode: divCode, RoundRobinExtra: 0, LPM: line.LPM, LPMDt: line.LPMDt);
            }

            if (runOption == RunOption.FillSKUMax)
            {
                foreach (var s in stores)
                {
                    if (remaining <= 0) break;
                    var take = Math.Min(s.SKUMax, remaining);
                    if (take <= 0) continue;
                    storeNameById.TryGetValue(s.StoreID, out var storeName);
                    allocs[s.StoreID] = MakeRow(s.StoreID, storeName ?? "", s.Country, s.VolumeGroup, s.MerchNeedMonth, s.SKUMax, take);
                    remaining -= take;
                }
            }
            else // RoundRobin — 1 pc per store per pass, respect SKUMax
            {
                while (remaining > 0)
                {
                    bool any = false;
                    foreach (var s in stores)
                    {
                        if (remaining <= 0) break;
                        var current = allocs.TryGetValue(s.StoreID, out var row) ? row.AllocQty : 0;
                        if (current >= s.SKUMax) continue;
                        storeNameById.TryGetValue(s.StoreID, out var storeName);
                        allocs[s.StoreID] = row is null
                            ? MakeRow(s.StoreID, storeName ?? "", s.Country, s.VolumeGroup, s.MerchNeedMonth, s.SKUMax, 1)
                            : row with { AllocQty = current + 1 };
                        remaining--;
                        any = true;
                    }
                    if (!any) break;  // every store at cap
                }
            }

            // Pass 2: round-robin (ignoring SKUMax) if Fill SKUMax mode has remaining qty.
            // In RoundRobin mode we respect the cap strictly — excess remains unallocated.
            if (runOption == RunOption.FillSKUMax && remaining > 0 && stores.Count > 0)
            {
                int idx = 0;
                while (remaining > 0)
                {
                    var s = stores[idx % stores.Count];
                    if (allocs.TryGetValue(s.StoreID, out var row))
                    {
                        allocs[s.StoreID] = row with
                        {
                            AllocQty = row.AllocQty + 1,
                            RoundRobinExtra = row.RoundRobinExtra + 1,
                        };
                    }
                    else
                    {
                        orgByItem.TryGetValue(line.ItemCode, out var meta2);
                        storeNameById.TryGetValue(s.StoreID, out var sn2);
                        itemMeta.TryGetValue(line.ItemCode, out var im2);
                        allocs[s.StoreID] = new AllocationRow(
                            Contno: line.ContNo, OraPONo: line.OraPONo, ItemCode: line.ItemCode,
                            ItemName: meta2.itemname, Brand: meta2.vendor, PoQty: line.Qty,
                            StoreID: s.StoreID, StoreName: sn2, Country: s.Country,
                            Division: im2.Division, VolumeGroup: s.VolumeGroup, SkuMax: s.SKUMax,
                            AllocQty: 1, MerchNeedMonth: s.MerchNeedMonth, DivCode: divCode,
                            RoundRobinExtra: 1, LPM: line.LPM, LPMDt: line.LPMDt);
                    }
                    remaining--;
                    idx++;
                }
            }

            result.AddRange(allocs.Values);
        }

        // Sanity check: total allocated must equal sum of PO line qtys (Fill SKUMax)
        // or be <= total in RoundRobin (excess unallocated when all stores at cap).
        var poTotal = lines.Sum(l => l.Qty);
        var allocTotal = result.Sum(r => r.AllocQty);
        if (runOption == RunOption.FillSKUMax && allocTotal != poTotal)
            Console.Error.WriteLine($"[ContainerAllocation] WARN: Fill SKUMax allocated {allocTotal} vs PO total {poTotal} (delta {allocTotal - poTotal}).");
        if (runOption == RunOption.RoundRobin && allocTotal > poTotal)
            Console.Error.WriteLine($"[ContainerAllocation] WARN: RoundRobin over-allocated {allocTotal} vs PO total {poTotal} (delta {allocTotal - poTotal}).");

        return result;
    }

    // ===================== Save Draft (LPMSIM tables) =====================
    // Detail rows go via SqlBulkCopy — 7000+ row drafts went from
    // "per-row INSERT one at a time, minutes of wall-time" to a couple
    // of seconds. Header still uses a normal INSERT (one row).
    public async Task SaveDraftAsync(string country, string contno, IReadOnlyList<AllocationRow> rows,
        string? warehouse = null, RunOption runOption = RunOption.FillSKUMax,
        IProgress<AllocationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var totalQty = rows.Sum(r => r.AllocQty);
        await using var c = OpenOnPremBackup();

        // 1) Wipe any prior draft for this (Country, ContNo) so re-Save replaces cleanly.
        progress?.Report(new AllocationProgress(0, rows.Count, "Saving draft: cleaning prior data"));
        await c.ExecuteAsync(new CommandDefinition(@"
            DELETE FROM LPMSIM.dbo.WMS_ContAllocationDraftDetail WHERE Country = @ct AND ContNo = @c;
            DELETE FROM LPMSIM.dbo.WMS_ContAllocationDraftHeader WHERE Country = @ct AND ContNo = @c;",
            new { ct = country, c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // 2) Header — single row.
        progress?.Report(new AllocationProgress(0, rows.Count, "Saving draft: writing header"));
        await c.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO LPMSIM.dbo.WMS_ContAllocationDraftHeader
                (Country, ContNo, Warehouse, RunOption, RowCount1, TotalQty, SavedTS, SavedBy)
            VALUES (@ct, @c, @wh, @ro, @rc, @tq, SYSDATETIME(), @u);",
            new { ct = country, c = contno, wh = warehouse, ro = runOption.ToString(),
                  rc = rows.Count, tq = totalQty, u = user.Name },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // 3) Detail — SqlBulkCopy. Build a DataTable that mirrors the 17 columns
        //    the previous per-row INSERT was populating; everything else stays NULL.
        progress?.Report(new AllocationProgress(0, rows.Count, "Saving draft: bulk insert"));
        var dt = new DataTable();
        dt.Columns.Add("Country",          typeof(string));
        dt.Columns.Add("ContNo",           typeof(string));
        dt.Columns.Add("TrnDate",          typeof(DateTime));
        dt.Columns.Add("Time1",            typeof(TimeSpan));
        dt.Columns.Add("UPC",              typeof(string));
        dt.Columns.Add("Itemcode",         typeof(string));
        dt.Columns.Add("GroupCode",        typeof(string));
        dt.Columns.Add("Qty",              typeof(int));
        dt.Columns.Add("PoQty",            typeof(int));
        dt.Columns.Add("QtyIssue",         typeof(int));
        dt.Columns.Add("StoreID",          typeof(string));
        dt.Columns.Add("TcmContno",        typeof(string));
        dt.Columns.Add("Itemname",         typeof(string));
        dt.Columns.Add("BuildingCategory", typeof(string));
        dt.Columns.Add("LPMDt",            typeof(DateTime));
        dt.Columns.Add("ORAPONo",          typeof(string));
        dt.Columns.Add("Division",         typeof(string));
        dt.Columns.Add("Remarks",          typeof(string));

        var now = DateTime.Now;
        var trnDate = now.Date;
        var time1 = new TimeSpan(now.Hour, now.Minute, now.Second);

        foreach (var r in rows)
        {
            dt.Rows.Add(
                country,
                r.Contno,
                trnDate,
                time1,
                r.ItemCode,
                r.ItemCode,
                r.VolumeGroup,
                r.AllocQty,
                r.PoQty,
                0,
                r.StoreID,
                r.Contno,
                (object?)r.ItemName ?? DBNull.Value,
                country,
                (object?)r.LPMDt ?? DBNull.Value,
                r.OraPONo,
                (object?)r.Division ?? DBNull.Value,
                (object?)(r.RoundRobinExtra > 0 ? $"RR+{r.RoundRobinExtra}" : null) ?? DBNull.Value);
        }

        // SqlBulkCopy.DestinationTableName needs the table to be in the connection's
        // current DB context; the existing OnPremBackup connection is on a different
        // database. Switch context to LPMSIM for the duration of the bulk copy.
        c.ChangeDatabase("LPMSIM");

        using var bulk = new SqlBulkCopy(c)
        {
            DestinationTableName = "dbo.WMS_ContAllocationDraftDetail",
            BatchSize            = 1000,
            BulkCopyTimeout      = CommandTimeoutSeconds,
            NotifyAfter          = 500,
        };
        bulk.SqlRowsCopied += (_, e) =>
            progress?.Report(new AllocationProgress((int)e.RowsCopied, rows.Count, "Saving draft to LPMSIM"));
        foreach (DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);

        progress?.Report(new AllocationProgress(rows.Count, rows.Count, "Saving draft: done"));
    }

    public async Task<List<AllocationRow>> LoadDraftAsync(string country, string contno, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        // Read draft detail; map back to AllocationRow shape. Several fields
        // (VolumeGroup, MerchNeedMonth, SkuMax, Brand, StoreName, DivCode) aren't
        // persisted on the detail row so they come back as defaults — preview
        // grid still works, sums/totals stay correct.
        var rows = (await c.QueryAsync<(string ContNo, string? OraPONo, string? ItemCode, string? ItemName,
                                       int? Qty, int? PoQty, string? StoreID, string? GroupCode, string? Division,
                                       string? Remarks, DateTime? LPMDt)>(new CommandDefinition(@"
            SELECT ContNo, ORAPONo, Itemcode, Itemname, Qty, PoQty, StoreID, GroupCode, Division, Remarks, LPMDt
            FROM LPMSIM.dbo.WMS_ContAllocationDraftDetail WITH (NOLOCK)
            WHERE Country = @ct AND ContNo = @c
            ORDER BY IdNo",
            new { ct = country, c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        return rows.Select(r => new AllocationRow(
            Contno: r.ContNo,
            OraPONo: r.OraPONo ?? "",
            ItemCode: r.ItemCode ?? "",
            ItemName: r.ItemName,
            Brand: null,
            PoQty: r.PoQty ?? 0,
            StoreID: r.StoreID ?? "",
            StoreName: null,
            Country: country,
            Division: r.Division,
            VolumeGroup: r.GroupCode ?? "",
            SkuMax: 0,
            AllocQty: r.Qty ?? 0,
            MerchNeedMonth: 0,
            DivCode: 0,
            RoundRobinExtra: ParseRoundRobin(r.Remarks),
            LPM: null,
            LPMDt: r.LPMDt
        )).ToList();
    }

    private static int ParseRoundRobin(string? remarks)
    {
        if (string.IsNullOrEmpty(remarks) || !remarks.StartsWith("RR+")) return 0;
        return int.TryParse(remarks.AsSpan(3), out var n) ? n : 0;
    }

    /// <summary>
    /// Reset Final: deletes all rows from LPMSIM.dbo.WMS_ContAllocationData for the
    /// given container so the page unlocks and Process can run again. Destructive —
    /// caller is responsible for confirming with the user. Returns rows deleted.
    /// </summary>
    public async Task<int> ResetFinalAsync(string contno, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WMS_ContAllocationData WHERE ContNo = @c",
            new { c = contno }, commandTimeout: 120, cancellationToken: ct));
    }

    public async Task<AllocationStatus> GetStatusAsync(string country, string contno, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var d = await c.QueryFirstOrDefaultAsync<(int? RowCount1, int? TotalQty, string? RunOption)>(new CommandDefinition(
            "SELECT RowCount1, TotalQty, RunOption FROM LPMSIM.dbo.WMS_ContAllocationDraftHeader WHERE Country = @ct AND ContNo = @c",
            new { ct = country, c = contno }, cancellationToken: ct));
        var hasDraft = d.RowCount1 is not null;
        var draftRows = d.RowCount1 ?? 0;

        var f = await c.QueryFirstOrDefaultAsync<(int Count1, DateTime? Max1)>(new CommandDefinition(
            "SELECT COUNT(*) AS Count1, MAX(TrnDate) AS Max1 FROM LPMSIM.dbo.WMS_ContAllocationData WHERE ContNo = @c",
            new { c = contno }, cancellationToken: ct));
        var hasFinal = f.Count1 > 0;
        return new AllocationStatus(hasDraft, hasFinal, draftRows, f.Count1, f.Max1, d.RunOption);
    }

    // ===================== Confirm & Save (Draft -> WMS_ContAllocationData) =====================
    // Atomic-ish: INSERT...SELECT from draft into final, then DELETE drafts.
    // If a draft exists, prefers that (single SQL copy). Falls back to inserting
    // the in-memory rows if no draft exists yet.
    public async Task<int> SaveAllocationAsync(IReadOnlyList<AllocationRow> rows,
        IProgress<AllocationProgress>? progress = null, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;
        var country = rows[0].Country;
        var contno  = rows[0].Contno;

        progress?.Report(new AllocationProgress(0, rows.Count, "Confirming: checking draft"));
        await using var c = OpenOnPremBackup();

        // Is there a saved draft for this (Country, ContNo)? If yes, copy and delete.
        var draftRows = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT COUNT(*) FROM LPMSIM.dbo.WMS_ContAllocationDraftDetail WHERE Country = @ct AND ContNo = @c",
            new { ct = country, c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)) ?? 0;

        if (draftRows > 0)
        {
            progress?.Report(new AllocationProgress(0, draftRows, $"Confirming: copying {draftRows} rows draft → final"));
            var copySql = @"
                INSERT INTO LPMSIM.dbo.WMS_ContAllocationData
                  (ContNo, TrnDate, Time1, UPC, Itemcode, GroupCode, Season, Department, Division,
                   Result, FinalResult, ResultType, Qty, QtyIssue, OrPrice, PrintFlag, RfidFlag,
                   Company, StoreID, Itemname, Barcode, SalesPrice, RefNo, Mark, Uid,
                   RStatus, RDateTime, PStatus, PDateTime, Excess, TcmContno, BuildingCategory,
                   LPMDt, LPMBoxNO, ORAPONo, Style, Remarks)
                SELECT
                   ContNo, TrnDate, Time1, UPC, Itemcode, GroupCode, Season, Department, Division,
                   Result, FinalResult, ResultType, Qty, QtyIssue, OrPrice, PrintFlag, RfidFlag,
                   Company, StoreID, Itemname, Barcode, SalesPrice, RefNo, Mark, Uid,
                   RStatus, RDateTime, PStatus, PDateTime, Excess, TcmContno, BuildingCategory,
                   LPMDt, LPMBoxNO, ORAPONo, Style, Remarks
                FROM LPMSIM.dbo.WMS_ContAllocationDraftDetail
                WHERE Country = @ct AND ContNo = @c;

                DELETE FROM LPMSIM.dbo.WMS_ContAllocationDraftDetail WHERE Country = @ct AND ContNo = @c;
                DELETE FROM LPMSIM.dbo.WMS_ContAllocationDraftHeader WHERE Country = @ct AND ContNo = @c;";
            await c.ExecuteAsync(new CommandDefinition(copySql, new { ct = country, c = contno },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            progress?.Report(new AllocationProgress(draftRows, draftRows, "Confirming: done"));
            return draftRows;
        }

        // Fallback path — no draft, insert in-memory rows directly.
        var insertSql = @"INSERT INTO LPMSIM.dbo.WMS_ContAllocationData
            (ContNo, TrnDate, Time1, UPC, Itemcode, GroupCode, Division, Qty, QtyIssue,
             StoreID, TcmContno, ORAPONo, LPMDt, Itemname, BuildingCategory, Remarks)
          VALUES
            (@ContNo, CAST(SYSDATETIME() AS DATE), CAST(SYSDATETIME() AS TIME(0)),
             @UPC, @ItemCode, @GroupCode, @Division, @Qty, 0,
             @StoreID, @ContNo, @OraPONo, @LPMDt, @ItemName, @Country, @Remarks);";
        var affected = 0;
        foreach (var r in rows)
        {
            affected += await c.ExecuteAsync(new CommandDefinition(insertSql, new
            {
                ContNo    = r.Contno,  UPC = r.ItemCode, ItemCode = r.ItemCode,
                GroupCode = r.VolumeGroup, Division = r.Division, Qty = r.AllocQty,
                StoreID   = r.StoreID, OraPONo = r.OraPONo, LPMDt = r.LPMDt,
                ItemName  = r.ItemName, Country = r.Country,
                Remarks   = r.RoundRobinExtra > 0 ? $"RR+{r.RoundRobinExtra}" : null,
            }, cancellationToken: ct));
        }
        return affected;
    }

    // ===================== Country / WH dropdowns (Azure WMS WHMaster) =====================
    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT Country FROM dbo.WmsWHMaster WHERE Active = 1 ORDER BY Country",
            cancellationToken: ct));
        return list.AsList();
    }

    public async Task<List<string>> GetWarehousesAsync(string country, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(country)) return new();
        await using var c = OpenWms();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT Warehouse FROM dbo.WmsWHMaster
              WHERE Active = 1 AND Country = @c ORDER BY Warehouse",
            new { c = country }, cancellationToken: ct));
        return list.AsList();
    }
}
