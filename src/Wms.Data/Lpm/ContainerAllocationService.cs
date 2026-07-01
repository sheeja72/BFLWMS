using System.Data;
using System.Text.RegularExpressions;
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
        const int TOTAL = 7;

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

        // 4+5+6+7: Azure WMS build/sync status — one round-trip, 4 sub-counts.
        progress?.Report(new AllocationProgress(4, TOTAL, "Validating: build status"));
        await using (var w = OpenWms())
        {
            var status = await w.QueryFirstAsync<(int Building, int Completed, int AllocSynced, int Scanned)>(new CommandDefinition(@"
                SELECT
                    (SELECT COUNT(*) FROM dbo.WmsOpenBox            WITH (NOLOCK) WHERE Contno = @c)                                 AS Building,
                    (SELECT COUNT(*) FROM dbo.WmsBuildingCompletion WITH (NOLOCK) WHERE Country = @ct AND ContNo = @c)                AS Completed,
                    (SELECT COUNT(*) FROM dbo.WMS_ContAllocationData WITH (NOLOCK) WHERE ContNo = @c)                                 AS AllocSynced,
                    (SELECT COUNT(*) FROM dbo.WMSContBuildScanData  WITH (NOLOCK) WHERE ContNo = @c)                                 AS Scanned",
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

            progress?.Report(new AllocationProgress(6, TOTAL, "Validating: allocation not yet synced to Azure"));
            var allocSynced = status.AllocSynced > 0;
            steps.Add(new ValidationStep(
                "Container not yet synced to Azure allocation (no WMS_ContAllocationData row)",
                !allocSynced,
                allocSynced
                    ? $"Container {contno} already has {status.AllocSynced} row(s) in dbo.WMS_ContAllocationData — its allocation was already approved and synced. Delete those first if you really want to re-allocate."
                    : null));
            if (allocSynced) return new ContainerAllocationValidationResult(false, steps);

            progress?.Report(new AllocationProgress(7, TOTAL, "Validating: no prior building scans"));
            var scanned = status.Scanned > 0;
            steps.Add(new ValidationStep(
                "No prior building scans (no WMSContBuildScanData row)",
                !scanned,
                scanned
                    ? $"Container {contno} already has {status.Scanned} scan row(s) in dbo.WMSContBuildScanData — building has started. Cannot re-run allocation."
                    : null));
            if (scanned) return new ContainerAllocationValidationResult(false, steps);
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
    public async Task<AllocationProcessResult> ProcessAllocationAsync(
        string contno,
        IProgress<AllocationProgress>? progress = null,
        RunOption runOption = RunOption.FillSKUMax,
        IReadOnlyCollection<string>? allocationCountries = null,
        CancellationToken ct = default)
    {
        var result  = new List<AllocationRow>();
        var blocked = new List<BlockedItemRow>();
        if (string.IsNullOrWhiteSpace(contno)) return new(result, blocked);
        contno = contno.Trim();

        await using var c = OpenOnPremBackup();

        // Per-prefetch progress messages so the bar shows what the algorithm is
        // doing during the ~9 prep queries before the per-line loop. Without them
        // the user sees a static "Processing…" for several seconds after Validate.
        progress?.Report(new AllocationProgress(0, 0, "Prefetching: PO line items"));
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

        if (lines.Count == 0) return new(result, blocked);

        progress?.Report(new AllocationProgress(0, 0, "Prefetching: item Division / Department"));
        // 2. Resolve DivCode + Division name + Department per item via vupc_subclass
        var itemMeta = (await c.QueryAsync<(string itemcode, int? DivID, string? Division, string? Department)>(new CommandDefinition(@"
            SELECT itemcode, DivID, Division, Department
            FROM datareporting.dbo.vupc_subclass WITH (NOLOCK)
            WHERE itemcode IN @items",
            new { items = lines.Select(l => l.ItemCode).Distinct().ToArray() }, cancellationToken: ct)))
            .GroupBy(r => r.itemcode)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var divByItem = itemMeta.ToDictionary(kv => kv.Key, kv => kv.Value.DivID ?? 0, StringComparer.OrdinalIgnoreCase);

        progress?.Report(new AllocationProgress(0, 0, "Prefetching: store eligibility blocks"));
        // 2.5. Pre-fetch block tables (LPM_StoreDeptAccess + LPM_StoreDivAccess where IsActive=0).
        // No Country filter per requirement — keys are (StoreID, DivCode[, Department]).
        var deptBlocks = (await c.QueryAsync<(string StoreID, int DivCode, string? Department)>(new CommandDefinition(@"
            SELECT StoreID, DivCode, Department
            FROM dbo.LPM_StoreDeptAccess WITH (NOLOCK)
            WHERE IsActive = 0",
            cancellationToken: ct)))
            .Select(r => (Sid: (r.StoreID ?? "").Trim().ToUpperInvariant(), r.DivCode, Dep: (r.Department ?? "").Trim().ToUpperInvariant()))
            .ToHashSet();
        var divBlocks = (await c.QueryAsync<(string StoreID, int DivCode)>(new CommandDefinition(@"
            SELECT StoreID, DivCode
            FROM dbo.LPM_StoreDivAccess WITH (NOLOCK)
            WHERE IsActive = 0",
            cancellationToken: ct)))
            .Select(r => (Sid: (r.StoreID ?? "").Trim().ToUpperInvariant(), r.DivCode))
            .ToHashSet();

        progress?.Report(new AllocationProgress(0, 0, "Prefetching: item Brand / Season / Style / Size"));
        // 2b. ItemName + Brand + Season + Style + Size from usa.USAOrgFile.
        // Season drives the PalletType pick at save time (S vs W variant);
        // Style + Size are written to the detail rows for downstream consumers.
        var orgByItem = (await c.QueryAsync<(string itemcode, string? itemname, string? vendor, string? season, string? Style, string? Size)>(new CommandDefinition(@"
            SELECT itemcode,
                   MAX(itemname) AS itemname,
                   MAX(vendor)   AS vendor,
                   MAX(season)   AS season,
                   MAX(Style)    AS Style,
                   MAX([Size])   AS [Size]
            FROM usa.dbo.USAOrgFile WITH (NOLOCK)
            WHERE contno = @c AND itemcode IN @items
            GROUP BY itemcode",
            new { c = contno, items = lines.Select(l => l.ItemCode).Distinct().ToArray() }, cancellationToken: ct)))
            .ToDictionary(r => r.itemcode, r => (r.itemname, r.vendor, r.season, r.Style, r.Size), StringComparer.OrdinalIgnoreCase);

        progress?.Report(new AllocationProgress(0, 0, "Prefetching: store names"));
        // 2c. StoreName lookup from bfldata.DataSettings.PBFullname (key column: StoreID)
        var storeNameById = (await c.QueryAsync<(string StoreID, string? PBFullname)>(new CommandDefinition(@"
            SELECT StoreID, MAX(PBFullname) AS PBFullname
            FROM bfldata.dbo.DataSettings WITH (NOLOCK)
            WHERE PBFullname IS NOT NULL
            GROUP BY StoreID",
            cancellationToken: ct)))
            .ToDictionary(r => r.StoreID, r => r.PBFullname, StringComparer.OrdinalIgnoreCase);

        progress?.Report(new AllocationProgress(0, 0, "Prefetching: pallet types"));
        // 2c2. WMS_Building_PalletTypes — keyed by StoreId. Per-row ResultType picks
        // PalletTypeS when item season <> 'W' else PalletTypeW (per user spec Q-C).
        var palletByStore = (await c.QueryAsync<(string StoreId, string? PalletTypeS, string? PalletTypeW)>(new CommandDefinition(@"
            SELECT StoreId, MAX(PalletTypeS) AS PalletTypeS, MAX(PalletTypeW) AS PalletTypeW
            FROM dbo.WMS_Building_PalletTypes WITH (NOLOCK)
            WHERE StoreId IS NOT NULL
            GROUP BY StoreId",
            cancellationToken: ct)))
            .ToDictionary(r => r.StoreId, r => (r.PalletTypeS, r.PalletTypeW), StringComparer.OrdinalIgnoreCase);

        // Determine the country filter once — used for both eomStores narrowing and
        // the SalesPrice lookup. Empty/null = all countries (back-compat).
        var hasCountryFilter = allocationCountries is { Count: > 0 };
        var countryFilter = hasCountryFilter ? allocationCountries!.ToArray() : Array.Empty<string>();

        // 2c3. SalesPrice per (country, itemcode). UAE stores → hodata.salesprice.salesrate,
        // other countries → <DataName>.dbo.RFSalesprice.salesrate where DataName comes
        // from bfldata.DataSettings.DataName for that SIMCountry. DataName is sanitized
        // (alphanumeric/underscore only) before being injected into the dynamic FROM clause.
        var pricesByCountryItem = new Dictionary<(string Country, string ItemCode), decimal?>();
        var distinctItemCodes = lines.Select(l => l.ItemCode).Distinct().ToArray();
        if (hasCountryFilter && distinctItemCodes.Length > 0)
        {
            foreach (var sc in countryFilter.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                progress?.Report(new AllocationProgress(0, 0, $"Prefetching: sales prices ({sc})"));
                try
                {
                    if (string.Equals(sc, "UAE", StringComparison.OrdinalIgnoreCase))
                    {
                        var rows = await c.QueryAsync<(string itemcode, decimal? salesrate)>(new CommandDefinition(@"
                            SELECT itemcode, salesrate
                            FROM hodata.dbo.salesprice WITH (NOLOCK)
                            WHERE itemcode IN @items",
                            new { items = distinctItemCodes }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                        foreach (var r in rows) pricesByCountryItem[(sc, r.itemcode)] = r.salesrate;
                    }
                    else
                    {
                        var dataName = await c.ExecuteScalarAsync<string?>(new CommandDefinition(@"
                            SELECT TOP 1 DataName FROM bfldata.dbo.DataSettings WITH (NOLOCK)
                            WHERE SIMCountry = @c
                              AND DataName IS NOT NULL AND LTRIM(RTRIM(DataName)) <> ''",
                            new { c = sc }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                        if (string.IsNullOrWhiteSpace(dataName)) continue;
                        if (!Regex.IsMatch(dataName, @"^[A-Za-z0-9_]+$")) continue;  // defend against ETL drift
                        var sql = $@"SELECT itemcode, salesrate
                                       FROM {dataName}.dbo.RFSalesprice WITH (NOLOCK)
                                      WHERE itemcode IN @items";
                        var rows = await c.QueryAsync<(string itemcode, decimal? salesrate)>(new CommandDefinition(
                            sql, new { items = distinctItemCodes }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                        foreach (var r in rows) pricesByCountryItem[(sc, r.itemcode)] = r.salesrate;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ContainerAllocation] WARN: SalesPrice lookup failed for country '{sc}': {ex.Message}");
                }
            }
        }

        progress?.Report(new AllocationProgress(0, 0, "Prefetching: prior approved allocations"));
        // 2c4. PrevAllocatedQty seed — sum of allocated qty per (StoreID, DivCode) from
        // approved Headers' detail rows whose (ContNo, Country) pair has NOT yet been
        // marked complete in the Azure WMS DB's WmsBuildingCompletion table.
        // Until Approve UI ships (P4), this returns empty.
        var prevAllocatedSeed = new Dictionary<(string StoreID, int DivCode), int>();
        {
            var approvedRows = (await c.QueryAsync<(string ContNo, string Country, string StoreID, string Itemcode, int? Qty)>(new CommandDefinition(@"
                SELECT d.TcmContno AS ContNo, d.Country, d.StoreID, d.Itemcode, d.Qty
                  FROM LPMSIM.dbo.WMS_Cont_Allocation_Header h WITH (NOLOCK)
                  JOIN LPMSIM.dbo.WMS_ContAllocationData d   WITH (NOLOCK) ON d.BatchNo = h.BatchNo
                 WHERE h.ApprovedDt IS NOT NULL",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
            if (approvedRows.Count > 0)
            {
                var distinctContnos = approvedRows.Select(r => r.ContNo).Distinct().ToArray();
                HashSet<(string, string)> completed;
                await using (var w = OpenWms())
                {
                    var compRows = await w.QueryAsync<(string ContNo, string Country)>(new CommandDefinition(@"
                        SELECT DISTINCT ContNo, Country FROM dbo.WmsBuildingCompletion WITH (NOLOCK)
                        WHERE ContNo IN @contnos",
                        new { contnos = distinctContnos }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    completed = compRows.Select(r => (r.ContNo, r.Country)).ToHashSet();
                }

                // Resolve DivCode for any approved-batch item not already in divByItem.
                var extraItems = approvedRows.Select(r => r.Itemcode)
                    .Where(i => !divByItem.ContainsKey(i)).Distinct().ToArray();
                var divByApprovedItem = new Dictionary<string, int>(divByItem, StringComparer.OrdinalIgnoreCase);
                if (extraItems.Length > 0)
                {
                    var extraDivs = await c.QueryAsync<(string itemcode, int? DivID)>(new CommandDefinition(@"
                        SELECT itemcode, MAX(DivID) AS DivID
                        FROM datareporting.dbo.vupc_subclass WITH (NOLOCK)
                        WHERE itemcode IN @items GROUP BY itemcode",
                        new { items = extraItems }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    foreach (var r in extraDivs) divByApprovedItem[r.itemcode] = r.DivID ?? 0;
                }

                foreach (var r in approvedRows)
                {
                    if (completed.Contains((r.ContNo, r.Country))) continue;
                    if (!divByApprovedItem.TryGetValue(r.Itemcode, out var div) || div == 0) continue;
                    var key = (r.StoreID, div);
                    prevAllocatedSeed[key] = prevAllocatedSeed.GetValueOrDefault(key, 0) + (r.Qty ?? 0);
                }
            }
        }

        // 2d. Prefetch eligible stores + SKUMax bands + latest TodayOTS for ALL DivCodes
        // in the container — in 3 round-trips total. The per-line loop below then does
        // an in-memory join instead of one SQL query per PO line (N+1 was the timeout cause).
        var distinctDivs = divByItem.Values.Where(d => d > 0).Distinct().ToArray();
        if (distinctDivs.Length == 0) return new(result, blocked);

        progress?.Report(new AllocationProgress(0, 0, "Prefetching: eligible stores"));
        // eomStores narrowed by the country filter computed above.
        var eomStores = (await c.QueryAsync<(string StoreID, string Country, int DivCode, string VolumeGroup, int MerchNeedMonth)>(
            new CommandDefinition(@"
                SELECT s.StoreID, s.Country, s.DivCode, s.VolumeGroup,
                       ISNULL(s.MerchNeedMonth, 0) AS MerchNeedMonth
                FROM dbo.LPM_EOM_Output s WITH (NOLOCK)
                WHERE s.DivCode IN @divs
                  AND s.Month1  = MONTH(DATEADD(hour, 4, SYSUTCDATETIME()))
                  AND s.Year1   = YEAR(DATEADD(hour, 4, SYSUTCDATETIME()))
                  AND s.VolumeGroup IS NOT NULL
                  AND (@noCountryFilter = 1 OR s.Country IN @countries)",
                new { divs = distinctDivs,
                      noCountryFilter = hasCountryFilter ? 0 : 1,
                      countries = countryFilter },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        var storesByDiv = eomStores
            .GroupBy(s => s.DivCode)
            .ToDictionary(g => g.Key, g => g.ToList());

        progress?.Report(new AllocationProgress(0, 0, "Prefetching: SKU Max bands"));
        var rulesRaw = (await c.QueryAsync<(string Country, int DivCode, string GroupCode, int WHStockFrom, int WHStockTo, int SKUMax)>(
            new CommandDefinition(@"
                SELECT Country, DivCode, GroupCode, WHStockFrom, WHStockTo, SKUMax
                FROM dbo.LPM_SKUMaxRule WITH (NOLOCK)
                WHERE DivCode IN @divs AND IsActive = 1",
                new { divs = distinctDivs }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        var rulesByKey = rulesRaw
            .GroupBy(r => (r.Country, r.DivCode, r.GroupCode))
            .ToDictionary(g => g.Key, g => g.ToList());

        progress?.Report(new AllocationProgress(0, 0, "Prefetching: OTS values"));
        // Latest OTS raw components per (StoreID, DivCode). We keep targetEOM / SOH /
        // SalesTgtWk / Σ trfQty separately so the per-item loop can recompute OTS as
        // AllocatedQty grows during this batch. The OTS formula stays in C# now:
        //
        //   OTS = ((targetEOM − SOH + SalesTgtWk − AllocatedQty − PrevAllocatedQty) − Σ trfQty)
        //         / targetEOM
        var otsRaw = (await c.QueryAsync<(string StoreID, int DivCode, int targetEOM, int SOH, int SalesTgtWk, int trfSum)>(
            new CommandDefinition(@"
                WITH ranked AS (
                    SELECT o.StoreID, o.DivCode,
                           ISNULL(o.targetEOM,0)   AS targetEOM,
                           ISNULL(o.SOH,0)         AS SOH,
                           ISNULL(o.SalesTgtWk,0)  AS SalesTgtWk,
                           ISNULL(o.trfQty1,0) + ISNULL(o.trfqty2,0) + ISNULL(o.trfqty3,0)
                             + ISNULL(o.trfqty4,0) + ISNULL(o.trfqty5,0) + ISNULL(o.trfqty6,0)
                             + ISNULL(o.trfqty7,0)                            AS trfSum,
                           ROW_NUMBER() OVER (PARTITION BY o.StoreID, o.DivCode
                                              ORDER BY o.OTSDate DESC) AS rn
                    FROM dbo.LPM_OTS_Output o WITH (NOLOCK)
                    WHERE o.DivCode IN @divs
                      AND o.OTSDate < CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE)
                )
                SELECT StoreID, DivCode, targetEOM, SOH, SalesTgtWk, trfSum
                  FROM ranked WHERE rn = 1",
                new { divs = distinctDivs }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        var otsRawByKey = otsRaw.ToDictionary(o => (o.StoreID, o.DivCode),
            o => (o.targetEOM, o.SOH, o.SalesTgtWk, o.trfSum));

        // Running allocation total per (StoreID, DivCode) this batch — drives the
        // OTS refresh between items. Starts at zero; grows as we allocate.
        var runningAlloc = new Dictionary<(string StoreID, int DivCode), int>();

        // ============ FillSKUMaxRoundRobin: extra prefetches ============
        // Only paid when the operator actually picked this run option. Nothing
        // for FillSKUMax / RoundRobin.
        Dictionary<(string StoreID, string ItemCode), int> initialAllocByKey = new();  // keys normalised to UPPER on both insert + lookup
        Dictionary<(string StoreID, int DivCode), int?> priorityByStoreDiv = new();
        DateTime? containerReceiptDt = null;
        int fillRRTopN = 25;
        if (runOption == RunOption.FillSKUMaxRoundRobin)
        {
            progress?.Report(new AllocationProgress(0, 0, "Prefetching: Initial Allocation + EOM priority + receipt date"));

            // Initial allocation per (StoreID, Itemcode) from Azure WMS.
            await using (var wms = new SqlConnection(resolver.GetWmsAzureConnectionString()))
            {
                await wms.OpenAsync(ct);
                var rows = await wms.QueryAsync<(string StoreID, string Itemcode, int AllocationQty)>(new CommandDefinition(
                    @"SELECT StoreID, Itemcode, AllocationQty
                        FROM dbo.WmsManualAllocation WITH (NOLOCK)
                       WHERE ContNo = @c",
                    new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                foreach (var r in rows)
                    initialAllocByKey[(r.StoreID.ToUpperInvariant(), r.Itemcode.ToUpperInvariant())] = r.AllocationQty;

                var cfg = await wms.ExecuteScalarAsync<string?>(new CommandDefinition(
                    @"SELECT TOP 1 ConfigValue FROM dbo.WmsAppConfig WITH (NOLOCK)
                       WHERE ConfigKey = 'ContainerAlloc.FillSKUMaxRoundRobin.TopN'",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                if (int.TryParse(cfg, out var t) && t > 0) fillRRTopN = t;
            }

            // Division Priority per (StoreID, DivCode) — lower priorityranking = higher priority.
            var nowGst = DateTime.UtcNow.AddHours(4);
            var pRows = await c.QueryAsync<(string StoreID, int DivCode, int? priorityranking)>(new CommandDefinition(
                @"SELECT StoreId AS StoreID, DivCode, priorityranking
                    FROM LPMSIM.dbo.LPM_EOM_Output WITH (NOLOCK)
                   WHERE Month1 = @m AND Year1 = @y",
                new { m = nowGst.Month, y = nowGst.Year },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var p in pRows)
                priorityByStoreDiv[(p.StoreID, p.DivCode)] = p.priorityranking;

            // Container receipt date from bfldata.dbo.contreceipt (receiptdt column).
            containerReceiptDt = await c.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                @"SELECT TOP 1 receiptdt FROM bfldata.dbo.contreceipt WITH (NOLOCK)
                   WHERE refno = @c
                   ORDER BY receiptdt DESC",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }

        // Effective SKU Max = MIN(baseSkuMax, InitialAlloc for (store, item))
        //                    when InitialAlloc exists; else baseSkuMax.
        int EffectiveSkuMax(int baseCap, string sid, string itemCode) =>
            initialAllocByKey.TryGetValue((sid.ToUpperInvariant(), itemCode.ToUpperInvariant()), out var ini)
                ? Math.Min(baseCap, ini) : baseCap;

        // Whether a given line-item's LPMDt is within the "current + N months"
        // window (Condition A) or after it (Condition B).
        bool IsWithinLpmWindow(DateTime? lpmDt)
        {
            if (containerReceiptDt is null || lpmDt is null) return true; // fall through to Condition A
            var n = containerReceiptDt.Value.Day < 15 ? 1 : 2;
            var now = DateTime.UtcNow.AddHours(4);
            var upper = new DateTime(now.Year, now.Month, 1).AddMonths(n + 1).AddDays(-1);
            return lpmDt.Value <= upper;
        }

        double? ComputeOts(string sid, int div)
        {
            if (!otsRawByKey.TryGetValue((sid, div), out var raw) || raw.targetEOM == 0) return null;
            var alloc = runningAlloc.GetValueOrDefault((sid, div), 0);
            var prev  = prevAllocatedSeed.GetValueOrDefault((sid, div), 0);
            var numerator = (raw.targetEOM - raw.SOH + raw.SalesTgtWk - alloc - prev) - raw.trfSum;
            return numerator * 1.0 / raw.targetEOM;
        }

        // 3. P3 allocation loop — group lines by (OraPONo, Division, LPMDt) combo, sort
        // items within each combo by Qty DESC, and walk stores by current OTS DESC.
        // After each item finishes, runningAlloc is mutated so the next item's store
        // ordering uses a refreshed OTS reflecting what's already been given out.
        progress?.Report(new AllocationProgress(0, lines.Count, null));
        var idxLine = 0;
        var combos = lines
            .Where(l => l.Qty > 0)
            .Select(l => new
            {
                Line = l,
                Division = itemMeta.TryGetValue(l.ItemCode, out var im) ? im.Division : null
            })
            .GroupBy(x => (x.Line.OraPONo, Division: x.Division ?? "", x.Line.LPMDt))
            .OrderBy(g => g.Key.OraPONo).ThenBy(g => g.Key.Division).ThenBy(g => g.Key.LPMDt);

        foreach (var combo in combos)
        {
            var comboLines = combo.OrderByDescending(x => x.Line.Qty).Select(x => x.Line).ToList();
            foreach (var line in comboLines)
            {
                idxLine++;
                progress?.Report(new AllocationProgress(idxLine, lines.Count, line.ItemCode));
                if (line.Qty <= 0) continue;
                if (!divByItem.TryGetValue(line.ItemCode, out var divCode) || divCode == 0) continue;
                if (!storesByDiv.TryGetValue(divCode, out var divStores)) continue;

                itemMeta.TryGetValue(line.ItemCode, out var itemRow);
                orgByItem.TryGetValue(line.ItemCode, out var orgRow);
                var dept = (itemRow.Department ?? "").Trim();

                // Resolve (StoreID -> band-matching SKUMax + current OTS) for this item.
                var perStore = new Dictionary<string, (string Country, string VolumeGroup, int MerchNeedMonth, int SKUMax, double? Ots)>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in divStores)
                {
                    if (perStore.ContainsKey(s.StoreID)) continue;
                    if (!rulesByKey.TryGetValue((s.Country, divCode, s.VolumeGroup), out var bands)) continue;
                    int? skuMax = null;
                    foreach (var b in bands)
                    {
                        if (line.Qty >= b.WHStockFrom && line.Qty <= b.WHStockTo) { skuMax = b.SKUMax; break; }
                    }
                    if (skuMax is null) continue;
                    perStore[s.StoreID] = (s.Country, s.VolumeGroup, s.MerchNeedMonth, skuMax.Value, ComputeOts(s.StoreID, divCode));
                }

                // Apply block filter (DeptAccess / DivAccess). Surviving stores then sorted
                // by current OTS DESC (nulls last).
                var stores = new List<(string StoreID, string Country, string VolumeGroup, int MerchNeedMonth, int SKUMax, double? Ots)>(perStore.Count);
                foreach (var (storeId, info) in perStore)
                {
                    var sidU  = storeId.Trim().ToUpperInvariant();
                    var deptU = dept.ToUpperInvariant();
                    var deptHit = !string.IsNullOrEmpty(dept) && deptBlocks.Contains((sidU, divCode, deptU));
                    var divHit  = divBlocks.Contains((sidU, divCode));
                    if (deptHit || divHit)
                    {
                        var reason = deptHit && divHit ? "DeptAccess+DivAccess" : (deptHit ? "DeptAccess" : "DivAccess");
                        storeNameById.TryGetValue(storeId, out var sName);
                        blocked.Add(new BlockedItemRow(
                            Contno: line.ContNo, ItemCode: line.ItemCode, ItemName: orgRow.itemname,
                            Division: itemRow.Division, Department: itemRow.Department,
                            StoreID: storeId, StoreName: sName, Country: info.Country,
                            PoQty: line.Qty, DivCode: divCode, BlockReason: reason));
                        continue;
                    }
                    stores.Add((storeId, info.Country, info.VolumeGroup, info.MerchNeedMonth, info.SKUMax, info.Ots));
                }
                if (stores.Count == 0) continue;

                stores = stores
                    .OrderBy(s => s.Ots.HasValue ? 0 : 1)
                    .ThenByDescending(s => s.Ots)
                    .ToList();

                // Build an enrichment-aware row factory. PalletType is season-driven
                // (W → PalletTypeW, else PalletTypeS); SalesPrice is per store country.
                var season = (orgRow.season ?? "").Trim();
                var isWinter = season.Equals("W", StringComparison.OrdinalIgnoreCase);
                string? PalletFor(string sid)
                {
                    if (!palletByStore.TryGetValue(sid, out var pt)) return null;
                    return isWinter ? pt.PalletTypeW : pt.PalletTypeS;
                }

                AllocationRow MakeRow(string sid, string country, string vg, int merch, int cap, int take, int rrExtra)
                {
                    storeNameById.TryGetValue(sid, out var storeName);
                    pricesByCountryItem.TryGetValue((country, line.ItemCode), out var price);
                    return new AllocationRow(
                        Contno: line.ContNo, OraPONo: line.OraPONo, ItemCode: line.ItemCode,
                        ItemName: orgRow.itemname, Brand: orgRow.vendor, PoQty: line.Qty,
                        StoreID: sid, StoreName: storeName, Country: country, Division: itemRow.Division,
                        VolumeGroup: vg, SkuMax: cap, AllocQty: take, MerchNeedMonth: merch,
                        DivCode: divCode, RoundRobinExtra: rrExtra, LPM: line.LPM, LPMDt: line.LPMDt,
                        OTS: ComputeOts(sid, divCode),
                        Season: orgRow.season, Style: orgRow.Style, Size: orgRow.Size,
                        Department: itemRow.Department, SalesPrice: price,
                        PalletType: PalletFor(sid),
                        PrevAllocatedQty: prevAllocatedSeed.GetValueOrDefault((sid, divCode), 0));
                }

                var allocs = new Dictionary<string, AllocationRow>(StringComparer.OrdinalIgnoreCase);
                var remaining = line.Qty;

                if (runOption == RunOption.FillSKUMax)
                {
                    foreach (var s in stores)
                    {
                        if (remaining <= 0) break;
                        var take = Math.Min(s.SKUMax, remaining);
                        if (take <= 0) continue;
                        allocs[s.StoreID] = MakeRow(s.StoreID, s.Country, s.VolumeGroup, s.MerchNeedMonth, s.SKUMax, take, 0);
                        remaining -= take;
                    }
                }
                else if (runOption == RunOption.FillSKUMaxRoundRobin)
                {
                    // ================= FillSKUMax + RoundRobin =================
                    // Two conditions per line item, driven by whether the item's
                    // LPMDt falls inside the container's LPM window (Scenario 1
                    // = receipt<15 -> N=1, Scenario 2 = receipt>=15 -> N=2).
                    var conditionA = IsWithinLpmWindow(line.LPMDt);

                    if (conditionA)
                    {
                        // 1. Priority rank by LPM_EOM_Output.priorityranking for
                        //    (StoreID, DivCode). Lower rank number = higher priority.
                        //    Stores with no ranking sink to the bottom.
                        var ranked = stores
                            .Select(s => new {
                                Store = s,
                                Priority = priorityByStoreDiv.TryGetValue((s.StoreID, divCode), out var pr) ? pr : null,
                            })
                            .OrderBy(x => x.Priority.HasValue ? 0 : 1)
                            .ThenBy(x => x.Priority ?? int.MaxValue)
                            .ToList();

                        var top = ranked.Take(fillRRTopN).Select(x => x.Store).ToList();
                        var rest = ranked.Skip(fillRRTopN).Select(x => x.Store).ToList();

                        // 2. Fill Top-N by OTS DESC up to Effective SKU Max.
                        var topByOts = top
                            .OrderBy(s => s.Ots.HasValue ? 0 : 1)
                            .ThenByDescending(s => s.Ots)
                            .ToList();
                        foreach (var s in topByOts)
                        {
                            if (remaining <= 0) break;
                            var cap  = EffectiveSkuMax(s.SKUMax, s.StoreID, line.ItemCode);
                            var take = Math.Min(cap, remaining);
                            if (take <= 0) continue;
                            allocs[s.StoreID] = MakeRow(s.StoreID, s.Country, s.VolumeGroup, s.MerchNeedMonth, cap, take, 0);
                            remaining -= take;
                        }

                        // 3. Round-Robin remaining stores by OTS DESC up to Effective SKU Max.
                        var restByOts = rest
                            .OrderBy(s => s.Ots.HasValue ? 0 : 1)
                            .ThenByDescending(s => s.Ots)
                            .ToList();
                        while (remaining > 0 && restByOts.Count > 0)
                        {
                            bool any = false;
                            foreach (var s in restByOts)
                            {
                                if (remaining <= 0) break;
                                var cap = EffectiveSkuMax(s.SKUMax, s.StoreID, line.ItemCode);
                                var current = allocs.TryGetValue(s.StoreID, out var row) ? row.AllocQty : 0;
                                if (current >= cap) continue;
                                allocs[s.StoreID] = row is null
                                    ? MakeRow(s.StoreID, s.Country, s.VolumeGroup, s.MerchNeedMonth, cap, 1, 0)
                                    : row with { AllocQty = current + 1 };
                                remaining--;
                                any = true;
                            }
                            if (!any) break;
                        }

                        // 4. Overflow: allocate leftover ignoring SKU Max caps —
                        //    RR across ALL stores by OTS DESC.
                        if (remaining > 0)
                        {
                            var allByOts = stores
                                .OrderBy(s => s.Ots.HasValue ? 0 : 1)
                                .ThenByDescending(s => s.Ots)
                                .ToList();
                            int idx = 0;
                            while (remaining > 0 && allByOts.Count > 0)
                            {
                                var s = allByOts[idx % allByOts.Count];
                                var cap = EffectiveSkuMax(s.SKUMax, s.StoreID, line.ItemCode);
                                if (allocs.TryGetValue(s.StoreID, out var row))
                                    allocs[s.StoreID] = row with
                                    {
                                        AllocQty        = row.AllocQty + 1,
                                        RoundRobinExtra = row.RoundRobinExtra + 1,
                                    };
                                else
                                    allocs[s.StoreID] = MakeRow(s.StoreID, s.Country, s.VolumeGroup, s.MerchNeedMonth, cap, 1, 1);
                                remaining--;
                                idx++;
                            }
                        }
                    }
                    else
                    {
                        // Condition B: RR ALL stores by OTS DESC up to Effective SKU Max.
                        var allByOts = stores
                            .OrderBy(s => s.Ots.HasValue ? 0 : 1)
                            .ThenByDescending(s => s.Ots)
                            .ToList();
                        while (remaining > 0 && allByOts.Count > 0)
                        {
                            bool any = false;
                            foreach (var s in allByOts)
                            {
                                if (remaining <= 0) break;
                                var cap = EffectiveSkuMax(s.SKUMax, s.StoreID, line.ItemCode);
                                var current = allocs.TryGetValue(s.StoreID, out var row) ? row.AllocQty : 0;
                                if (current >= cap) continue;
                                allocs[s.StoreID] = row is null
                                    ? MakeRow(s.StoreID, s.Country, s.VolumeGroup, s.MerchNeedMonth, cap, 1, 0)
                                    : row with { AllocQty = current + 1 };
                                remaining--;
                                any = true;
                            }
                            if (!any) break;
                        }
                    }
                }
                else
                {
                    while (remaining > 0)
                    {
                        bool any = false;
                        foreach (var s in stores)
                        {
                            if (remaining <= 0) break;
                            var current = allocs.TryGetValue(s.StoreID, out var row) ? row.AllocQty : 0;
                            if (current >= s.SKUMax) continue;
                            allocs[s.StoreID] = row is null
                                ? MakeRow(s.StoreID, s.Country, s.VolumeGroup, s.MerchNeedMonth, s.SKUMax, 1, 0)
                                : row with { AllocQty = current + 1 };
                            remaining--;
                            any = true;
                        }
                        if (!any) break;
                    }
                }

                // FillSKUMax pass 2: round-robin extras when cap hit but qty remains.
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
                                AllocQty        = row.AllocQty + 1,
                                RoundRobinExtra = row.RoundRobinExtra + 1,
                            };
                        }
                        else
                        {
                            allocs[s.StoreID] = MakeRow(s.StoreID, s.Country, s.VolumeGroup, s.MerchNeedMonth, s.SKUMax, 1, 1);
                        }
                        remaining--;
                        idx++;
                    }
                }

                // Commit allocations + mutate runningAlloc so next item's OTS reflects what
                // we just gave out.
                foreach (var row in allocs.Values)
                {
                    result.Add(row);
                    var key = (row.StoreID, divCode);
                    runningAlloc[key] = runningAlloc.GetValueOrDefault(key, 0) + row.AllocQty;
                }
            }
        }

        // Sanity check: total allocated must equal sum of PO line qtys (Fill SKUMax)
        // or be <= total in RoundRobin (excess unallocated when all stores at cap).
        var poTotal = lines.Sum(l => l.Qty);
        var allocTotal = result.Sum(r => r.AllocQty);
        if (runOption == RunOption.FillSKUMax && allocTotal != poTotal)
            Console.Error.WriteLine($"[ContainerAllocation] WARN: Fill SKUMax allocated {allocTotal} vs PO total {poTotal} (delta {allocTotal - poTotal}).");
        if (runOption == RunOption.RoundRobin && allocTotal > poTotal)
            Console.Error.WriteLine($"[ContainerAllocation] WARN: RoundRobin over-allocated {allocTotal} vs PO total {poTotal} (delta {allocTotal - poTotal}).");

        return new AllocationProcessResult(result, blocked);
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
            VALUES (@ct, @c, @wh, @ro, @rc, @tq, DATEADD(hour, 4, SYSUTCDATETIME()), @u);",
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

        var now = DateTime.UtcNow.AddHours(4);  // GST stamp for Trndate/Time1
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
    /// Bulk-write the allocation rows DIRECTLY to LPMSIM.dbo.WMS_ContAllocationData
    /// (no draft round-trip). Used by the simplified Container Allocation flow where
    /// Process always saves immediately.
    /// </summary>
    public async Task<int> SaveFinalDirectAsync(string genCountry, string contno, string allocationCountries,
        string? warehouse, IReadOnlyList<AllocationRow> rows, RunOption runOption,
        IReadOnlyList<BlockedItemRow>? blocked = null,
        IProgress<AllocationProgress>? progress = null, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;
        var roTag = runOption.ToString();
        await using var c = OpenOnPremBackup();

        // 1) Find any prior Header batches for (GenCountry, ContNo, RunOption) — re-Process
        //    replaces the matching slice. Delete their detail + blocked + header rows.
        //    Sub-progress so the user can tell which DELETE is the slow one.
        progress?.Report(new AllocationProgress(0, rows.Count, "Saving: looking up prior batches"));
        var priorBatches = (await c.QueryAsync<int>(new CommandDefinition(@"
            SELECT BatchNo FROM LPMSIM.dbo.WMS_Cont_Allocation_Header
            WHERE GenCountry = @gc AND ContNo = @c AND RunOption = @ro",
            new { gc = genCountry, c = contno, ro = roTag },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        if (priorBatches.Count > 0)
        {
            progress?.Report(new AllocationProgress(0, rows.Count, $"Saving: deleting prior detail rows ({priorBatches.Count} batch(es))"));
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LPMSIM.dbo.WMS_ContAllocationData    WHERE BatchNo IN @bs",
                new { bs = priorBatches }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            progress?.Report(new AllocationProgress(0, rows.Count, "Saving: deleting prior blocked rows"));
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LPMSIM.dbo.WMS_ContAllocationBlocked WHERE BatchNo IN @bs",
                new { bs = priorBatches }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            progress?.Report(new AllocationProgress(0, rows.Count, "Saving: deleting prior header rows"));
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LPMSIM.dbo.WMS_Cont_Allocation_Header WHERE BatchNo IN @bs",
                new { bs = priorBatches }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }

        // 2) Create the new Header row and read back BatchNo.
        var totalQty = rows.Sum(r => r.AllocQty);
        var batchNo = await c.ExecuteScalarAsync<int>(new CommandDefinition(@"
            INSERT INTO LPMSIM.dbo.WMS_Cont_Allocation_Header
                (ContNo, Warehouse, GenCountry, Country, RunOption,
                 RowCount1, TotalQty, ProcessedBy)
            VALUES (@c, @wh, @gc, @ac, @ro, @rc, @tq, @u);
            SELECT CAST(SCOPE_IDENTITY() AS INT);",
            new { c = contno, wh = warehouse, gc = genCountry, ac = allocationCountries,
                  ro = roTag, rc = rows.Count, tq = totalQty, u = user.Name },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // 3) Write blocked rows (small list — per-row INSERT is fine).
        if (blocked is { Count: > 0 })
        {
            const string blkSql = @"
                INSERT INTO LPMSIM.dbo.WMS_ContAllocationBlocked
                    (BatchNo, Country, ContNo, RunOption, ItemCode, ItemName, StoreID, StoreName,
                     DivCode, Division, Department, PoQty, BlockReason, CreatedBy)
                VALUES
                    (@BatchNo, @Country, @ContNo, @RunOption, @ItemCode, @ItemName, @StoreID, @StoreName,
                     @DivCode, @Division, @Department, @PoQty, @BlockReason, @CreatedBy)";
            foreach (var b in blocked)
            {
                await c.ExecuteAsync(new CommandDefinition(blkSql, new
                {
                    BatchNo = batchNo, Country = b.Country, ContNo = b.Contno, RunOption = roTag,
                    b.ItemCode, b.ItemName, b.StoreID, b.StoreName,
                    b.DivCode, b.Division, b.Department, b.PoQty, b.BlockReason,
                    CreatedBy = user.Name,
                }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }
        }

        // 4) Bulk-copy detail rows tagged with BatchNo + per-row Country + enrichment columns.
        // BuildingCategory now = Division (P3 spec; was the SIM country in P1/P2).
        // ResultType comes from WMS_Building_PalletTypes (S vs W per item season);
        // FinalResult mirrors ResultType (Q-A). Result stays NULL.
        progress?.Report(new AllocationProgress(0, rows.Count, "Saving: bulk insert"));
        var dt = new System.Data.DataTable();
        dt.Columns.Add("BatchNo",          typeof(int));
        dt.Columns.Add("ContNo",           typeof(string));
        dt.Columns.Add("Country",          typeof(string));
        dt.Columns.Add("TrnDate",          typeof(DateTime));
        dt.Columns.Add("Time1",            typeof(TimeSpan));
        dt.Columns.Add("UPC",              typeof(string));
        dt.Columns.Add("Itemcode",         typeof(string));
        dt.Columns.Add("Barcode",          typeof(string));
        dt.Columns.Add("GroupCode",        typeof(string));
        dt.Columns.Add("Qty",              typeof(int));
        dt.Columns.Add("SkuMax",           typeof(int));
        dt.Columns.Add("AllocatedQty",     typeof(int));
        dt.Columns.Add("PrevAllocatedQty", typeof(int));
        dt.Columns.Add("QtyIssue",         typeof(int));
        dt.Columns.Add("StoreID",          typeof(string));
        dt.Columns.Add("TcmContno",        typeof(string));
        dt.Columns.Add("Itemname",         typeof(string));
        dt.Columns.Add("BuildingCategory", typeof(string));
        dt.Columns.Add("LPMDt",            typeof(DateTime));
        dt.Columns.Add("ORAPONo",          typeof(string));
        dt.Columns.Add("Division",         typeof(string));
        dt.Columns.Add("Brand",            typeof(string));
        dt.Columns.Add("DivCode",          typeof(int));
        dt.Columns.Add("Department",       typeof(string));
        dt.Columns.Add("Season",           typeof(string));
        dt.Columns.Add("Style",            typeof(string));
        dt.Columns.Add("Size",             typeof(string));
        dt.Columns.Add("SalesPrice",       typeof(decimal));
        dt.Columns.Add("ResultType",       typeof(string));
        dt.Columns.Add("FinalResult",      typeof(string));
        dt.Columns.Add("Remarks",          typeof(string));
        dt.Columns.Add("OTS",              typeof(double));

        var now = DateTime.UtcNow.AddHours(4);  // GST stamp for Trndate/Time1
        var trnDate = now.Date;
        var time1 = new TimeSpan(now.Hour, now.Minute, now.Second);

        foreach (var r in rows)
        {
            dt.Rows.Add(
                batchNo,
                r.Contno, r.Country, trnDate, time1, r.ItemCode, r.ItemCode,
                r.ItemCode,                                  // Barcode = ItemCode
                r.VolumeGroup,
                r.AllocQty, r.SkuMax, r.AllocQty, r.PrevAllocatedQty, 0,
                r.StoreID, r.Contno,
                (object?)r.ItemName ?? DBNull.Value,
                (object?)r.Division ?? DBNull.Value,         // BuildingCategory = Division
                (object?)r.LPMDt ?? DBNull.Value, r.OraPONo,
                (object?)r.Division ?? DBNull.Value,
                (object?)r.Brand ?? DBNull.Value,
                r.DivCode,
                (object?)r.Department ?? DBNull.Value,
                (object?)r.Season ?? DBNull.Value,
                (object?)r.Style ?? DBNull.Value,
                (object?)r.Size ?? DBNull.Value,
                (object?)r.SalesPrice ?? DBNull.Value,
                (object?)r.PalletType ?? DBNull.Value,        // ResultType
                (object?)r.PalletType ?? DBNull.Value,        // FinalResult mirrors ResultType
                (object?)(r.RoundRobinExtra > 0 ? $"RR+{r.RoundRobinExtra}" : null) ?? DBNull.Value,
                (object?)r.OTS ?? DBNull.Value);
        }

        c.ChangeDatabase("LPMSIM");
        using var bulk = new SqlBulkCopy(c)
        {
            DestinationTableName = "dbo.WMS_ContAllocationData",
            BatchSize            = 1000,
            BulkCopyTimeout      = CommandTimeoutSeconds,
            NotifyAfter          = 500,
        };
        bulk.SqlRowsCopied += (_, e) =>
            progress?.Report(new AllocationProgress((int)e.RowsCopied, rows.Count, "Saving to LPMSIM"));
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);

        progress?.Report(new AllocationProgress(rows.Count, rows.Count, "Saving: done"));
        return batchNo;
    }

    /// <summary>
    /// Sum of orgqty in usa.dbo.usaorgfile_LPM for the container — drives the
    /// 'Total PO Qty' card on the allocation page. Returns 0 when no rows match.
    /// </summary>
    public async Task<long> GetTotalPoQtyAsync(string contno, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await c.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT CAST(ISNULL(SUM(orgqty),0) AS BIGINT) FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK) WHERE ContNo = @c",
            new { c = contno }, commandTimeout: 60, cancellationToken: ct)) ?? 0;
    }

    /// <summary>
    /// Load rows from LPMSIM.dbo.WMS_ContAllocationData and map back to AllocationRow.
    /// Fields not stored in the final table (PoQty, SkuMax, VolumeGroup, etc.) come
    /// back as defaults; UI still displays Allocated Qty, StoreID, Division, etc.
    /// </summary>
    public async Task<List<AllocationRow>> LoadFinalAsync(string genCountry, string contno, RunOption runOption, CancellationToken ct = default)
    {
        var roTag = runOption.ToString();
        await using var c = OpenOnPremBackup();
        return await LoadAllocationDetailAsync(c,
            "JOIN LPMSIM.dbo.WMS_Cont_Allocation_Header h WITH (NOLOCK) ON h.BatchNo = d.BatchNo " +
            "WHERE h.GenCountry = @gc AND h.ContNo = @c AND h.RunOption = @ro",
            new { gc = genCountry, c = contno, ro = roTag }, ct);
    }

    /// <summary>
    /// Reset Final: deletes all rows from LPMSIM.dbo.WMS_ContAllocationData for the
    /// given container so the page unlocks and Process can run again. Destructive —
    /// caller is responsible for confirming with the user. Returns rows deleted.
    /// </summary>
    public async Task<int> ResetFinalAsync(string genCountry, string contno, RunOption runOption, CancellationToken ct = default)
    {
        var roTag = runOption.ToString();
        await using var c = OpenOnPremBackup();
        var batches = (await c.QueryAsync<int>(new CommandDefinition(@"
            SELECT BatchNo FROM LPMSIM.dbo.WMS_Cont_Allocation_Header
             WHERE GenCountry = @gc AND ContNo = @c AND RunOption = @ro",
            new { gc = genCountry, c = contno, ro = roTag },
            commandTimeout: 120, cancellationToken: ct))).ToList();
        if (batches.Count == 0) return 0;

        var n = await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WMS_ContAllocationData    WHERE BatchNo IN @bs",
            new { bs = batches }, commandTimeout: 120, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WMS_ContAllocationBlocked WHERE BatchNo IN @bs",
            new { bs = batches }, commandTimeout: 120, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WMS_Cont_Allocation_Header WHERE BatchNo IN @bs",
            new { bs = batches }, commandTimeout: 120, cancellationToken: ct));
        return n;
    }

    /// <summary>Load saved blocked items for the (Container, RunOption).</summary>
    public async Task<List<BlockedItemRow>> LoadBlockedAsync(string genCountry, string contno, RunOption runOption, CancellationToken ct = default)
    {
        var roTag = runOption.ToString();
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<BlockedItemRow>(new CommandDefinition(@"
            SELECT b.ContNo AS Contno, b.ItemCode, b.ItemName, b.Division, b.Department,
                   b.StoreID, b.StoreName, b.Country, b.PoQty, b.DivCode, b.BlockReason
              FROM LPMSIM.dbo.WMS_ContAllocationBlocked b WITH (NOLOCK)
              JOIN LPMSIM.dbo.WMS_Cont_Allocation_Header h WITH (NOLOCK) ON h.BatchNo = b.BatchNo
             WHERE h.GenCountry = @gc AND h.ContNo = @c AND h.RunOption = @ro
             ORDER BY b.ItemCode, b.StoreID",
            new { gc = genCountry, c = contno, ro = roTag },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<AllocationStatus> GetStatusAsync(string genCountry, string contno, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var d = await c.QueryFirstOrDefaultAsync<(int? RowCount1, int? TotalQty, string? RunOption)>(new CommandDefinition(
            "SELECT RowCount1, TotalQty, RunOption FROM LPMSIM.dbo.WMS_ContAllocationDraftHeader WHERE Country = @ct AND ContNo = @c",
            new { ct = genCountry, c = contno }, cancellationToken: ct));
        var hasDraft = d.RowCount1 is not null;
        var draftRows = d.RowCount1 ?? 0;

        // Per-RunOption final row counts from the Header table. Each Process run creates
        // one Header row per (GenCountry, ContNo, RunOption); RowCount1 holds the saved total.
        var f = await c.QueryFirstOrDefaultAsync<(int Total, DateTime? Max1, int Fsm, int Rr)>(new CommandDefinition(@"
            SELECT
                Total = ISNULL(SUM(RowCount1), 0),
                Max1  = MAX(ProcessedTS),
                Fsm   = ISNULL(SUM(CASE WHEN RunOption = 'FillSKUMax' THEN RowCount1 ELSE 0 END), 0),
                Rr    = ISNULL(SUM(CASE WHEN RunOption = 'RoundRobin' THEN RowCount1 ELSE 0 END), 0)
            FROM LPMSIM.dbo.WMS_Cont_Allocation_Header
            WHERE GenCountry = @gc AND ContNo = @c",
            new { gc = genCountry, c = contno }, cancellationToken: ct));
        var hasFinal = f.Total > 0;
        return new AllocationStatus(hasDraft, hasFinal, draftRows, f.Total, f.Max1, d.RunOption, f.Fsm, f.Rr);
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
            (@ContNo, CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE), CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS TIME(0)),
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

    // ===================== P2: SIM countries (allocation destinations) =====================
    // Excludes 'Ex2Locations' — not a real allocation destination, per user request.
    public async Task<List<string>> GetSimCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT SIMCountry
                FROM bfldata.dbo.DataSettings WITH (NOLOCK)
               WHERE SIMCountry IS NOT NULL
                 AND LTRIM(RTRIM(SIMCountry)) <> ''
                 AND SIMCountry <> 'Ex2Locations'
               ORDER BY SIMCountry",
            cancellationToken: ct));
        return list.AsList();
    }

    // ===================== P2: Processed Contnos dropdown =====================
    public async Task<List<string>> GetProcessedContnosAsync(string genCountry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(genCountry)) return new();
        await using var c = OpenOnPremBackup();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT ContNo
                FROM LPMSIM.dbo.WMS_Cont_Allocation_Header WITH (NOLOCK)
               WHERE GenCountry = @gc
               ORDER BY ContNo",
            new { gc = genCountry }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return list.AsList();
    }

    /// <summary>Latest batch (highest BatchNo) for (GenCountry, ContNo). When runOption is
    /// passed, scopes to that algorithm so the Process / Load flows can pick the right
    /// Header (a container can have one batch per RunOption).</summary>
    public async Task<BatchInfo?> GetLatestBatchInfoAsync(string genCountry, string contno,
        string? runOption = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(genCountry) || string.IsNullOrWhiteSpace(contno)) return null;
        await using var c = OpenOnPremBackup();
        var b = await c.QueryFirstOrDefaultAsync<BatchInfo>(new CommandDefinition(@"
            SELECT TOP 1
                   BatchNo, ContNo, Warehouse, GenCountry, Country, RunOption,
                   RowCount1, TotalQty, ProcessedTS, ProcessedBy, ApprovedDt, ApprovedBy
              FROM LPMSIM.dbo.WMS_Cont_Allocation_Header WITH (NOLOCK)
             WHERE GenCountry = @gc AND ContNo = @c
               AND (@ro IS NULL OR RunOption = @ro)
             ORDER BY BatchNo DESC",
            new { gc = genCountry, c = contno, ro = runOption },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return b;
    }

    /// <summary>P4 Approve. Stamps ApprovedDt = SYSDATETIME() + ApprovedBy = current user
    /// on the latest Header matching (GenCountry, ContNo, RunOption). Returns true when a
    /// row was actually updated; false when no matching unapproved batch exists.</summary>
    public async Task<bool> ApproveAsync(string genCountry, string contno, RunOption runOption, CancellationToken ct = default)
    {
        var roTag = runOption.ToString();
        await using var c = OpenOnPremBackup();
        var n = await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE LPMSIM.dbo.WMS_Cont_Allocation_Header
               SET ApprovedDt = DATEADD(hour, 4, SYSUTCDATETIME()),
                   ApprovedBy = @u
             WHERE GenCountry = @gc AND ContNo = @c AND RunOption = @ro
               AND ApprovedDt IS NULL",
            new { gc = genCountry, c = contno, ro = roTag, u = user.Name },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return n > 0;
    }

    /// <summary>Load detail rows for a specific BatchNo. Used by the "Load Processed Data" path.
    /// Delegates to the shared loader so the re-opened grid carries PoQty, LPM, Brand,
    /// StoreName, MerchNeedMonth, DivCode, OTS — the columns the report views need.</summary>
    public async Task<List<AllocationRow>> LoadFinalByBatchAsync(int batchNo, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await LoadAllocationDetailAsync(c, "WHERE d.BatchNo = @b", new { b = batchNo }, ct);
    }

    public async Task<List<BlockedItemRow>> LoadBlockedByBatchAsync(int batchNo, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<BlockedItemRow>(new CommandDefinition(@"
            SELECT ContNo AS Contno, ItemCode, ItemName, Division, Department,
                   StoreID, StoreName, Country, PoQty, DivCode, BlockReason
              FROM LPMSIM.dbo.WMS_ContAllocationBlocked WITH (NOLOCK)
             WHERE BatchNo = @b
             ORDER BY ItemCode, StoreID",
            new { b = batchNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Shared loader for `WMS_ContAllocationData` rows. Caller supplies the JOIN +
    /// WHERE clause (e.g. " WHERE d.BatchNo = @b " or with a JOIN to Header by RunOption)
    /// and matching parameter object. Persisted columns (Itemname, GroupCode, OTS, ...)
    /// come straight from the detail row; transient fields (PoQty, Brand, StoreName,
    /// MerchNeedMonth, LPM, DivCode) are filled by 5 prefetches and joined in memory so
    /// every report view shows complete data when a batch is re-opened.
    /// </summary>
    private async Task<List<AllocationRow>> LoadAllocationDetailAsync(
        SqlConnection c, string joinAndWhereSql, object filterParams, CancellationToken ct)
    {
        var rows = (await c.QueryAsync<(string ContNo, string? OraPONo, string? ItemCode, string? ItemName, string? Brand,
                                       int? Qty, int? SkuMax, int? DivCode, string? StoreID, string? Country, string? GroupCode, string? Division,
                                       string? Remarks, DateTime? LPMDt, double? OTS)>(new CommandDefinition($@"
            SELECT d.ContNo, d.ORAPONo, d.Itemcode, d.Itemname, d.Brand, d.Qty, d.SkuMax, d.DivCode, d.StoreID, d.Country,
                   d.GroupCode, d.Division, d.Remarks, d.LPMDt, d.OTS
              FROM LPMSIM.dbo.WMS_ContAllocationData d WITH (NOLOCK)
              {joinAndWhereSql}
             ORDER BY d.IdNo",
            filterParams, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        if (rows.Count == 0) return new();

        var distinctContnos = rows.Select(r => r.ContNo).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();
        var distinctItems   = rows.Select(r => r.ItemCode!).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();
        var distinctStores  = rows.Select(r => r.StoreID!).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();

        // PoQty + LPM per (ContNo, OraPONo, ItemCode) from usaorgfile_LPM.
        var poInfo = new Dictionary<(string ContNo, string OraPONo, string ItemCode), (int Qty, string? LPM)>();
        if (distinctContnos.Length > 0)
        {
            var poRows = await c.QueryAsync<(string ContNo, string? OraPONo, string ItemCode, int? Qty, string? LPM)>(new CommandDefinition(@"
                SELECT ContNo, OraPONo, ItemCode,
                       SUM(CAST(ISNULL(orgqty,0) AS INT)) AS Qty,
                       MAX(LPM)                          AS LPM
                  FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK)
                 WHERE ContNo IN @contnos
                 GROUP BY ContNo, OraPONo, ItemCode",
                new { contnos = distinctContnos }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var p in poRows) poInfo[(p.ContNo, p.OraPONo ?? "", p.ItemCode)] = (p.Qty ?? 0, p.LPM);
        }

        // Brand: read directly from d.Brand (persisted on the detail row from this deploy
        // onwards). Legacy batches show NULL until re-processed.

        // StoreName per StoreID from DataSettings.
        var storeNameById = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (distinctStores.Length > 0)
        {
            var snRows = await c.QueryAsync<(string StoreID, string? PBFullname)>(new CommandDefinition(@"
                SELECT StoreID, MAX(PBFullname) AS PBFullname
                  FROM bfldata.dbo.DataSettings WITH (NOLOCK)
                 WHERE StoreID IN @stores AND PBFullname IS NOT NULL
                 GROUP BY StoreID",
                new { stores = distinctStores }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var s in snRows) storeNameById[s.StoreID] = s.PBFullname;
        }

        // DivCode: read directly from d.DivCode (persisted from this deploy onwards).

        // MerchNeedMonth per (StoreID, DivCode) for the current month. Use the per-row
        // d.DivCode values from the rows just loaded to build the @divs filter.
        var merchByKey = new Dictionary<(string StoreID, int DivCode), int>();
        var distinctDivs = rows.Where(r => r.DivCode is > 0).Select(r => r.DivCode!.Value).Distinct().ToArray();
        if (distinctStores.Length > 0 && distinctDivs.Length > 0)
        {
            var merchRows = await c.QueryAsync<(string StoreID, int DivCode, int MerchNeedMonth)>(new CommandDefinition(@"
                SELECT StoreID, DivCode, ISNULL(MerchNeedMonth, 0) AS MerchNeedMonth
                  FROM dbo.LPM_EOM_Output WITH (NOLOCK)
                 WHERE StoreID IN @stores AND DivCode IN @divs
                   AND Month1 = MONTH(DATEADD(hour, 4, SYSUTCDATETIME())) AND Year1 = YEAR(DATEADD(hour, 4, SYSUTCDATETIME()))",
                new { stores = distinctStores, divs = distinctDivs }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var m in merchRows) merchByKey[(m.StoreID, m.DivCode)] = m.MerchNeedMonth;
        }

        return rows.Select(r =>
        {
            var item    = r.ItemCode ?? "";
            var store   = r.StoreID ?? "";
            var divCode = r.DivCode ?? 0;
            poInfo.TryGetValue((r.ContNo, r.OraPONo ?? "", item), out var po);
            storeNameById.TryGetValue(store, out var storeName);
            merchByKey.TryGetValue((store, divCode), out var merch);

            return new AllocationRow(
                Contno: r.ContNo,
                OraPONo: r.OraPONo ?? "",
                ItemCode: item,
                ItemName: r.ItemName,
                Brand: r.Brand,
                PoQty: po.Qty,
                StoreID: store,
                StoreName: storeName,
                Country: r.Country ?? "",
                Division: r.Division,
                VolumeGroup: r.GroupCode ?? "",
                SkuMax: r.SkuMax ?? 0,
                AllocQty: r.Qty ?? 0,
                MerchNeedMonth: merch,
                DivCode: divCode,
                RoundRobinExtra: ParseRoundRobin(r.Remarks),
                LPM: po.LPM,
                LPMDt: r.LPMDt,
                OTS: r.OTS);
        }).ToList();
    }
}
