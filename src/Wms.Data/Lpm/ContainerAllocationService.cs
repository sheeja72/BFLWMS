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
    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
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
    public async Task<ContainerAllocationValidationResult> ValidateAsync(
        string country, string contno, CancellationToken ct = default)
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

        // 1. Contreceipt.TCMNo
        await using (var c = OpenOnPremBackup())
        {
            var ok = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM bfldata.dbo.Contreceipt WITH (NOLOCK) WHERE TCMNo = @c",
                new { c = contno }, cancellationToken: ct)) == 1;
            steps.Add(new ValidationStep(
                "Container exists in bfldata.Contreceipt (TCMNo)",
                ok,
                ok ? null : $"No row in bfldata.Contreceipt with TCMNo = '{contno}'."));
            if (!ok) return new ContainerAllocationValidationResult(false, steps);

            // 2. usa.knbboxes
            var ok2 = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM usa.dbo.KNBBoxes WITH (NOLOCK) WHERE contno = @c",
                new { c = contno }, cancellationToken: ct)) == 1;
            steps.Add(new ValidationStep(
                "Container exists in usa.KNBBoxes",
                ok2,
                ok2 ? null : $"No row in usa.KNBBoxes with contno = '{contno}'."));
            if (!ok2) return new ContainerAllocationValidationResult(false, steps);

            // 3. Three-way qty match: USAOrgFile vs usaorgfile_LPM vs hodata..vUSAOrder
            var q1 = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT ISNULL(SUM(orgqty),0) FROM usa.dbo.USAOrgFile WITH (NOLOCK) WHERE ContNo = @c",
                new { c = contno }, cancellationToken: ct)) ?? 0;
            var q2 = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT ISNULL(SUM(orgqty),0) FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK) WHERE ContNo = @c",
                new { c = contno }, cancellationToken: ct)) ?? 0;
            var q3 = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT ISNULL(SUM(qty),0) FROM hodata.dbo.vUSAOrder WITH (NOLOCK) WHERE refno = @c",
                new { c = contno }, cancellationToken: ct)) ?? 0;
            var qtyOk = q1 > 0 && q1 == q2 && q2 == q3;
            steps.Add(new ValidationStep(
                "Three-way qty match (USAOrgFile = usaorgfile_LPM = hodata.vUSAOrder)",
                qtyOk,
                qtyOk ? $"All three = {q1}." : $"USAOrgFile={q1}, usaorgfile_LPM={q2}, vUSAOrder={q3} — must be > 0 and equal."));
            if (!qtyOk) return new ContainerAllocationValidationResult(false, steps);
        }

        // 4. Not already started building (Azure WMS — any WmsOpenBox row for this Contno)
        // 5. Not already completed (Azure WMS — WmsBuildingCompletion)
        await using (var w = OpenWms())
        {
            var building = await w.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM dbo.WmsOpenBox WITH (NOLOCK) WHERE Contno = @c",
                new { c = contno }, cancellationToken: ct)) == 1;
            steps.Add(new ValidationStep(
                "Container not yet being built (no WmsOpenBox row)",
                !building,
                building ? $"Container {contno} already has open box(es) in WmsOpenBox." : null));
            if (building) return new ContainerAllocationValidationResult(false, steps);

            var completed = await w.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM dbo.WmsBuildingCompletion WITH (NOLOCK) WHERE Country = @ct AND ContNo = @c",
                new { ct = country, c = contno }, cancellationToken: ct)) == 1;
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
    public async Task<List<AllocationRow>> ProcessAllocationAsync(string contno, CancellationToken ct = default)
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

        // 2. Resolve DivCode per item via vupc_subclass.DivID
        var divByItem = (await c.QueryAsync<(string itemcode, int? DivID)>(new CommandDefinition(@"
            SELECT itemcode, DivID
            FROM datareporting.dbo.vupc_subclass WITH (NOLOCK)
            WHERE itemcode IN @items",
            new { items = lines.Select(l => l.ItemCode).Distinct().ToArray() }, cancellationToken: ct)))
            .GroupBy(r => r.itemcode)
            .ToDictionary(g => g.Key, g => g.First().DivID ?? 0, StringComparer.OrdinalIgnoreCase);

        // 3. For each PO line, query eligible stores+rules in priority order
        //    (VolumeGroup ASC = A first, then MerchNeedMonth DESC).
        foreach (var line in lines)
        {
            if (line.Qty <= 0) continue;
            if (!divByItem.TryGetValue(line.ItemCode, out var divCode) || divCode == 0) continue;

            var stores = (await c.QueryAsync<(string StoreID, string Country, string VolumeGroup, int MerchNeedMonth, int SKUMax)>(
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
                    new { q = line.Qty, d = divCode }, cancellationToken: ct))).AsList();

            if (stores.Count == 0) continue;

            // Pass 1: walk in order, fill each store up to its SKUMax.
            var allocs = new Dictionary<string, AllocationRow>(StringComparer.OrdinalIgnoreCase);
            var remaining = line.Qty;
            foreach (var s in stores)
            {
                if (remaining <= 0) break;
                var take = Math.Min(s.SKUMax, remaining);
                if (take <= 0) continue;
                allocs[s.StoreID] = new AllocationRow(
                    Contno: line.ContNo,
                    OraPONo: line.OraPONo,
                    ItemCode: line.ItemCode,
                    PoQty: line.Qty,
                    ShopCode: s.StoreID,
                    Country: s.Country,
                    VolumeGroup: s.VolumeGroup,
                    SkuMax: s.SKUMax,
                    AllocQty: take,
                    MerchNeedMonth: s.MerchNeedMonth,
                    DivCode: divCode,
                    RoundRobinExtra: 0,
                    LPM: line.LPM,
                    LPMDt: line.LPMDt);
                remaining -= take;
            }

            // Pass 2: round-robin if there's still qty left after every store was filled to cap.
            if (remaining > 0 && stores.Count > 0)
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
                        allocs[s.StoreID] = new AllocationRow(
                            line.ContNo, line.OraPONo, line.ItemCode, line.Qty,
                            s.StoreID, s.Country, s.VolumeGroup, s.SKUMax,
                            AllocQty: 1, s.MerchNeedMonth, divCode,
                            RoundRobinExtra: 1, line.LPM, line.LPMDt);
                    }
                    remaining--;
                    idx++;
                }
            }

            result.AddRange(allocs.Values);
        }

        return result;
    }

    // ===================== Confirm & Save to LPMSIM.WMS_ContAllocationData =====================
    // Inserts the allocation rows. Idempotency is NOT enforced — re-running will
    // create duplicates. (Add a delete-by-Contno first if you want re-run support.)
    public async Task<int> SaveAllocationAsync(IReadOnlyList<AllocationRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;
        await using var c = OpenOnPremBackup();
        // INSERT goes to LPMSIM via 3-part naming (OnPremBackup conn has access).
        var sql = @"INSERT INTO LPMSIM.dbo.WMS_ContAllocationData
            (ContNo, TrnDate, Time1, UPC, Itemcode, GroupCode, Qty, QtyIssue,
             ShopCode, TcmContno, ORAPONo, LPMDt, BuildingCategory, Remarks)
          VALUES
            (@ContNo, CAST(SYSDATETIME() AS DATE), CAST(SYSDATETIME() AS TIME(0)),
             @UPC, @ItemCode, @GroupCode, @Qty, 0,
             @ShopCode, @ContNo, @OraPONo, @LPMDt, @Country, @Remarks);";
        var affected = 0;
        foreach (var r in rows)
        {
            affected += await c.ExecuteAsync(new CommandDefinition(sql, new
            {
                ContNo    = r.Contno,
                UPC       = r.ItemCode,
                ItemCode  = r.ItemCode,
                GroupCode = r.VolumeGroup,
                Qty       = r.AllocQty,
                ShopCode  = r.ShopCode,
                OraPONo   = r.OraPONo,
                LPMDt     = r.LPMDt,
                Country   = r.Country,
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
