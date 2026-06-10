using System.Net.Http.Headers;
using System.Text;
using Aiwms.Core;
using Aiwms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;
using MiniExcelLibs;

namespace Aiwms.Data.ContainerProcess;

/// <summary>
/// Implements the legacy VB ContainerProcess logic.
/// For each item Ã— orgqty in the container it runs the 3-tier allocation:
///   Online (ToysInitialAllocationDetail) â†' Export (PowerDrops / Brands / Ramadan) â†' Shop fallback.
/// Results are written to ONLINE.dbo.PhotoCheckingResult (+ optional Robo server).
/// </summary>
public class ContainerProcessingService(IConnectionConfig conn, ICurrentUser currentUser)
{
    private static readonly HttpClient _roboHttp = new();
    private const string RoboApiBase = "https://bflapigee-dev.bflgroup.ae/bfl-iq-api";
    private const string RoboApiToken =
        "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiIxMSIsImp0aSI6IjJmMzZiODM5MjAxZDAwZjhjNWViNzc0YTMxOWJjZDA3ZjMwODFiMmIzYWU0ZWYzYmQ3NzUzYTQzMWU0OGU3ODQxMTRjYjRhYmIxMDFjYmE4IiwiaWF0IjoxNzY5MjM5OTUzLjcyMzQ2NiwibmJmIjoxNzY5MjM5OTUzLjcyMzQ2NywiZXhwIjoxODMyMzExOTUzLjcxOTEwOSwic3ViIjoiMTQzIiwic2NvcGVzIjpbXX0.cg_hwNSyi3zOXvixJPRK_FQObQLks4-fH1YVtepp0YpZfnNhtstosTSwH3mWJP45JMmSfGzCnj1MiSmOi7zKXRtYHgYjdo0Bfzs-AIYa7yxjEfmQrLvKoRTiRT07AzTQRcS_b6AB-fCkuzojnSvwScB_-d9hKkkhey234YHLLGBJpNDMTtrsUIZiQEo7OSEEWZ4FpfxrNlyJIJMMT9gg3OvFfEklR8oqMiTZOgsOSsOIbBYPb0J1n6H7qyuGB5reSIj9uzDNXMjTmQtQJR-THVOJga7LSajP48ncHJfMimNrfoUDf6FnIX7WyeeFd--D4aZ44P7geKZ0Yqp5K2888PcB7FpBgSo1_VKu4X5S7-NNDVmv8Y6NA5WMj0l-EKhEWzAQxoJEENHU6C9DJqrMUC5SJDkTN8_5X7YvUxUbMOIpT2Vx8MXLCYwLzobvx8q8iZfwTLM7Pz7jHuJyA1qSA4H-pan37T9JrgGnsMz0fthFOOKv_8hJ2hoRbkHK13tf98pSFlbH19u4-6D5zR83hgbNQZNf_AsdL2SWwYibo85yziSl_tPWdQOU4Ipk_QCybKGu27QIaDKyY0xAWII8cKktZwtQa8FSyyo5kdsDNrfsfDzYK00iLxPXeLs8Tvi6vCiZ6bplBQexcOTGTmV95zYD4yb_nrS31m88x2TIn0M";

    // â"€â"€ connection helpers â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    private SqlConnection Open() =>
        Open(null);

    private SqlConnection Open(string? db)
    {
        var cs = db is null ? conn.GetAiwmsConnectionString() : conn.GetConnectionString(db);
        var c = new SqlConnection(cs);
        c.Open();
        return c;
    }

    // â"€â"€ 1. Validate items (check groupcode / BuildingCategory completeness) â"€â"€

    public async Task<ContProcessValidation> ValidateItemsAsync(string contno, CancellationToken ct = default)
    {
        await using var c = Open();
        var items = await LoadAndEnrichAsync(c, contno, ct);

        var missing = items
            .Where(x => string.IsNullOrEmpty(x.GroupCode) || string.IsNullOrEmpty(x.BuildingCategory))
            .Select(x => x.Itemcode)
            .Distinct()
            .ToList();

        if (missing.Count > 0)
            return new ContProcessValidation(false,
                $"{missing.Count} item(s) missing GroupCode/BuildingCategory  -  check tempdata.dbo.containerprocess_missing.",
                missing, null);

        return new ContProcessValidation(true, null, new List<string>(), items);
    }

    // â"€â"€ Pre-process checks (mirrors VB btnProcess_Click pre-flight) â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    public async Task<ContProcessPrecheck> ValidateAndPrepareAsync(string contno, CancellationToken ct = default)
    {
        await using var c = Open();

        // 1. Already fully processed?
        int existing = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT COUNT(1) FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK) WHERE ContNo=@c",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct)) ?? 0;
        if (existing > 0)
            return new ContProcessPrecheck(false, null, IsAlreadyProcessed: true);

        // 2. Already started — distinguish fully finished from mid-flight
        var startRow = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            "SELECT Finish FROM ONLINE.dbo.PhotoCheckingResultStart WITH (NOLOCK) WHERE ContNo=@c",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));
        if (startRow != null)
        {
            string startMsg = (string?)startRow.Finish == "Y"
                ? $"Container {contno} has already completed processing. Please check with IT."
                : $"Container {contno} has started processing but not completed. Please check with IT.";
            return new ContProcessPrecheck(false, startMsg);
        }

        // 3. Prepare allocation tables via stored procs
        await c.ExecuteAsync(new CommandDefinition(
            "EXEC Online.dbo.Stp_ProductionDept_Samples @ContNo=@c",
            new { c = contno }, commandTimeout: 600, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition(
            "EXEC Online.dbo.Stp_ExportAllocation_Brands @ContNo=@c",
            new { c = contno }, commandTimeout: 600, cancellationToken: ct));

// 5. Auto-detect Ramadan container
        bool isRamadan = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            @"SELECT TOP 1 1 FROM usa.dbo.Reserve_Groups WITH (NOLOCK)
              WHERE PalletType_result = 'RAMADAN'
                AND contno = @c
                AND trndate >= GETDATE() - 180",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct)) == 1;

        // 5b. Ramadan container validation
        if (isRamadan)
        {
            string? nonRamadanGroups = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                @"SELECT ISNULL(STRING_AGG(groupCode, ', '),'')
                  FROM usa.dbo.USAPriority WITH (NOLOCK)
                  WHERE groupCode IN (
                      SELECT DISTINCT GroupCode FROM usa.dbo.USAOrgfile WITH (NOLOCK)
                      WHERE ContNo = @c
                        AND ContNo IN (SELECT ContNo FROM usa.dbo.ContRamadanDept)
                  )
                  AND Department NOT LIKE '%Ramadan%'",
                new { c = contno }, commandTimeout: 300, cancellationToken: ct));
            if (!string.IsNullOrEmpty(nonRamadanGroups))
                return new ContProcessPrecheck(false,
                    $"RAMADAN container has groups with non-Ramadan department. Please contact IT. Groups: {nonRamadanGroups}");

            string? nonRamadanItems = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                @"SELECT ISNULL(STRING_AGG(Itemcode, ', '),'')
                  FROM usa.dbo.USAOrgfile WITH (NOLOCK)
                  WHERE ContNo = @c
                    AND GroupCode IN (
                        SELECT GroupCode FROM usa.dbo.USAPriority WITH (NOLOCK) WHERE Department LIKE '%Ramadan%'
                    )
                    AND Itemcode NOT IN (SELECT Itemcode FROM usa.dbo.RAMADANGroups WITH (NOLOCK))",
                new { c = contno }, commandTimeout: 300, cancellationToken: ct));
            if (!string.IsNullOrEmpty(nonRamadanItems))
                return new ContProcessPrecheck(false,
                    $"RAMADAN container has items outside Ramadan groups. Items: {nonRamadanItems}");
        }

        // 6. Container type  -  only USA supported; TCM requires separate flow
        bool isTcm = contno.StartsWith("TCM", StringComparison.OrdinalIgnoreCase);
        if (isTcm)
            return new ContProcessPrecheck(false,
                $"Container {contno} appears to be a TCM container. TCM processing is not yet supported in this tool.");

        string contType = isRamadan ? "USA-R" : "USA";

        // 7. 3-way qty validation: LPM = ORG = HODATA Order (only when LPM rows exist)
        bool hasLpm = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK) WHERE contno=@c",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct)) == 1;

        if (hasLpm)
        {
            var qty = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                @"SELECT
                      LpmQty   = (SELECT ISNULL(SUM(OrgQty),0) FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK) WHERE ContNo=@c),
                      OrgQty   = (SELECT ISNULL(SUM(OrgQty),0) FROM usa.dbo.USAOrgFile     WITH (NOLOCK) WHERE ContNo=@c),
                      OrderQty = (SELECT ISNULL(SUM(qty),0)    FROM HODATA.dbo.vUSAOrder   WITH (NOLOCK) WHERE refno=@c)",
                new { c = contno }, commandTimeout: 300, cancellationToken: ct));

            if (qty != null)
            {
                int lpmQty   = (int)(qty.LpmQty   ?? 0);
                int orgQty   = (int)(qty.OrgQty   ?? 0);
                int orderQty = (int)(qty.OrderQty ?? 0);
                if (lpmQty != orgQty || orgQty != orderQty)
                    return new ContProcessPrecheck(false,
                        $"Qty mismatch — LPM: {lpmQty}, ORG: {orgQty}, Order: {orderQty}. All three must match before processing.");
            }
        }

        // 8. Online allocation must exist
        int allocRows = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT COUNT(1) FROM ONLINE.dbo.ToysInitialAllocationDetail WITH (NOLOCK) WHERE UPPER(ContNo)=UPPER(@c)",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct)) ?? 0;
        if (allocRows == 0)
            return new ContProcessPrecheck(false,
                $"No online allocation found for {contno}. Complete the allocation step before processing.");

        // 9. Mark as started
        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO ONLINE.dbo.PhotoCheckingResultStart (ContNo, finish, trndate, time1, username) VALUES (@c, 'N', @d, @t, @u)",
            new { c = contno, d = DateTime.Now.Date, t = DateTime.Now.ToString("HH:mm:ss"), u = currentUser.Name }, commandTimeout: 300, cancellationToken: ct));

        return new ContProcessPrecheck(true, null, isRamadan, contType);
    }

    private async Task<List<ContProcessItem>> LoadAndEnrichAsync(
        SqlConnection c, string contno, CancellationToken ct)
    {
        var hasLpm = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK) WHERE contno = @c",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct)) == 1;

        var items = hasLpm
            ? (await LoadFromLpmAsync(c, contno, ct)).ToList()
            : (await LoadFromOrgFileAsync(c, contno, ct)).ToList();

        return await EnrichItemsAsync(c, items, ct);
    }

    // â"€â"€ 2. Run full container process â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    public async Task<ContProcessOutcome> ProcessAsync(
        string contno,
        bool isRamadanCont,
        bool manualChecking,
        List<ContProcessItem>? preloadedItems = null,
        IProgress<ContProcessProgress>? progress = null,
        CancellationToken ct = default)
    {
        // â"€â"€ Timing instrumentation  -  prints a breakdown to the server console â"€â"€
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var timings = new System.Collections.Generic.List<(string Label, long Ms)>();
        var swStep  = System.Diagnostics.Stopwatch.StartNew();
        void Tick(string label) { timings.Add((label, swStep.ElapsedMilliseconds)); swStep.Restart(); }

        await using var c = Open();
        Tick("OpenConnection");

        var (contExcOnline, contExcShop) = await GetContainerExclusionsAsync(c, contno, ct);
        Tick("GetContainerExclusions");

        var items = preloadedItems ?? await LoadAndEnrichAsync(c, contno, ct);

        var missing = items.Where(x => string.IsNullOrEmpty(x.GroupCode) || string.IsNullOrEmpty(x.BuildingCategory)).ToList();
        if (missing.Any())
            return new ContProcessOutcome(false,
                $"GroupCode/BuildingCategory missing for {missing.Count} item(s). Run validation first.",
                0, 0, new());

        int totalQty = items.Sum(x => x.OrgQty);
        int done = 0;
        var results = new List<ContProcessResultRow>(totalQty);

        // â"€â"€ Pre-load static lookups (1 query each, covers all items) â"€â"€â"€â"€â"€â"€â"€â"€
        var ceilings  = await LoadCeilingsAsync(c, items.Select(x => x.Division).Distinct().ToList(), ct);
        var locStock  = await LoadLocStockAsync(c, items.Select(x => x.Upc).Distinct().ToList(), ct);
        var allocInit = await LoadAllocInitialAsync(c, contno, items.Select(x => x.Itemcode).Distinct().ToList(), ct);
        Tick("PreLoadLookups");

        var shopMinsCache = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        // Pre-load PowerDrop groups  -  1 query for all items instead of 1 per item
        var pdGroups = new HashSet<string>((await c.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT GroupCode FROM USA.dbo.PowerDrops WITH (NOLOCK) WHERE ContNo=@c",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct))), StringComparer.OrdinalIgnoreCase);
        Tick("PreLoadPdGroups");

        // Pre-load container-level static data that PrepareBrandsContextAsync was re-querying per item
        int purCnt = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT COUNT(ContNo) FROM usa.dbo.USAPurchase WITH (NOLOCK) WHERE ContNo=@c",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct)) ?? 0;
        var foreignShopsGlobal = new HashSet<string>((await c.QueryAsync<string>(new CommandDefinition(
            "SELECT ShopName FROM BFLDATA.dbo.DataSettings WITH (NOLOCK) WHERE ExportActive='Y' AND FCCode NOT IN ('AED','DHS')",
            commandTimeout: 300, cancellationToken: ct))), StringComparer.OrdinalIgnoreCase);
        var nonProdShopsGlobal = new HashSet<string>((await c.QueryAsync<string>(new CommandDefinition(
            "SELECT ShopName FROM BFLDATA.dbo.DataSettings WITH (NOLOCK) WHERE FCCode<>'AED' AND ExportActive='Y' AND FCCode<>'ROB' AND ISNULL(Production,'N')<>'Y'",
            commandTimeout: 300, cancellationToken: ct))), StringComparer.OrdinalIgnoreCase);
        var itemExcMap = await LoadItemExclusionsAsync(c, items, ct);
        Tick("PreLoadStatic");

        // Pre-load UPC barcodes (realCode + itemType + origin) for all UPCs in one query
        var upcBarcodeMap = await LoadUpcBarcodesAsync(c, items.Select(x => x.Upc).Distinct().ToList(), ct);
        // Pre-load excludeexport_Item for the whole container  -  replaces per-item SELECT
        var excludeItemCached = new HashSet<string>((await c.QueryAsync<string>(new CommandDefinition(
            "SELECT UPC FROM usa.dbo.excludeexport_Item WITH (NOLOCK) WHERE ContNo=@c",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct))), StringComparer.OrdinalIgnoreCase);
        // GroupCode-level Brands context cache  -  ~14 queries run once per unique GC, not per item
        var brandsGroupCache = new Dictionary<string, BrandsGroupCtx?>(StringComparer.OrdinalIgnoreCase);
        Tick("PreLoadUpcAndExcludeCache");

        // Pre-load ExportAllocation_Itemcodes for the whole container — drives item-level allocation for ALL containers
        var itemAllocMap = await LoadItemAllocMapAsync(c, contno, ct);
        // Pre-load OriginExclude (static lookup — same origin repeats across many items)
        var originExcludeMap = await LoadOriginExcludeMapAsync(c, ct);
        Tick("PreLoadItemAllocAndOrigin");

        // Build full list of real itemcodes for the container
        var allRealCodes = items
            .Select(x => upcBarcodeMap.TryGetValue(x.Upc, out var uv) && !string.IsNullOrEmpty(uv.RealCode)
                ? uv.RealCode : x.Itemcode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Create #USAStock temp table — loaded once, all reads/writes hit this during processing,
        // then flushed back to the real table in a single MERGE at the end.
        await c.ExecuteAsync(new CommandDefinition(@"
            CREATE TABLE #USAStock (
                ItemCode      NVARCHAR(50) NOT NULL,
                Shop          NVARCHAR(50) NOT NULL,
                Quantity      INT NOT NULL DEFAULT 0,
                QtyToSend     INT NOT NULL DEFAULT 0,
                QtyReqd       INT NOT NULL DEFAULT 0,
                QtyReqdDirty  BIT NOT NULL DEFAULT 0,
                PRIMARY KEY (ItemCode, Shop)
            )", commandTimeout: 60, cancellationToken: ct));

        if (allRealCodes.Count > 0)
        {
            // Stage item codes via SqlBulkCopy — avoids 2100-parameter IN-list limit
            await c.ExecuteAsync(new CommandDefinition(
                "CREATE TABLE #ItemCodes (ItemCode NVARCHAR(50) NOT NULL PRIMARY KEY)",
                commandTimeout: 60, cancellationToken: ct));

            var dt = new System.Data.DataTable();
            dt.Columns.Add("ItemCode", typeof(string));
            foreach (var code in allRealCodes) dt.Rows.Add(code);

            using (var bcp = new SqlBulkCopy(c))
            {
                bcp.DestinationTableName = "#ItemCodes";
                bcp.BulkCopyTimeout = 60;
                await bcp.WriteToServerAsync(dt, ct);
            }

            await c.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO #USAStock(ItemCode,Shop,Quantity,QtyToSend,QtyReqd)
                  SELECT s.ItemCode,s.Shop,ISNULL(s.Quantity,0),ISNULL(s.QtyToSend,0),ISNULL(s.QtyReqd,0)
                  FROM usa.dbo.USAStock s WITH (NOLOCK)
                  JOIN #ItemCodes ic ON s.ItemCode=ic.ItemCode",
                commandTimeout: 300, cancellationToken: ct));
        }
        Tick("PreLoadUSAStock");

        // Per-item accumulators for the timing breakdown
        long msExcl = 0, msBuildShopMins = 0, msPdCtx = 0, msBrandsCtx = 0, msRamadanCtx = 0;
        long msWhileLoop = 0, msFlushOnline = 0, msFlushBrands = 0, msFlushPd = 0, msFlushRamadan = 0;

        foreach (var item in items)
        {
            int qty = item.OrgQty;

            // Per-item flags (once per SKU)
            bool isPdGroup    = pdGroups.Contains(item.GroupCode);
            string itemChkCode = upcBarcodeMap.TryGetValue(item.Upc, out var ucbv) && !string.IsNullOrEmpty(ucbv.RealCode) ? ucbv.RealCode : item.Itemcode;
            bool isRamadanItem    = itemAllocMap.ContainsKey(itemChkCode);
            bool isRamadanRMGroup = isRamadanItem && await IsRamadanRMGroupAsync(c, item.GroupCode, ct);

            swStep.Restart();
            // In-memory exclusion lookup (pre-loaded above  -  was 3 DB queries per item)
            bool excOnline = false, excShop = false;
            if (itemExcMap.ByUpc.TryGetValue(item.Upc, out var ueX))      { excOnline |= ueX.Online; excShop |= ueX.Shop; }
            if (itemExcMap.ByGc.TryGetValue(item.GroupCode, out var geX)) { excOnline |= geX.Online; excShop |= geX.Shop; }
            if (itemExcMap.StoppedSeasons.Contains(item.Season)) excOnline = true;
            msExcl += swStep.ElapsedMilliseconds;

            bool skipOnline = contExcOnline || excOnline;
            bool skipShop   = contExcShop   || excShop;

            // Per-item online state  -  read once, track in memory
            int ceiling = ceilings.GetValueOrDefault(item.Division, 0);
            int locQty  = locStock.GetValueOrDefault(item.Upc, 0);
            var ai = allocInit.GetValueOrDefault(item.Itemcode) ?? new AllocState(0, 0, 0, 0);
            int sampDone = 0, onlineDone = 0;

            // Per-item export contexts  -  prepared once, used per unit
            swStep.Restart();
            if (!shopMinsCache.ContainsKey(item.GroupCode))
                shopMinsCache[item.GroupCode] = await BuildShopMinsAsync(c, item.GroupCode, ct);
            msBuildShopMins += swStep.ElapsedMilliseconds;

            swStep.Restart();
            ExportPdContext? pdCtx = isPdGroup
                ? await PrepareExportContextAsync(c, contno, item, shopMinsCache[item.GroupCode], upcBarcodeMap, ct)
                : null;
            msPdCtx += swStep.ElapsedMilliseconds;

            // Signal UI that we're preparing this item, then yield so the render executes
            progress?.Report(new ContProcessProgress(done, totalQty, item.Itemcode, item.Itemname));
            await Task.Yield();

            swStep.Restart();
            bool isBrandsItem = !isPdGroup && !isRamadanItem;
            BrandsCtx? brandsCtx = isBrandsItem
                ? await PrepareBrandsContextAsync(c, contno, item, upcBarcodeMap, excludeItemCached, brandsGroupCache, purCnt, foreignShopsGlobal, nonProdShopsGlobal, itemAllocMap, originExcludeMap, ct)
                : null;
            msBrandsCtx += swStep.ElapsedMilliseconds;

            swStep.Restart();
            RamadanCtx? ramadanCtx = isRamadanItem
                ? BuildItemAllocCtx(itemChkCode, itemAllocMap)
                : null;
            msRamadanCtx += swStep.ElapsedMilliseconds;

            // Snapshot before while loop  -  deltas used for batched DB flushes after the loop
            var allocChkSnap    = brandsCtx != null  ? new Dictionary<string, int>(brandsCtx.AllocChk,   StringComparer.OrdinalIgnoreCase) : null;
            var qtyToSendSnap   = brandsCtx != null  ? new Dictionary<string, int>(brandsCtx.QtyToSend,  StringComparer.OrdinalIgnoreCase) : null;
            var ramadanChkSnap  = ramadanCtx != null ? new Dictionary<string, int>(ramadanCtx.AllocChk,  StringComparer.OrdinalIgnoreCase) : null;
            var pdBalanceSnap   = pdCtx?.PdBalance != null
                ? new Dictionary<string, int>(pdCtx.PdBalance, StringComparer.OrdinalIgnoreCase)
                : null;

            swStep.Restart();
            while (qty-- > 0)
            {
                ct.ThrowIfCancellationRequested();
                string result = "SHOP";

                // Tier 1  -  Online (pure in-memory; DB writes batched after while loop)
                if (!skipOnline)
                {
                    int sampRemaining   = ai.SampTotal   - ai.SampChk   - sampDone;
                    int onlineRemaining = ai.OnlineTotal - ai.OnlineChk - onlineDone;
                    if (ceiling > 0 && locQty >= ceiling) onlineRemaining = 0;

                    if (sampRemaining > 0)
                    {
                        sampDone++; result = "SAMPLES Qty"; goto ResultFound;
                    }
                    if (onlineRemaining > 0)
                    {
                        onlineDone++; result = "Photo Buffer"; goto ResultFound;
                    }
                }

                // Tier 2  -  Export
                {
                    string? exp = null;
                    if (isPdGroup && pdCtx != null)
                        exp = await AllocatePowerDropUnitAsync(c, contno, item.GroupCode, pdCtx, ct);
                    else if (isRamadanItem && ramadanCtx != null)
                        exp = AllocateRamadanUnit(ramadanCtx);
                    else if (brandsCtx != null)
                        exp = await AllocateBrandsUnitAsync(c, contno, item.GroupCode, brandsCtx, ct);

                    if (exp != null)
                    {
                        result = isPdGroup                             ? exp + "-POWERDROPS"
                               : (isRamadanItem && !isRamadanRMGroup) ? exp + "-RM"
                               : exp;
                        goto ResultFound;
                    }
                }

                // Tier 3  -  Shop fallback
                if (!skipShop)
                {
                    result = isPdGroup                             ? "SHOP-POWERDROPS"
                           : (isRamadanItem && !isRamadanRMGroup) ? "SHOP-RM"
                           : "SHOP";
                }

            ResultFound:
                results.Add(new ContProcessResultRow(
                    contno, item.Upc, item.Itemcode, item.GroupCode,
                    item.Season, item.Department, item.Division, result,
                    null, null, item.Itemname, item.BuildingCategory,
                    item.LpmDt, item.OraPoNo, item.Style));

                done++;
                progress?.Report(new ContProcessProgress(done, totalQty, item.Itemcode, item.Itemname, result, item.BuildingCategory, item.Season, item.LpmDt));

                // Yield every 50 units so the Blazor circuit can process queued StateHasChanged calls
                if (done % 50 == 0) await Task.Yield();
            }
            msWhileLoop += swStep.ElapsedMilliseconds;

            // â"€â"€ Flush Online tier (was 2N per-unit writes â†' 2 per item) â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
            swStep.Restart();
            if (sampDone > 0)
                await c.ExecuteAsync(new CommandDefinition(
                    "UPDATE ONLINE.dbo.ToysInitialAllocationDetail SET SamplesCheckedQty=ISNULL(SamplesCheckedQty,0)+@n WHERE UPPER(ContNo)=UPPER(@c) AND ItemCode=@i AND SamplesQty>0",
                    new { n = sampDone, c = contno, i = item.Itemcode }, commandTimeout: 300, cancellationToken: ct));
            if (onlineDone > 0)
                await c.ExecuteAsync(new CommandDefinition(
                    "UPDATE ONLINE.dbo.ToysInitialAllocationDetail SET ShopCheckedQty=ISNULL(ShopCheckedQty,0)+@n WHERE UPPER(ContNo)=UPPER(@c) AND ItemCode=@i AND ShopQty>0",
                    new { n = onlineDone, c = contno, i = item.Itemcode }, commandTimeout: 300, cancellationToken: ct));

            // Update allocMap so subsequent occurrences of the same itemcode use the updated checked qty
            if ((sampDone > 0 || onlineDone > 0) && allocInit.TryGetValue(item.Itemcode, out var aiCurrent))
                allocInit[item.Itemcode] = aiCurrent with
                {
                    SampChk   = aiCurrent.SampChk   + sampDone,
                    OnlineChk = aiCurrent.OnlineChk  + onlineDone
                };

            msFlushOnline += swStep.ElapsedMilliseconds;

            // â"€â"€ Flush Brands tier (was 4N per-unit writes â†' 4Ã—K where K = shops touched) â"€
            swStep.Restart();
            if (brandsCtx != null && allocChkSnap != null && qtyToSendSnap != null)
                await FlushBrandsWritesAsync(c, contno, item.GroupCode, brandsCtx, allocChkSnap, qtyToSendSnap, ct);
            msFlushBrands += swStep.ElapsedMilliseconds;

            // â"€â"€ Flush PowerDrop tier (was 2N per-unit writes â†' 2Ã—K where K = shops touched) â"€
            swStep.Restart();
            if (pdCtx != null && pdBalanceSnap != null)
                await FlushPdWritesAsync(c, contno, item.GroupCode, pdCtx, pdBalanceSnap, ct);
            msFlushPd += swStep.ElapsedMilliseconds;

            // â"€â"€ Flush Ramadan tier â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
            swStep.Restart();
            if (ramadanCtx != null && ramadanChkSnap != null)
                await FlushItemAllocWritesAsync(c, contno, item.GroupCode, ramadanCtx, ramadanChkSnap, ct);
            msFlushRamadan += swStep.ElapsedMilliseconds;
        }

        // Single MERGE from #USAStock back to the real table — one network round-trip for the whole container
        swStep.Restart();
        await c.ExecuteAsync(new CommandDefinition(
            @"MERGE usa.dbo.USAStock AS t
              USING #USAStock AS s ON t.ItemCode=s.ItemCode AND t.Shop=s.Shop
              WHEN MATCHED THEN UPDATE SET
                  t.QtyToSend = s.QtyToSend,
                  t.QtyReqd   = CASE WHEN s.QtyReqdDirty=1 THEN s.QtyReqd ELSE t.QtyReqd END
              WHEN NOT MATCHED BY TARGET AND (s.QtyToSend>0 OR s.QtyReqdDirty=1)
                  THEN INSERT VALUES(s.ItemCode,'',0,s.Shop,0,s.QtyToSend,0,s.QtyReqd);",
            commandTimeout: 300, cancellationToken: ct));
        Tick("FlushUSAStock");

        swStep.Restart();
        await ApplyBuildingSettingsAsync(c, results, ct);
        await EnrichExportResultsAsync(c, results, ct);
        Tick("ApplySettings+Enrich");

        // Bulk insert  -  one SqlBulkCopy operation instead of N individual INSERTs
        await BulkInsertPhotoCheckingResultAsync(c, results, isRobo: false, ct);
        Tick("BulkInsert_Main");

        if (!conn.IsRoboFallback)
        {
            await using var robo = new SqlConnection(conn.GetRoboConnectionString());
            await robo.OpenAsync(ct);
            await BulkInsertPhotoCheckingResultAsync(robo, results, isRobo: true, ct);
            Tick("BulkInsert_Robo");
        }

        // Mark container as finished
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE online.dbo.PhotoCheckingResultStart SET finish='Y' WHERE contno=@c",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));

        // Manual checking  -  rebuild ExportAllocation_Itemcodes
        if (manualChecking)
        {
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Online.dbo.ExportAllocation_Itemcodes WHERE contno=@c",
                new { c = contno }, commandTimeout: 300, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO Online.dbo.ExportAllocation_Itemcodes
                  SELECT REPLACE(Result,'-POWERDROPS',''), ContNo, Itemcode,
                         SUM(Qty), 0, TrnDate, 1065, 'POWERDROPS', Result
                  FROM ONLINE.dbo.PhotoCheckingResult
                  WHERE contno=@c AND result NOT IN ('SHOP','PHOTO BUFFER')
                  GROUP BY REPLACE(Result,'-POWERDROPS',''), ContNo, Itemcode, TrnDate, Result",
                new { c = contno }, commandTimeout: 300, cancellationToken: ct));
        }

        // â"€â"€ Print timing breakdown â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€
        swTotal.Stop();
        timings.Add(("GetItemExclusions[all items]", msExcl));
        timings.Add(("BuildShopMins[all items]",     msBuildShopMins));
        timings.Add(("PrepareExportCtx[PD items]",   msPdCtx));
        timings.Add(("PrepareBrandsCtx[all items]",   msBrandsCtx));
        timings.Add(("PrepareRamadanCtx[all items]",  msRamadanCtx));
        timings.Add(("WhileLoop[in-memory]",           msWhileLoop));
        timings.Add(("FlushOnlineWrites",              msFlushOnline));
        timings.Add(("FlushBrandsWrites",              msFlushBrands));
        timings.Add(("FlushPdWrites",                  msFlushPd));
        timings.Add(("FlushRamadanWrites",             msFlushRamadan));
        var sep = new string('-', 44);
        Console.Error.WriteLine($"\n[TIMING] {contno}  total={swTotal.ElapsedMilliseconds}ms  items={items.Count}  qty={totalQty}");
        Console.Error.WriteLine(sep);
        foreach (var (lbl, ms) in timings.OrderByDescending(t => t.Ms))
            Console.Error.WriteLine($"  {ms,7}ms  {lbl}");
        Console.Error.WriteLine(sep);

        var summary = results
            .GroupBy(r => new { r.Result, r.FinalResult, r.ResultType })
            .Select(g => new ContProcessSummaryRow(g.Key.Result, g.Key.FinalResult, g.Key.ResultType, g.Count()))
            .OrderByDescending(x => x.Qty)
            .ToList();

        return new ContProcessOutcome(true, null, items.Count, results.Count, summary);
    }

    // â"€â"€ loaders â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    private async Task<IEnumerable<ContProcessItem>> LoadFromLpmAsync(
        SqlConnection c, string contno, CancellationToken ct)
    {
        var rows = await c.QueryAsync<dynamic>(new CommandDefinition(
            @"SELECT l.upc, l.itemcode,
                     ISNULL(o.itemname,'')  AS itemname,
                     ISNULL(o.groupcode,'') AS groupcode,
                     ISNULL(o.season,'')    AS season,
                     ISNULL(o.vendor,'')    AS vendor,
                     SUM(l.orgqty)          AS orgqty,
                     l.LPMDt, l.ORAPONo, l.Style
              FROM usa.dbo.usaorgfile_LPM l WITH (NOLOCK)
              LEFT JOIN (
                  SELECT DISTINCT upc,itemcode,itemname,groupcode,season,vendor
                  FROM usa.dbo.USAOrgFile WITH (NOLOCK) WHERE contno=@c
              ) o ON o.itemcode = l.itemcode
              WHERE l.contno=@c
              GROUP BY l.upc,l.itemcode,o.itemname,o.groupcode,o.season,o.vendor,l.LPMDt,l.ORAPONo,l.Style",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));

        return rows.Select(r => new ContProcessItem(
            (string)(r.upc ?? ""), (string)(r.itemcode ?? ""),
            (string)(r.itemname ?? ""), (string)(r.groupcode ?? ""),
            (string)(r.season ?? ""), "", "", (string)(r.vendor ?? ""),
            (int)(r.orgqty ?? 0), null,
            r.LPMDt as DateTime?, (string?)r.ORAPONo, (string?)r.Style));
    }

    private async Task<IEnumerable<ContProcessItem>> LoadFromOrgFileAsync(
        SqlConnection c, string contno, CancellationToken ct)
    {
        var rows = await c.QueryAsync<dynamic>(new CommandDefinition(
            @"SELECT upc, itemcode, ISNULL(itemname,'') AS itemname,
                     ISNULL(groupcode,'') AS groupcode, ISNULL(season,'') AS season,
                     ISNULL(vendor,'')   AS vendor,    SUM(orgqty) AS orgqty,
                     LPMDt, ORAPONo, Style
              FROM usa.dbo.USAOrgFile WITH (NOLOCK)
              WHERE ContNo=@c
              GROUP BY upc,itemcode,itemname,groupcode,season,vendor,LPMDt,ORAPONo,Style",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));

        return rows.Select(r => new ContProcessItem(
            (string)(r.upc ?? ""), (string)(r.itemcode ?? ""),
            (string)(r.itemname ?? ""), (string)(r.groupcode ?? ""),
            (string)(r.season ?? ""), "", "", (string)(r.vendor ?? ""),
            (int)(r.orgqty ?? 0), null,
            r.LPMDt as DateTime?, (string?)r.ORAPONo, (string?)r.Style));
    }

    // â"€â"€ enrichment â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    private async Task<List<ContProcessItem>> EnrichItemsAsync(
        SqlConnection c, List<ContProcessItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return items;

        // Single batch query to HODATA for all items missing name or groupcode
        var needMaster = items
            .Where(x => string.IsNullOrEmpty(x.Itemname) || string.IsNullOrEmpty(x.GroupCode))
            .Select(x => x.Itemcode).Distinct().ToList();

        var masterLookup = new Dictionary<string, (string Name, string Gc)>(StringComparer.OrdinalIgnoreCase);
        if (needMaster.Count > 0)
        {
            foreach (var batch in needMaster.Chunk(2000))
            {
                var brows = await c.QueryAsync<dynamic>(new CommandDefinition(
                    "SELECT ItemCode, ISNULL(description,'') AS description, ISNULL(groupcode,'') AS groupcode FROM HODATA.dbo.ItemMaster WITH (NOLOCK) WHERE ItemCode IN @codes",
                    new { codes = batch }, commandTimeout: 300, cancellationToken: ct));
                foreach (var row in brows)
                    masterLookup[(string)row.ItemCode] = ((string)row.description, (string)row.groupcode);
            }
        }

        // Collect all groupcodes (existing + resolved from master)
        var allGcs = items
            .Select(x => !string.IsNullOrEmpty(x.GroupCode) ? x.GroupCode
                         : masterLookup.TryGetValue(x.Itemcode, out var m) ? m.Gc : "")
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Single batch query to USAPriority
        var priorityLookup = new Dictionary<string, (string Dept, string Div, string? Bc)>(StringComparer.OrdinalIgnoreCase);
        if (allGcs.Count > 0)
        {
            var prows = await c.QueryAsync<dynamic>(new CommandDefinition(
                "SELECT groupCode, ISNULL(Department,'') AS Department, ISNULL(DivisionY,'') AS DivisionY, BuildingCategory FROM usa.dbo.USAPriority WITH (NOLOCK) WHERE groupCode IN @codes",
                new { codes = allGcs }, commandTimeout: 300, cancellationToken: ct));
            foreach (var row in prows)
                priorityLookup[(string)row.groupCode] = ((string)row.Department, (string)row.DivisionY, (string?)row.BuildingCategory);
        }

        return items.Select(item =>
        {
            var gc   = item.GroupCode;
            var name = item.Itemname;

            if (masterLookup.TryGetValue(item.Itemcode, out var master))
            {
                if (string.IsNullOrEmpty(name)) name = master.Name;
                if (string.IsNullOrEmpty(gc))   gc   = master.Gc;
            }

            var dept = item.Department;
            var div  = item.Division;
            var bc   = item.BuildingCategory;

            if (!string.IsNullOrEmpty(gc) && priorityLookup.TryGetValue(gc, out var p))
            { dept = p.Dept; div = p.Div; bc = p.Bc; }

            return item with { Itemname = name, GroupCode = gc, Department = dept, Division = div, BuildingCategory = bc };
        }).ToList();
    }

    // â"€â"€ exclusion checks â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    private async Task<(bool excOnline, bool excShop)> GetContainerExclusionsAsync(
        SqlConnection c, string contno, CancellationToken ct)
    {
        var row = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            @"SELECT ExcludeOnline, ExcludeShop FROM ONLINE.dbo.PhotoCheckExclude WITH (NOLOCK)
              WHERE ContNo=@c AND ISNULL(ContNo,'')<>'' AND ISNULL(Itemsize,'')='' AND Active='Y'
                AND (ExcludeOnline='Y' OR ExcludeShop='Y')",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));
        if (row == null) return (false, false);
        return ((string?)row.ExcludeOnline == "Y", (string?)row.ExcludeShop == "Y");
    }

    private async Task<(bool excOnline, bool excShop)> GetItemExclusionsAsync(
        SqlConnection c, string upc, string groupCode, string season, CancellationToken ct)
    {
        bool excOnline = false, excShop = false;

        var upcRow = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            @"SELECT ExcludeOnline, ExcludeShop FROM ONLINE.dbo.PhotoCheckExclude WITH (NOLOCK)
              WHERE upc=@u AND ISNULL(upc,'')<>'' AND Active='Y' AND (ExcludeOnline='Y' OR ExcludeShop='Y')",
            new { u = upc }, commandTimeout: 300, cancellationToken: ct));
        if (upcRow != null)
        {
            if ((string?)upcRow.ExcludeOnline == "Y") excOnline = true;
            if ((string?)upcRow.ExcludeShop   == "Y") excShop   = true;
        }

        var gcRow = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            @"SELECT ExcludeOnline, ExcludeShop FROM ONLINE.dbo.PhotoCheckExclude WITH (NOLOCK)
              WHERE GroupCode=@g AND ISNULL(GroupCode,'')<>'' AND Active='Y' AND (ExcludeOnline='Y' OR ExcludeShop='Y')",
            new { g = groupCode }, commandTimeout: 300, cancellationToken: ct));
        if (gcRow != null)
        {
            if ((string?)gcRow.ExcludeOnline == "Y") excOnline = true;
            if ((string?)gcRow.ExcludeShop   == "Y") excShop   = true;
        }

        // Season stop online
        var seasonStop = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            @"SELECT 1 FROM Online.dbo.PhotoCheckExclude_Season WITH (NOLOCK)
              WHERE Season=@s AND CONVERT(date,GETDATE(),103) BETWEEN fromDt AND ToDt",
            new { s = season }, commandTimeout: 300, cancellationToken: ct));
        if (seasonStop == 1) excOnline = true;

        return (excOnline, excShop);
    }

    private record ItemExcFlags(bool Online, bool Shop);
    private record ItemExcMap(
        Dictionary<string, ItemExcFlags> ByUpc,
        Dictionary<string, ItemExcFlags> ByGc,
        HashSet<string> StoppedSeasons);

    private async Task<ItemExcMap> LoadItemExclusionsAsync(
        SqlConnection c, List<ContProcessItem> items, CancellationToken ct)
    {
        var upcs    = items.Select(x => x.Upc).Distinct().ToList();
        var gcs     = items.Select(x => x.GroupCode).Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList();
        var seasons = items.Select(x => x.Season).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();

        var byUpc   = new Dictionary<string, ItemExcFlags>(StringComparer.OrdinalIgnoreCase);
        var byGc    = new Dictionary<string, ItemExcFlags>(StringComparer.OrdinalIgnoreCase);
        var stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (upcs.Count > 0)
        {
            foreach (var batch in upcs.Chunk(2000))
            {
                var rows = await c.QueryAsync<dynamic>(new CommandDefinition(
                    @"SELECT upc, ExcludeOnline, ExcludeShop FROM ONLINE.dbo.PhotoCheckExclude WITH (NOLOCK)
                      WHERE upc IN @u AND ISNULL(upc,'')<>'' AND Active='Y' AND (ExcludeOnline='Y' OR ExcludeShop='Y')",
                    new { u = batch }, commandTimeout: 300, cancellationToken: ct));
                foreach (var r in rows)
                    byUpc[(string)r.upc] = new ItemExcFlags((string?)r.ExcludeOnline == "Y", (string?)r.ExcludeShop == "Y");
            }
        }

        if (gcs.Count > 0)
        {
            var rows = await c.QueryAsync<dynamic>(new CommandDefinition(
                @"SELECT GroupCode, ExcludeOnline, ExcludeShop FROM ONLINE.dbo.PhotoCheckExclude WITH (NOLOCK)
                  WHERE GroupCode IN @g AND ISNULL(GroupCode,'')<>'' AND Active='Y' AND (ExcludeOnline='Y' OR ExcludeShop='Y')",
                new { g = gcs }, commandTimeout: 300, cancellationToken: ct));
            foreach (var r in rows)
                byGc[(string)r.GroupCode] = new ItemExcFlags((string?)r.ExcludeOnline == "Y", (string?)r.ExcludeShop == "Y");
        }

        if (seasons.Count > 0)
        {
            var stopRows = await c.QueryAsync<string>(new CommandDefinition(
                @"SELECT Season FROM Online.dbo.PhotoCheckExclude_Season WITH (NOLOCK)
                  WHERE Season IN @s AND CONVERT(date,GETDATE(),103) BETWEEN fromDt AND ToDt",
                new { s = seasons }, commandTimeout: 300, cancellationToken: ct));
            foreach (var s in stopRows) stopped.Add(s);
        }

        return new ItemExcMap(byUpc, byGc, stopped);
    }

    private async Task<bool> IsPowerDropGroupAsync(SqlConnection c, string contno, string groupCode, CancellationToken ct) =>
        await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM USA.dbo.PowerDrops WITH (NOLOCK) WHERE Contno=@c AND GroupCode=@g",
            new { c = contno, g = groupCode }, commandTimeout: 300, cancellationToken: ct)) == 1;

    private async Task<bool> IsRamadanContItemAsync(SqlConnection c, string contno, string itemcode, CancellationToken ct) =>
        await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM Online.dbo.ExportAllocation_Itemcodes WITH (NOLOCK) WHERE Contno=@c AND Itemcode=@i",
            new { c = contno, i = itemcode }, commandTimeout: 300, cancellationToken: ct)) == 1;

    private async Task<bool> IsRamadanRMGroupAsync(SqlConnection c, string groupCode, CancellationToken ct) =>
        await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM usa.dbo.USAPriority WITH (NOLOCK) WHERE Department LIKE '%ramadan%' AND GroupCode=@g",
            new { g = groupCode }, commandTimeout: 300, cancellationToken: ct)) == 1;

    // â"€â"€ Pre-load helpers (bulk queries run once before the item loop) â"€â"€â"€â"€â"€â"€â"€â"€â"€

    private record AllocState(int SampTotal, int SampChk, int OnlineTotal, int OnlineChk);

    private async Task<Dictionary<string, int>> LoadCeilingsAsync(
        SqlConnection c, List<string> divisions, CancellationToken ct)
    {
        if (divisions.Count == 0) return new();
        var rows = await c.QueryAsync<(string Division, int Ceiling)>(new CommandDefinition(
            "SELECT division, ISNULL(SKUCeiling,0) FROM ONLINE.dbo.ContSKUMaxQtyRuleDiv WITH (NOLOCK) WHERE division IN @d",
            new { d = divisions }, commandTimeout: 300, cancellationToken: ct));
        return rows.ToDictionary(r => r.Division, r => r.Ceiling, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, int>> LoadLocStockAsync(
        SqlConnection c, List<string> upcs, CancellationToken ct)
    {
        if (upcs.Count == 0) return new();
        await c.ExecuteAsync(new CommandDefinition(
            "CREATE TABLE #LocList (ItemCode NVARCHAR(50) NOT NULL PRIMARY KEY)",
            commandTimeout: 60, cancellationToken: ct));
        var dt = new System.Data.DataTable();
        dt.Columns.Add("ItemCode", typeof(string));
        foreach (var u in upcs) dt.Rows.Add(u);
        using (var bcp = new SqlBulkCopy(c))
        {
            bcp.DestinationTableName = "#LocList";
            bcp.BulkCopyTimeout = 60;
            await bcp.WriteToServerAsync(dt, ct);
        }
        var rows = await c.QueryAsync<(string Itemcode, int Qty)>(new CommandDefinition(
            "SELECT itemcode, ISNULL(quantity,0) FROM online.dbo.LocStock WITH (NOLOCK) WHERE itemcode IN (SELECT ItemCode FROM #LocList) AND costcode='002'",
            commandTimeout: 300, cancellationToken: ct));
        return rows.ToDictionary(r => r.Itemcode, r => r.Qty, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, AllocState>> LoadAllocInitialAsync(
        SqlConnection c, string contno, List<string> itemcodes, CancellationToken ct)
    {
        if (itemcodes.Count == 0) return new();
        var result = new Dictionary<string, AllocState>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in itemcodes.Chunk(2000))
        {
            var rows = await c.QueryAsync<dynamic>(new CommandDefinition(
                @"SELECT ItemCode,
                         SUM(SamplesQty)        AS SampTotal,
                         SUM(SamplesCheckedQty) AS SampChk,
                         SUM(ShopQty)           AS OnlineTotal,
                         SUM(ShopCheckedQty)    AS OnlineChk
                  FROM ONLINE.dbo.ToysInitialAllocationDetail WITH (NOLOCK)
                  WHERE UPPER(ContNo)=UPPER(@c) AND ItemCode IN @codes
                  GROUP BY ItemCode",
                new { c = contno, codes = batch }, commandTimeout: 300, cancellationToken: ct));
            foreach (var r in rows)
                result[(string)r.ItemCode] = new AllocState((int)(r.SampTotal ?? 0), (int)(r.SampChk ?? 0),
                                                            (int)(r.OnlineTotal ?? 0), (int)(r.OnlineChk ?? 0));
        }
        return result;
    }

    private async Task<Dictionary<string, (string RealCode, string ItemType, string? Origin)>> LoadUpcBarcodesAsync(
        SqlConnection c, List<string> upcs, CancellationToken ct)
    {
        var dict = new Dictionary<string, (string, string, string?)>(StringComparer.OrdinalIgnoreCase);
        if (upcs.Count == 0) return dict;

        // Use a temp table + SqlBulkCopy to avoid Dapper expanding IN @u into thousands of parameters
        await c.ExecuteAsync(new CommandDefinition(
            "CREATE TABLE #UpcList (UPC NVARCHAR(50) NOT NULL PRIMARY KEY)",
            commandTimeout: 60, cancellationToken: ct));

        var dt = new System.Data.DataTable();
        dt.Columns.Add("UPC", typeof(string));
        foreach (var u in upcs) dt.Rows.Add(u);
        using (var bcp = new SqlBulkCopy(c))
        {
            bcp.DestinationTableName = "#UpcList";
            bcp.BulkCopyTimeout = 60;
            await bcp.WriteToServerAsync(dt, ct);
        }

        var rows = await c.QueryAsync<dynamic>(new CommandDefinition(
            @"SELECT b.upc, b.ItemCode, ISNULL(b.ItemType,'') AS ItemType, b.CountryOrigin
              FROM (
                  SELECT upc, ItemCode, ItemType, CountryOrigin,
                         ROW_NUMBER() OVER (PARTITION BY upc ORDER BY TrnDate DESC) AS rn
                  FROM usa.dbo.UPCBarCodes WITH (NOLOCK)
                  WHERE upc IN (SELECT UPC FROM #UpcList)
              ) b WHERE rn = 1",
            commandTimeout: 300, cancellationToken: ct));
        foreach (var r in rows)
            dict[(string)r.upc] = ((string)(r.ItemCode ?? ""), (string)(r.ItemType ?? ""), (string?)r.CountryOrigin);
        return dict;
    }

    // â"€â"€ Export allocation  -  PowerDrops (split into per-item prep + per-unit alloc) â"€â"€

    private record ExportPdContext(
        string RealCode,
        Dictionary<string, int> ShopMins,
        HashSet<string> Excluded,
        Dictionary<string, int> PdBalance,     // shop â†' remaining alloc balance; mutated in-memory per unit
        Dictionary<string, int> StockCurrent,  // shop â†' qty+qtyToSend; mutated in-memory per unit
        Dictionary<string, int> StockReqd,     // shop â†' qtyReqd
        Dictionary<string, int> PdQtyToSend);  // shop â†' qtyToSend snapshot for flush delta

    private async Task<ExportPdContext?> PrepareExportContextAsync(
        SqlConnection c, string contno, ContProcessItem item,
        Dictionary<string, int> shopMins,
        Dictionary<string, (string RealCode, string ItemType, string? Origin)> upcBarcodeMap,
        CancellationToken ct)
    {
        int balance = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT SUM(AllocationReqQty - ISNULL(AllocationChkQty,0)) FROM USA.dbo.PowerDrops WITH (NOLOCK) WHERE contno=@c AND GroupCode=@g",
            new { c = contno, g = item.GroupCode }, commandTimeout: 300, cancellationToken: ct)) ?? 0;
        if (balance <= 0) return null;

        // RealCode from pre-loaded map  -  no DB hit
        string realCode = item.Itemcode;
        if (upcBarcodeMap.TryGetValue(item.Upc, out var uv) && !string.IsNullOrEmpty(uv.RealCode))
            realCode = uv.RealCode;

        var excluded = await BuildExcludedShopsAsync(c, contno, realCode, item.GroupCode, item.Vendor, item.Itemname, ct);

        var origin = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP 1 CountryOrigin FROM usa.dbo.UPCBarCodes WITH (NOLOCK) WHERE Itemcode=@u AND ISNULL(CountryOrigin,'')<>'' ORDER BY TrnDate DESC",
            new { u = item.Upc }, commandTimeout: 300, cancellationToken: ct));
        if (!string.IsNullOrEmpty(origin))
        {
            var origExcl = await c.QueryAsync<string>(new CommandDefinition(
                @"SELECT Shopname FROM usa.dbo.OriginExclude WITH (NOLOCK)
                  WHERE Active='Y' AND Origin=@o
                    AND Shopname IN (SELECT DISTINCT ShopName FROM USA.dbo.PowerDrops WITH (NOLOCK) WHERE Contno=@c)",
                new { o = origin, c = contno }, commandTimeout: 300, cancellationToken: ct));
            foreach (var s in origExcl) excluded.Add(s);
        }

        // Load PD shop balances into memory
        var pdBalance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pdShops = await c.QueryAsync<dynamic>(new CommandDefinition(
            @"SELECT pd.ShopName, pd.AllocationReqQty - ISNULL(pd.AllocationChkQty,0) AS Balance
              FROM USA.dbo.PowerDrops pd WITH (NOLOCK)
              WHERE pd.ContNo=@c AND pd.GroupCode=@g AND pd.AllocationReqQty > ISNULL(pd.AllocationChkQty,0)
              ORDER BY pd.ShopName",
            new { c = contno, g = item.GroupCode }, commandTimeout: 300, cancellationToken: ct));
        foreach (var sh in pdShops)
            pdBalance[(string)sh.ShopName] = (int)(sh.Balance ?? 0);

        // Set QtyReqd in #USAStock (local temp table — no HOLDLOCK on huge real table)
        if (pdBalance.Count > 0)
        {
            var pdP = new DynamicParameters();
            pdP.Add("i", realCode);
            var sbPd = new StringBuilder("MERGE #USAStock AS t USING (VALUES ");
            int ji = 0;
            foreach (var sn in pdBalance.Keys)
            {
                if (ji > 0) sbPd.Append(',');
                int min = shopMins.TryGetValue(sn, out var m) ? m : 0;
                sbPd.Append($"(@i,@pds{ji},@pdm{ji})");
                pdP.Add($"pds{ji}", sn);
                pdP.Add($"pdm{ji}", min);
                ji++;
            }
            sbPd.Append(") AS src(ItemCode,Shop,QtyReqd) ON t.ItemCode=src.ItemCode AND t.Shop=src.Shop WHEN MATCHED THEN UPDATE SET QtyReqd=src.QtyReqd,QtyReqdDirty=1 WHEN NOT MATCHED THEN INSERT(ItemCode,Shop,QtyReqd,QtyReqdDirty) VALUES(src.ItemCode,src.Shop,src.QtyReqd,1);");
            await c.ExecuteAsync(new CommandDefinition(sbPd.ToString(), pdP, commandTimeout: 60, cancellationToken: ct));
        }

        // Load USAstock from temp table  -  no hit on real table
        var stockCurrent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stockReqd    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pdQtyToSend  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (pdBalance.Count > 0)
        {
            var stockRows = await c.QueryAsync<dynamic>(new CommandDefinition(
                @"SELECT s.Shop,
                         ISNULL(s.Quantity,0)+ISNULL(s.QtyToSend,0) AS Current,
                         ISNULL(s.QtyReqd,0)   AS Reqd,
                         ISNULL(s.QtyToSend,0) AS Qts
                  FROM #USAStock s
                  WHERE s.ItemCode=@i
                    AND s.Shop IN (
                        SELECT ShopName FROM BFLDATA.dbo.DataSettings WITH (NOLOCK)
                        WHERE ExportActive='Y' AND Production='Y' AND FCCode<>'AED' AND FCCode<>'ROB' AND MaxQtyField LIKE '%P2%')",
                new { i = realCode }, commandTimeout: 60, cancellationToken: ct));
            foreach (var r in stockRows)
            {
                string sn = (string)r.Shop;
                stockCurrent[sn] = (int)(r.Current ?? 0);
                stockReqd[sn]    = (int)(r.Reqd    ?? 0);
                pdQtyToSend[sn]  = (int)(r.Qts     ?? 0);
            }
        }

        return new ExportPdContext(realCode, shopMins, excluded, pdBalance, stockCurrent, stockReqd, pdQtyToSend);
    }

    private Task<string?> AllocatePowerDropUnitAsync(
        SqlConnection c, string contno, string groupCode, ExportPdContext ctx, CancellationToken ct)
    {
        // Fully in-memory  -  no DB reads or writes; writes are batched via FlushPdWritesAsync
        if (ctx.PdBalance.Values.Sum() <= 0) return Task.FromResult<string?>(null);

        foreach (var sn in ctx.StockCurrent.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            if (ctx.Excluded.Contains(sn)) continue;
            if (!ctx.PdBalance.TryGetValue(sn, out var shopBalance) || shopBalance <= 0) continue;

            int min     = ctx.ShopMins.TryGetValue(sn, out var m) ? m : 0;
            int current = ctx.StockCurrent.GetValueOrDefault(sn, 0);
            int reqd    = ctx.StockReqd.GetValueOrDefault(sn, 0);

            if (min > current || reqd > current)
            {
                ctx.PdBalance[sn]    = shopBalance - 1;
                ctx.StockCurrent[sn] = current + 1;
                ctx.PdQtyToSend[sn]  = ctx.PdQtyToSend.GetValueOrDefault(sn, 0) + 1;
                return Task.FromResult<string?>(sn);
            }
        }

        return Task.FromResult<string?>(null);
    }

    private async Task FlushPdWritesAsync(
        SqlConnection c, string contno, string groupCode,
        ExportPdContext ctx,
        Dictionary<string, int> pdBalanceSnap,
        CancellationToken ct)
    {
        foreach (var shop in ctx.PdBalance.Keys)
        {
            int origBalance = pdBalanceSnap.GetValueOrDefault(shop, 0);
            int newBalance  = ctx.PdBalance.GetValueOrDefault(shop, 0);
            int allocated   = origBalance - newBalance;
            if (allocated <= 0) continue;

            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE #USAStock SET QtyToSend=QtyToSend+@n WHERE ItemCode=@i AND Shop=@s",
                new { n = allocated, i = ctx.RealCode, s = shop }, commandTimeout: 60, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE USA.dbo.PowerDrops SET AllocationChkQty=ISNULL(AllocationChkQty,0)+@n WHERE UPPER(ContNo)=UPPER(@c) AND GroupCode=@g AND ShopName=@s AND AllocationChkQty<AllocationReqQty",
                new { n = allocated, c = contno, g = groupCode, s = shop }, commandTimeout: 300, cancellationToken: ct));
        }
    }

    // Builds shopName â†' minQty from USAGroupSKUMaxqty + DataSettings P2 columns
    private async Task<Dictionary<string, int>> BuildShopMinsAsync(
        SqlConnection c, string groupCode, CancellationToken ct)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var p2Shops = await c.QueryAsync<(string ShopName, string MaxQtyField)>(new CommandDefinition(
                @"SELECT ShopName, MaxQtyField FROM BFLDATA.dbo.DataSettings WITH (NOLOCK)
                  WHERE FCCode<>'AED' AND Production='Y' AND ExportActive='Y'
                    AND FCCode<>'ROB' AND MaxQtyField LIKE '%P2%'",
                commandTimeout: 300, cancellationToken: ct));

            var groupRow = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                "SELECT * FROM usa.dbo.USAGroupSKUMaxqty WITH (NOLOCK) WHERE GroupCode=@g",
                new { g = groupCode }, commandTimeout: 300, cancellationToken: ct));

            foreach (var (shopName, field) in p2Shops)
            {
                if (groupRow == null) continue;
                try
                {
                    var val = ((IDictionary<string, object>)groupRow)[field];
                    dict[shopName] = val == null ? 0 : Convert.ToInt32(val);
                }
                catch { /* column not found  -  skip */ }
            }
        }
        catch { /* table missing on this server  -  return empty */ }
        return dict;
    }

    private async Task<HashSet<string>> BuildExcludedShopsAsync(
        SqlConnection c, string contno, string realCode, string groupCode, string vendor, string itemname, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Description keyword exclusions
        var descExcl = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT a.ShopName
              FROM usa.dbo.ExcludeExport_Description a WITH (NOLOCK)
              JOIN usa.dbo.USAPriority b WITH (NOLOCK) ON a.department=b.department AND b.groupcode=@g
              JOIN USA.dbo.PowerDrops  pd WITH (NOLOCK) ON a.Shopname=pd.Shopname AND pd.Contno=@c
              WHERE a.KeyWord<>'' AND CHARINDEX(UPPER(a.KeyWord), UPPER(@n)) > 0",
            new { g = groupCode, c = contno, n = itemname }, commandTimeout: 300, cancellationToken: ct));
        foreach (var s in descExcl) set.Add(s);

        // Item/group/vendor exclusions
        var itemExcl = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT a.ShopName
              FROM usa.dbo.excludeexport a WITH (NOLOCK)
              JOIN USA.dbo.PowerDrops pd WITH (NOLOCK) ON a.ShopName=pd.Shopname AND pd.Contno=@c
              WHERE a.Active='Y' AND (
                  (a.ContNo=@c   AND a.ContNo<>'')    OR
                  (a.GroupCode=@g AND a.GroupCode<>'') OR
                  (a.itemcode=@i  AND a.itemcode<>'')  OR
                  (CHARINDEX(UPPER(a.vendor), UPPER(@v)) > 0 AND a.vendor<>'') OR
                  (CHARINDEX(UPPER(a.vendor), UPPER(@n)) > 0 AND a.vendor<>''))",
            new { c = contno, g = groupCode, i = realCode, v = vendor, n = itemname },
            commandTimeout: 300, cancellationToken: ct));
        foreach (var s in itemExcl) set.Add(s);

        return set;
    }

    // â"€â"€ Export allocation  -  Brands (split: per-item prep + per-unit alloc) â"€â"€â"€â"€â"€â"€

    // Group-level context  -  computed once per GroupCode, shared across all items in that group.
    // AllocChk and DeptCap are mutable and accumulate correctly across sequential items.
    private record BrandsGroupCtx(
        string Dept, string Div, string? FixedShop,
        Dictionary<string, int> BaseMinShops,  // group-level zeros only; copied per item before item-specific zeros
        Dictionary<string, int> AllocReq,
        Dictionary<string, int> AllocChk,      // mutable, shared across items
        Dictionary<string, int> DeptCap);      // mutable, shared across items

    private record BrandsCtx(
        string RealCode, string ItemType, string Dept, string Div,
        string? FixedShop,
        Dictionary<string, int> MinShops,
        Dictionary<string, int> AllocReq,
        Dictionary<string, int> AllocChk,      // mutated in-memory per unit
        Dictionary<string, int> DeptCap,      // mutated in-memory per unit
        Dictionary<string, int> StockQty,
        Dictionary<string, int> QtyToSend,    // mutated in-memory per unit
        Dictionary<string, int> QtyReqd,
        Dictionary<string, int> ItemAllocReq, // per-item container-specific target (from ExportAllocation_Itemcodes)
        HashSet<string> ForeignShops);

    // Item-specific Brands context (~5-7 queries). Group-level data comes from the cached BrandsGroupCtx.
    private async Task<BrandsCtx?> PrepareBrandsContextAsync(
        SqlConnection c, string contno, ContProcessItem item,
        Dictionary<string, (string RealCode, string ItemType, string? Origin)> upcBarcodeMap,
        HashSet<string> excludeItemCached,
        Dictionary<string, BrandsGroupCtx?> brandsGroupCache,
        int purCnt, HashSet<string> foreignShopsGlobal, HashSet<string> nonProdShopsGlobal,
        Dictionary<string, Dictionary<string, (int Req, int Chk)>> itemAllocMap,
        Dictionary<string, List<string>> originExcludeMap,
        CancellationToken ct)
    {
        // RealCode + ItemType from pre-loaded map (no DB hit)
        string realCode = item.Upc, itemType = "";
        if (upcBarcodeMap.TryGetValue(item.Upc, out var upcInfo))
        { realCode = string.IsNullOrEmpty(upcInfo.RealCode) ? item.Upc : upcInfo.RealCode; itemType = upcInfo.ItemType; }

        // Group-level context  -  cached per GroupCode, runs ~14 queries only once per unique GC
        var grp = await PrepareOrGetBrandsGroupCtxAsync(c, contno, item.GroupCode, itemType, brandsGroupCache, nonProdShopsGlobal, ct);
        if (grp == null) return null;

        // Purchased container  -  skip item if stp_FindExportSalesPrice_New returns a message
        if (purCnt > 0)
        {
            using var cmd = new Microsoft.Data.SqlClient.SqlCommand("BFLData.dbo.stp_FindExportSalesPrice_New", c)
                { CommandType = System.Data.CommandType.StoredProcedure, CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@ItemCode", item.Upc);
            var msgParam = cmd.Parameters.Add("@ResultMsg", System.Data.SqlDbType.VarChar, 250);
            msgParam.Direction = System.Data.ParameterDirection.Output;
            await cmd.ExecuteNonQueryAsync(ct);
            if (!string.IsNullOrWhiteSpace(msgParam.Value?.ToString())) return null;
        }

        // Copy group base min shops  -  item-specific zeros applied below
        bool excludeItem = false;
        var minShops = new Dictionary<string, int>(grp.BaseMinShops, StringComparer.OrdinalIgnoreCase);

        // Zero MinShops  -  keyword exclusions (item-name specific)
        foreach (var sn in await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT a.ShopName FROM usa.dbo.ExcludeExport_Description a WITH (NOLOCK)
              JOIN usa.dbo.USAPriority b WITH (NOLOCK) ON a.Department=b.Department AND b.GroupCode=@g
              JOIN ONLINE.dbo.ExportAllocation_Brands cc WITH (NOLOCK) ON a.ShopName=cc.ShopName AND cc.ContNo=@c
              WHERE a.KeyWord<>'' AND CHARINDEX(UPPER(a.KeyWord),UPPER(@n))>0",
            new { g = item.GroupCode, c = contno, n = item.Itemname }, commandTimeout: 300, cancellationToken: ct))) minShops[sn] = 0;

        // Zero MinShops  -  excludeexport (in-memory cache replaces per-item SELECT)
        if (!excludeItemCached.Contains(item.Upc))
        {
            var excShops = (await c.QueryAsync<string>(new CommandDefinition(
                @"SELECT DISTINCT a.ShopName FROM usa.dbo.excludeexport a WITH (NOLOCK)
                  JOIN ONLINE.dbo.ExportAllocation_Brands b WITH (NOLOCK) ON a.ShopName=b.ShopName AND b.ContNo=@c
                  WHERE a.Active='Y' AND (
                      (a.ContNo=@c AND a.ContNo<>'') OR (a.GroupCode=@g AND a.GroupCode<>'') OR
                      (a.ItemCode=@i AND a.ItemCode<>'') OR
                      (CHARINDEX(UPPER(a.Vendor),UPPER(@v))>0 AND a.Vendor<>'') OR
                      (CHARINDEX(UPPER(a.Vendor),UPPER(@n))>0 AND a.Vendor<>''))",
                new { c = contno, g = item.GroupCode, i = realCode, v = item.Vendor, n = item.Itemname }, commandTimeout: 300, cancellationToken: ct))
            ).ToList();
            if (excShops.Count > 0)
            {
                foreach (var sn in excShops)
                {
                    minShops[sn] = 0;
                    if (string.Equals(sn, "EX2KSA", StringComparison.OrdinalIgnoreCase)) excludeItem = true;
                }
            }
            else
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO usa.dbo.excludeexport_Item (ContNo, UPC, TrnDate) VALUES (@c, @u, GETDATE())",
                    new { c = contno, u = item.Upc }, commandTimeout: 300, cancellationToken: ct));
                excludeItemCached.Add(item.Upc);
            }
        }

        // Zero MinShops  -  country of origin exclusions (pre-loaded map — no per-item DB query)
        string? origin = upcBarcodeMap.TryGetValue(item.Upc, out var uo) ? uo.Origin : null;
        if (!string.IsNullOrEmpty(origin) && originExcludeMap.TryGetValue(origin, out var originShops))
            foreach (var sn in originShops) minShops[sn] = 0;

        // Per-item allocation requirements from ExportAllocation_Itemcodes (pre-loaded map — no per-item DB query)
        var itemAllocReq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (itemAllocMap.TryGetValue(realCode, out var preloadedAlloc))
            foreach (var kv in preloadedAlloc) itemAllocReq[kv.Key] = kv.Value.Req;

        // Load USAStock into memory  -  read-only, no upfront MERGE/write on the huge table
        var stockQty  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var qtyToSend = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var qtyReqd   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await c.QueryAsync<(string Shop, int Qty, int Qts)>(new CommandDefinition(
            @"SELECT b.Shop, b.Quantity, b.QtyToSend
              FROM ONLINE.dbo.ExportAllocation_Brands a WITH (NOLOCK)
              JOIN #USAStock b ON a.ShopName=b.Shop AND b.ItemCode=@i
              WHERE a.ContNo=@c AND a.GroupCode=@g",
            new { c = contno, g = item.GroupCode, i = realCode }, commandTimeout: 300, cancellationToken: ct)))
        { stockQty[r.Shop] = r.Qty; qtyToSend[r.Shop] = r.Qts; }

        // QtyReqd: max(group minShop, per-item Allocation_ReqQty).
        // Write to #USAStock for shops still needing allocation — mirrors VB INSERT/UPDATE QtyReqd.
        var reqdRows = new List<(string Shop, int Reqd)>();
        foreach (var sn in grp.AllocReq.Keys)
        {
            int minQ = Math.Max(minShops.GetValueOrDefault(sn, 0), itemAllocReq.GetValueOrDefault(sn, 0));
            if (minQ > 0) qtyReqd[sn] = Math.Max(qtyReqd.GetValueOrDefault(sn, 0), minQ);
            if (grp.AllocReq[sn] > 0
                && grp.AllocReq[sn] > grp.AllocChk.GetValueOrDefault(sn, 0)
                && qtyReqd.GetValueOrDefault(sn, 0) > 0)
                reqdRows.Add((sn, qtyReqd[sn]));
        }
        if (reqdRows.Count > 0)
        {
            var sbReqd = new StringBuilder("MERGE #USAStock AS t USING (VALUES ");
            var rp = new DynamicParameters();
            rp.Add("i", realCode);
            for (int j = 0; j < reqdRows.Count; j++)
            {
                if (j > 0) sbReqd.Append(',');
                sbReqd.Append($"(@i,@rs{j},@rq{j})");
                rp.Add($"rs{j}", reqdRows[j].Shop);
                rp.Add($"rq{j}", reqdRows[j].Reqd);
            }
            sbReqd.Append(") AS src(ItemCode,Shop,QtyReqd) ON t.ItemCode=src.ItemCode AND t.Shop=src.Shop " +
                "WHEN MATCHED THEN UPDATE SET QtyReqd=src.QtyReqd,QtyReqdDirty=1 " +
                "WHEN NOT MATCHED THEN INSERT(ItemCode,Shop,QtyReqd,QtyReqdDirty) VALUES(src.ItemCode,src.Shop,src.QtyReqd,1);");
            await c.ExecuteAsync(new CommandDefinition(sbReqd.ToString(), rp, commandTimeout: 60, cancellationToken: ct));
        }

        // FixedShop  -  nullified if this item is excluded
        string? fixedShop = excludeItem ? null : grp.FixedShop;

        return new BrandsCtx(realCode, itemType, grp.Dept, grp.Div, fixedShop,
            minShops, grp.AllocReq, grp.AllocChk, grp.DeptCap,
            stockQty, qtyToSend, qtyReqd, itemAllocReq, foreignShopsGlobal);
    }

    private async Task<Dictionary<string, Dictionary<string, (int Req, int Chk)>>> LoadItemAllocMapAsync(
        SqlConnection c, string contno, CancellationToken ct)
    {
        var result = new Dictionary<string, Dictionary<string, (int Req, int Chk)>>(StringComparer.OrdinalIgnoreCase);
        var rows = await c.QueryAsync<(string ItemCode, string Shop, int Req, int Chk)>(new CommandDefinition(
            @"SELECT ItemCode, ShopName, ISNULL(Allocation_ReqQty,0), ISNULL(Allocation_CheckedQty,0)
              FROM ONLINE.dbo.ExportAllocation_Itemcodes WITH (NOLOCK)
              WHERE ContNo=@c AND ISNULL(Allocation_ReqQty,0)>0",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));
        foreach (var (item, shop, req, chk) in rows)
        {
            if (!result.TryGetValue(item, out var d))
                result[item] = d = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            d[shop] = (req, chk);
        }
        return result;
    }

    private static RamadanCtx? BuildItemAllocCtx(
        string itemCode,
        Dictionary<string, Dictionary<string, (int Req, int Chk)>> itemAllocMap)
    {
        if (!itemAllocMap.TryGetValue(itemCode, out var shopMap)) return null;
        var allocReq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allocChk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in shopMap) { allocReq[kv.Key] = kv.Value.Req; allocChk[kv.Key] = kv.Value.Chk; }
        return new RamadanCtx(allocReq, allocChk);
    }

    private async Task<Dictionary<string, List<string>>> LoadOriginExcludeMapAsync(
        SqlConnection c, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var rows = await c.QueryAsync<(string Origin, string Shop)>(new CommandDefinition(
            "SELECT Origin, ShopName FROM usa.dbo.OriginExclude WITH (NOLOCK) WHERE Active='Y'",
            commandTimeout: 300, cancellationToken: ct));
        foreach (var (origin, shop) in rows)
        {
            if (!result.TryGetValue(origin, out var shops))
                result[origin] = shops = new List<string>();
            shops.Add(shop);
        }
        return result;
    }

    // Group-level Brands context  -  runs ~14 queries once per unique GroupCode, cached for all items.
    private async Task<BrandsGroupCtx?> PrepareOrGetBrandsGroupCtxAsync(
        SqlConnection c, string contno, string groupCode, string itemType,
        Dictionary<string, BrandsGroupCtx?> cache,
        HashSet<string> nonProdShopsGlobal,
        CancellationToken ct)
    {
        if (cache.TryGetValue(groupCode, out var cached)) return cached;

        // Quick exit  -  no balance
        int totalBal = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT SUM(AllocationReqQty - ISNULL(AllocationChkQty, 0)) FROM ONLINE.dbo.ExportAllocation_Brands WITH (NOLOCK) WHERE ContNo=@c AND GroupCode=@g",
            new { c = contno, g = groupCode }, commandTimeout: 300, cancellationToken: ct)) ?? 0;
        if (totalBal <= 0) { cache[groupCode] = null; return null; }

        // Department / Division
        var prio = await c.QueryFirstOrDefaultAsync<(string Dept, string Div)>(new CommandDefinition(
            "SELECT Department, DivisionY FROM usa.dbo.USAPriority WITH (NOLOCK) WHERE GroupCode=@g",
            new { g = groupCode }, commandTimeout: 300, cancellationToken: ct));
        if (prio == default || string.IsNullOrEmpty(prio.Dept) || string.IsNullOrEmpty(prio.Div))
        { cache[groupCode] = null; return null; }

        // AllocReq + AllocChk  -  mutable dicts shared across all items in this GroupCode
        var allocRows = await c.QueryAsync<(string Sn, int Req, int Chk)>(new CommandDefinition(
            "SELECT ShopName, AllocationReqQty, ISNULL(AllocationChkQty,0) FROM ONLINE.dbo.ExportAllocation_Brands WITH (NOLOCK) WHERE ContNo=@c AND GroupCode=@g AND AllocationReqQty>0",
            new { c = contno, g = groupCode }, commandTimeout: 300, cancellationToken: ct));
        var allocReq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allocChk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sn, req, chk) in allocRows) { allocReq[sn] = req; allocChk[sn] = chk; }
        if (allocReq.Count == 0) { cache[groupCode] = null; return null; }

        // DeptCap  -  3 queries using BFL_Division/BFL_Department scalar UDFs; run once per GC
        string balExpr = itemType == "W"
            ? "SUM(MaxQtyW-(CurrStockW+TrfQtyW))"
            : "(SUM(MaxQty+MaxQtyH)-SUM(CurrStock+CurrStockH+TrfQty+TrfQtyH))";
        var deptCap = allocReq.Keys.ToDictionary(sn => sn, _ => int.MaxValue, StringComparer.OrdinalIgnoreCase);

        foreach (var row in await c.QueryAsync<(string Sn, int Bal)>(new CommandDefinition(
            $@"SELECT a.ShopName, {balExpr} FROM BFLDATA.dbo.DeptStock a WITH (NOLOCK)
               JOIN ONLINE.dbo.ExportAllocation_Brands b WITH (NOLOCK) ON a.ShopName=b.ShopName
               WHERE b.ContNo=@c AND b.GroupCode=@g AND a.Division=USA.dbo.BFL_Division(b.GroupCode)
               GROUP BY a.ShopName", new { c = contno, g = groupCode }, commandTimeout: 300, cancellationToken: ct)))
            if (deptCap.ContainsKey(row.Sn) && row.Bal < deptCap[row.Sn]) deptCap[row.Sn] = row.Bal;

        foreach (var row in await c.QueryAsync<(string Sn, int Bal)>(new CommandDefinition(
            $@"SELECT a.ShopName, {balExpr} FROM BFLDATA.dbo.DeptStock a WITH (NOLOCK)
               JOIN ONLINE.dbo.ExportAllocation_Brands b WITH (NOLOCK) ON a.ShopName=b.ShopName
               WHERE b.ContNo=@c AND b.GroupCode=@g AND a.Department=USA.dbo.BFL_Department(b.GroupCode)
                 AND a.Department LIKE '%Production%'
               GROUP BY a.ShopName", new { c = contno, g = groupCode }, commandTimeout: 300, cancellationToken: ct)))
            if (deptCap.ContainsKey(row.Sn) && row.Bal < deptCap[row.Sn]) deptCap[row.Sn] = row.Bal;

        foreach (var row in await c.QueryAsync<(string Sn, int Bal)>(new CommandDefinition(
            $@"SELECT a.ShopName, {balExpr} FROM BFLDATA.dbo.DeptStock a WITH (NOLOCK)
               JOIN ONLINE.dbo.ExportAllocation_Brands b WITH (NOLOCK) ON a.ShopName=b.ShopName
               JOIN usa.dbo.DepartmentGrouping dg WITH (NOLOCK) ON a.Division=dg.Division AND a.Department=dg.Department
               WHERE b.ContNo=@c AND b.GroupCode=@g AND a.Division=USA.dbo.BFL_Division(b.GroupCode)
                 AND dg.GroupLevel>1
                 AND dg.GroupLevel=ISNULL((SELECT TOP 1 GroupLevel FROM usa.dbo.DepartmentGrouping WHERE Department=USA.dbo.BFL_Department(b.GroupCode)),-1)
               GROUP BY a.ShopName", new { c = contno, g = groupCode }, commandTimeout: 300, cancellationToken: ct)))
            if (deptCap.ContainsKey(row.Sn) && row.Bal < deptCap[row.Sn]) deptCap[row.Sn] = row.Bal;

        // Shops with no DeptStock match have no department capacity constraint  -  leave uncapped.

        // BaseMinShops  -  DataSettings + USAGroupSKUMaxqty (2 queries once per GC)
        var baseMinShops = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        {
            var dsShops = (await c.QueryAsync<(string Sn, string Field)>(new CommandDefinition(
                "SELECT ShopName, MaxQtyField FROM BFLDATA.dbo.DataSettings WITH (NOLOCK) WHERE FCCode<>'AED' AND Production='Y' AND ExportActive='Y' AND FCCode<>'ROB'",
                commandTimeout: 300, cancellationToken: ct))).ToList();
            var groupMaxRow = (IDictionary<string, object>?)await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                "SELECT * FROM usa.dbo.USAGroupSKUMaxqty WITH (NOLOCK) WHERE GroupCode=@g",
                new { g = groupCode }, commandTimeout: 300, cancellationToken: ct));
            foreach (var (sn, field) in dsShops)
            {
                if (!allocReq.ContainsKey(sn)) continue;
                int q = 0;
                if (groupMaxRow != null && groupMaxRow.TryGetValue(field, out var v))
                    try { q = v == null ? 0 : Convert.ToInt32(v); } catch { }
                baseMinShops[sn] = q;
            }
        }

        // Zero baseMinShops  -  stopped / inactive dept rows (2 queries once per GC)
        string stoppedExpr = itemType == "W"
            ? "(((ISNULL(CurrStockW,0)+ISNULL(TrfQtyW,0)>MaxQtyW) AND StopDel='Y') OR MaxQtyW=0)"
            : "((((ISNULL(CurrStock,0)+ISNULL(TrfQty,0)+ISNULL(CurrStockH,0)+ISNULL(TrfQtyH,0))>MaxQty+MaxQtyH) AND StopDel='Y') OR MaxQty+MaxQtyH=0)";
        foreach (var sn in await c.QueryAsync<string>(new CommandDefinition(
            $"SELECT ShopName FROM BFLDATA.dbo.DeptStock WITH (NOLOCK) WHERE Department=@d AND {stoppedExpr}",
            new { d = prio.Dept }, commandTimeout: 300, cancellationToken: ct))) baseMinShops[sn] = 0;

        string inactiveExpr = itemType == "W"
            ? "(ISNULL(MaxQtyW,0)=0 OR ISNULL(Active,'N')<>'Y')"
            : "((ISNULL(MaxQty,0)+ISNULL(MaxQtyH,0)=0) OR ISNULL(Active,'N')<>'Y')";
        foreach (var sn in await c.QueryAsync<string>(new CommandDefinition(
            $"SELECT ShopName FROM BFLDATA.dbo.DeptStock WITH (NOLOCK) WHERE Department=@d AND {inactiveExpr}",
            new { d = prio.Dept }, commandTimeout: 300, cancellationToken: ct))) baseMinShops[sn] = 0;

        // Zero baseMinShops  -  nonProd shops + inactive USAMaxQty
        foreach (var sn in nonProdShopsGlobal) baseMinShops[sn] = 0;
        foreach (var sn in await c.QueryAsync<string>(new CommandDefinition(
            "SELECT ShopName FROM usa.dbo.usamaxqty WITH (NOLOCK) WHERE GroupCode=@g AND (ISNULL(Inactive,'')='Y' OR MaxQty=0)",
            new { g = groupCode }, commandTimeout: 300, cancellationToken: ct))) baseMinShops[sn] = 0;

        // Zero baseMinShops+deptCap  -  inactive departments, multi-brand overflow
        foreach (var sn in await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT a.ShopName FROM BFLDATA.dbo.DeptStock a WITH (NOLOCK)
              JOIN ONLINE.dbo.ExportAllocation_Brands b WITH (NOLOCK) ON a.ShopName=b.ShopName
              WHERE b.ContNo=@c AND b.GroupCode=@g AND a.Department=USA.dbo.BFL_Department(b.GroupCode) AND a.Active='N'
              GROUP BY a.ShopName",
            new { c = contno, g = groupCode }, commandTimeout: 300, cancellationToken: ct)))
        { baseMinShops[sn] = 0; deptCap[sn] = 0; }

        foreach (var sn in await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT a.ShopName FROM usa.dbo.usamaxqty a WITH (NOLOCK)
              JOIN ONLINE.dbo.ExportAllocation_Brands b WITH (NOLOCK) ON a.ShopName=b.ShopName AND a.GroupCode=b.GroupCode
              JOIN usa.dbo.USAPriority p WITH (NOLOCK) ON a.GroupCode=p.GroupCode
              WHERE b.ContNo=@c AND b.GroupCode=@g
                AND (a.CurrStock>=a.MaxQty*2 OR b.AllocationChkQty>=a.MaxQty*2)",
            new { c = contno, g = groupCode }, commandTimeout: 300, cancellationToken: ct)))
        { baseMinShops[sn] = 0; deptCap[sn] = 0; }

        // Fixed shop override (once per GC  -  excludeItem adjustment is applied per-item)
        string? fixedShop = null;
        try
        {
            string? co = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT TOP 1 ShopName FROM ONLINE.dbo.ExportAllocation_Container WITH (NOLOCK) WHERE UPPER(ContNo)=UPPER(@c) AND GroupCode=@g AND ShopName<>''",
                new { c = contno, g = groupCode }, commandTimeout: 300, cancellationToken: ct));
            if (!string.IsNullOrEmpty(co)) fixedShop = co;
        }
        catch (Microsoft.Data.SqlClient.SqlException) { }

        if (fixedShop == null &&
            (string.Equals(contno, "KNB5412", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(contno, "KNB6785", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(contno, "WH3665",  StringComparison.OrdinalIgnoreCase)))
            fixedShop = "EX2KSA";

        var ctx = new BrandsGroupCtx(prio.Dept, prio.Div, fixedShop, baseMinShops, allocReq, allocChk, deptCap);
        cache[groupCode] = ctx;
        return ctx;
    }

    private Task<string?> AllocateBrandsUnitAsync(
        SqlConnection c, string contno, string groupCode, BrandsCtx ctx, CancellationToken ct)
    {
        // Fully in-memory  -  no DB reads or writes; writes are batched via FlushBrandsWritesAsync
        string? shopSend = ctx.FixedShop;

        if (string.IsNullOrEmpty(shopSend))
        {
            shopSend = ctx.AllocReq.Keys
                .Where(sn =>
                {
                    int totBal = Math.Min(
                        ctx.AllocReq[sn] - ctx.AllocChk.GetValueOrDefault(sn, 0),
                        ctx.DeptCap.GetValueOrDefault(sn, 0));
                    if (totBal <= 0) return false;
                    int minQ     = ctx.MinShops.GetValueOrDefault(sn, 0);
                    int qty      = ctx.StockQty.GetValueOrDefault(sn, 0);
                    int qts      = ctx.QtyToSend.GetValueOrDefault(sn, 0);
                    int reqd     = ctx.QtyReqd.GetValueOrDefault(sn, 0);
                    int itemReqd = ctx.ItemAllocReq.GetValueOrDefault(sn, 0);
                    if (itemReqd > 0)
                        // Container-specific target: send exactly itemReqd units regardless of existing stock.
                        // Compare only against qts (already sent from this container), not qty (existing stock).
                        return itemReqd > qts;
                    if (reqd > 0)
                    {
                        // Group minimum stock target: existing stock counts toward the requirement.
                        bool shopF = minQ > qty + qts || reqd > qty + qts;
                        return shopF && reqd >= qts;
                    }
                    return true; // no minimum  -  allow allocation while AllocReq balance remains
                })
                .OrderBy(sn => ctx.AllocReq[sn] > 0
                    ? ctx.AllocChk.GetValueOrDefault(sn, 0) * 1.0 / ctx.AllocReq[sn]
                    : 1.0)
                .FirstOrDefault();
        }

        if (string.IsNullOrEmpty(shopSend)) return Task.FromResult<string?>(null);

        string shop = shopSend.ToUpperInvariant();
        ctx.AllocChk[shop]  = ctx.AllocChk.GetValueOrDefault(shop, 0) + 1;
        ctx.QtyToSend[shop] = ctx.QtyToSend.GetValueOrDefault(shop, 0) + 1;
        if (ctx.DeptCap.ContainsKey(shop)) ctx.DeptCap[shop] = Math.Max(0, ctx.DeptCap[shop] - 1);

        return Task.FromResult<string?>(shop);
    }

    private async Task FlushBrandsWritesAsync(
        SqlConnection c, string contno, string groupCode,
        BrandsCtx ctx,
        Dictionary<string, int> allocChkSnap,
        Dictionary<string, int> qtyToSendSnap,
        CancellationToken ct)
    {
        // Compute deltas for every shop in one pass.
        var rows = ctx.AllocChk.Keys
            .Select(shop => (
                shop,
                allocDelta: ctx.AllocChk.GetValueOrDefault(shop, 0) - allocChkSnap.GetValueOrDefault(shop, 0),
                qtsDelta:   ctx.QtyToSend.GetValueOrDefault(shop, 0) - qtyToSendSnap.GetValueOrDefault(shop, 0)))
            .Where(r => r.allocDelta > 0)
            .ToList();

        if (rows.Count == 0) return;

        var p = new DynamicParameters();
        p.Add("c", contno);
        p.Add("g", groupCode);
        p.Add("i", ctx.RealCode);

        // --- ExportAllocation_Brands: one UPDATE with CASE per shop ---
        var sbBrands = new StringBuilder(
            "UPDATE ONLINE.dbo.ExportAllocation_Brands SET AllocationChkQty=AllocationChkQty+CASE ShopName ");
        for (int j = 0; j < rows.Count; j++)
        {
            sbBrands.Append($"WHEN @bs{j} THEN @ba{j} ");
            p.Add($"bs{j}", rows[j].shop);
            p.Add($"ba{j}", rows[j].allocDelta);
        }
        sbBrands.Append("ELSE 0 END WHERE ContNo=@c AND GroupCode=@g AND ShopName IN (");
        sbBrands.Append(string.Join(',', rows.Select((_, j) => $"@bs{j}")));
        sbBrands.Append(')');
        await c.ExecuteAsync(new CommandDefinition(sbBrands.ToString(), p, commandTimeout: 300, cancellationToken: ct));

        // --- #USAStock: single MERGE into temp table (flushed to real table at end of ProcessAsync) ---
        var sbStock = new StringBuilder("MERGE #USAStock AS t USING (VALUES ");
        for (int j = 0; j < rows.Count; j++)
        {
            if (j > 0) sbStock.Append(',');
            sbStock.Append($"(@i,@us{j},@uq{j})");
            p.Add($"us{j}", rows[j].shop);
            p.Add($"uq{j}", rows[j].qtsDelta);
        }
        sbStock.Append(") AS src(ItemCode,Shop,Delta) ON t.ItemCode=src.ItemCode AND t.Shop=src.Shop WHEN MATCHED THEN UPDATE SET QtyToSend=QtyToSend+src.Delta WHEN NOT MATCHED THEN INSERT(ItemCode,Shop,QtyToSend) VALUES(src.ItemCode,src.Shop,src.Delta);");
        await c.ExecuteAsync(new CommandDefinition(sbStock.ToString(), p, commandTimeout: 300, cancellationToken: ct));

        // --- ExportAllocation_Itemcodes: one UPDATE with CASE per shop ---
        var sbItemcodes = new StringBuilder(
            "UPDATE ONLINE.dbo.ExportAllocation_Itemcodes SET Allocation_CheckedQty=Allocation_CheckedQty+CASE ShopName ");
        for (int j = 0; j < rows.Count; j++)
            sbItemcodes.Append($"WHEN @bs{j} THEN @ba{j} ");
        sbItemcodes.Append("ELSE 0 END WHERE ContNo=@c AND ItemCode=@i AND ShopName IN (");
        sbItemcodes.Append(string.Join(',', rows.Select((_, j) => $"@bs{j}")));
        sbItemcodes.Append(')');
        await c.ExecuteAsync(new CommandDefinition(sbItemcodes.ToString(), p, commandTimeout: 300, cancellationToken: ct));

        // --- DeptStock: one UPDATE per unique (Div,Dept) group for foreign shops only ---
        var foreignRows = rows.Where(r => ctx.ForeignShops.Contains(r.shop)).ToList();
        if (foreignRows.Count > 0)
        {
            string trfCol = ctx.ItemType == "W" ? "TrfQtyW=TrfQtyW+@n" : "TrfQty=TrfQty+@n";
            int totalDelta = foreignRows.Sum(r => r.allocDelta);
            await c.ExecuteAsync(new CommandDefinition(
                $"UPDATE BFLDATA.dbo.DeptStock SET {trfCol} WHERE Division=@div AND Department=@dept AND ShopName IN ({string.Join(',', foreignRows.Select((_, j) => $"@fs{j}"))})",
                foreignRows.Select((r, j) => new { j, r.shop })
                    .Aggregate(new DynamicParameters(new { n = totalDelta, div = ctx.Div, dept = ctx.Dept }),
                        (dp, x) => { dp.Add($"fs{x.j}", x.shop); return dp; }),
                commandTimeout: 300, cancellationToken: ct));
        }
    }


    // â"€â"€ Export allocation  -  Ramadan â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    private record RamadanCtx(
        Dictionary<string, int> AllocReq,   // Allocation_ReqQty per shop from ExportAllocation_Itemcodes
        Dictionary<string, int> AllocChk);  // mutable; starts from Allocation_CheckedQty, incremented per unit

    private async Task<RamadanCtx?> PrepareRamadanContextAsync(
        SqlConnection c, string contno, string itemcode, CancellationToken ct)
    {
        var rows = (await c.QueryAsync<(string Shop, int Req, int Chk)>(new CommandDefinition(
            @"SELECT ShopName, ISNULL(Allocation_ReqQty,0), ISNULL(Allocation_CheckedQty,0)
              FROM ONLINE.dbo.ExportAllocation_Itemcodes WITH (NOLOCK)
              WHERE ContNo=@c AND ItemCode=@i AND ISNULL(Allocation_ReqQty,0)>0",
            new { c = contno, i = itemcode }, commandTimeout: 300, cancellationToken: ct))).ToList();

        if (rows.Count == 0) return null;

        var allocReq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allocChk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (shop, req, chk) in rows) { allocReq[shop] = req; allocChk[shop] = chk; }
        return new RamadanCtx(allocReq, allocChk);
    }

    // Fully in-memory  -  picks the shop with the most remaining requirement (lowest checked/req ratio).
    private static string? AllocateRamadanUnit(RamadanCtx ctx)
    {
        var shop = ctx.AllocReq.Keys
            .Where(sn => ctx.AllocReq[sn] - ctx.AllocChk.GetValueOrDefault(sn, 0) > 0)
            .OrderBy(sn => ctx.AllocReq[sn] > 0
                ? ctx.AllocChk.GetValueOrDefault(sn, 0) * 1.0 / ctx.AllocReq[sn]
                : 1.0)
            .FirstOrDefault();

        if (shop == null) return null;
        ctx.AllocChk[shop] = ctx.AllocChk.GetValueOrDefault(shop, 0) + 1;
        return shop;
    }

    private async Task FlushItemAllocWritesAsync(
        SqlConnection c, string contno, string groupCode,
        RamadanCtx ctx, Dictionary<string, int> allocChkSnap, CancellationToken ct)
    {
        var rows = ctx.AllocChk.Keys
            .Select(shop => (shop, delta: ctx.AllocChk.GetValueOrDefault(shop, 0) - allocChkSnap.GetValueOrDefault(shop, 0)))
            .Where(r => r.delta > 0)
            .ToList();
        if (rows.Count == 0) return;

        // Update ExportAllocation_Brands.AllocationChkQty — same condition used by the Brands path
        var sb = new StringBuilder("UPDATE ONLINE.dbo.ExportAllocation_Brands SET AllocationChkQty=AllocationChkQty+CASE ShopName ");
        var p  = new DynamicParameters();
        p.Add("c", contno);
        p.Add("g", groupCode);
        for (int j = 0; j < rows.Count; j++)
        {
            sb.Append($"WHEN @s{j} THEN @d{j} ");
            p.Add($"s{j}", rows[j].shop);
            p.Add($"d{j}", rows[j].delta);
        }
        sb.Append("ELSE 0 END WHERE ContNo=@c AND GroupCode=@g AND ShopName IN (");
        sb.Append(string.Join(',', rows.Select((_, j) => $"@s{j}")));
        sb.Append(')');
        await c.ExecuteAsync(new CommandDefinition(sb.ToString(), p, commandTimeout: 300, cancellationToken: ct));
    }

    // â"€â"€ Post-process â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    private static readonly HashSet<string> ShopTypes = new(StringComparer.OrdinalIgnoreCase)
        { "SHOP", "Photo Buffer", "SAMPLES Qty", "SHOP-POWERDROPS", "SHOP-RM" };

    private async Task ApplyBuildingSettingsAsync(
        SqlConnection c, List<ContProcessResultRow> results, CancellationToken ct)
    {
        var settings = await c.QueryAsync<(string ResultType, string SPalletType, string WPalletType)>(
            new CommandDefinition(
                "SELECT ResultType, SPalletType, WPalletType FROM bfldata.dbo.BuildingProcess_Settings WITH (NOLOCK)",
                commandTimeout: 300, cancellationToken: ct));
        var dict = settings.ToDictionary(s => s.ResultType, s => s, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            string? finalResult, resultType;

            if (dict.TryGetValue(r.Result, out var s))
            {
                var pallet = r.Season == "W" ? s.WPalletType : s.SPalletType;
                finalResult = ShopTypes.Contains(r.Result) ? pallet : r.Result;
                resultType  = pallet;
            }
            else
            {
                finalResult = r.Result;
                resultType  = null;
            }
            results[i] = r with { FinalResult = finalResult, ResultType = resultType };
        }
    }

    // â"€â"€ getMark() port â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    private static string GetMark()
    {
        var today = DateTime.Today;
        var letter = today.Month switch
        {
            1  => "Z", 2  => "Y", 3  => "K", 4  => "R",
            5  => "T", 6  => "G", 7  => "M", 8  => "P",
            9  => "D", 10 => "L", 11 => "U", 12 => "W",
            _  => ""
        };
        return string.IsNullOrEmpty(letter) ? "" : letter + (today.Year % 10);
    }

    // â"€â"€ getShopRefNo() port â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    private async Task<string> GetShopRefNoAsync(
        SqlConnection c, string shop, string shopLetter, CancellationToken ct)
    {
        // Return today's existing TrfNo if already created
        var existing = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TrfNo FROM BFLDATA.dbo.RFIDTransfer WITH (NOLOCK) WHERE ShopName=@s AND TrfDate=CAST(GETDATE() AS DATE)",
            new { s = shop }, commandTimeout: 300, cancellationToken: ct));
        if (!string.IsNullOrEmpty(existing)) return existing;

        // Get max right-3-digit sequence for this shop this year
        var maxSeq = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(CAST(RIGHT(TrfNo,3) AS INT)) FROM BFLDATA.dbo.RFIDTransfer WITH (NOLOCK) WHERE ShopName=@s AND YEAR(TrfDate)=@y",
            new { s = shop, y = DateTime.Today.Year }, commandTimeout: 300, cancellationToken: ct)) ?? 0;

        var trfNo = shopLetter + (DateTime.Today.Year % 10) + (maxSeq + 1).ToString("D3");

        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO BFLDATA.dbo.RFIDTransfer VALUES(@s, @t, CAST(GETDATE() AS DATE))",
            new { s = shop, t = trfNo }, commandTimeout: 300, cancellationToken: ct));

        return trfNo;
    }

    // â"€â"€ Enrich export results (company / shopcode / refno / mark / price / barcode) â"€â"€

    private async Task EnrichExportResultsAsync(
        SqlConnection c, List<ContProcessResultRow> results, CancellationToken ct)
    {
        var exportResults = results
            .Where(r => !ShopTypes.Contains(r.Result))
            .Select(r => r.Result.Replace("-POWERDROPS", "").Replace("-RM", ""))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (exportResults.Count == 0) return;

        var mark = GetMark();

        // Per-shop: company, shopcode, shopletter, dataname, refno
        var shopMeta = new Dictionary<string, (string Company, string ShopCode, string ShopLetter, string DataName, string RefNo)>(StringComparer.OrdinalIgnoreCase);
        foreach (var shop in exportResults)
        {
            var ds = await c.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                "SELECT barcompname, ShopCode, ShopLetter, dataname FROM bfldata.dbo.datasettings WITH (NOLOCK) WHERE shopname=@s",
                new { s = shop }, commandTimeout: 300, cancellationToken: ct));
            if (ds == null) continue;

            string company    = (string?)ds.barcompname  ?? "";
            string shopCode   = (string?)ds.ShopCode     ?? "";
            string shopLetter = (string?)ds.ShopLetter   ?? "";
            string dataName   = (string?)ds.dataname     ?? "";
            string refNo      = await GetShopRefNoAsync(c, shop, shopLetter, ct);

            shopMeta[shop] = (company, shopCode, shopLetter, dataName, refNo);
        }

        // Per-itemcode sales price (per shop's dataname database)
        // Build lookup: (bareShop, itemcode) â†' price
        var priceCache = new Dictionary<(string Shop, string Itemcode), double>(
            EqualityComparer<(string, string)>.Default);

        foreach (var (shop, (_, _, _, dataName, _)) in shopMeta)
        {
            if (string.IsNullOrEmpty(dataName)) continue;
            try
            {
                var prices = await c.QueryAsync<(string Itemcode, double Price)>(new CommandDefinition(
                    $"SELECT Itemcode, Price FROM {dataName}.dbo.RFSalesPrice WITH (NOLOCK)",
                    commandTimeout: 300, cancellationToken: ct));
                foreach (var (ic, price) in prices)
                    priceCache[(shop, ic)] = price;
            }
            catch { /* table may not exist on every DB  -  skip */ }
        }

        // Apply enrichment to each result row
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (ShopTypes.Contains(r.Result)) continue;

            var bare = r.Result.Replace("-POWERDROPS", "").Replace("-RM", "");
            if (!shopMeta.TryGetValue(bare, out var meta)) continue;

            double price = priceCache.TryGetValue((bare, r.Itemcode), out var p) ? p : 0;
            string barcode = price > 0
                ? $"{r.Itemcode}/{price:####.###}/{meta.RefNo}"
                : r.Itemcode;

            results[i] = r with
            {
                Company    = meta.Company,
                ShopCode   = meta.ShopCode,
                RefNo      = meta.RefNo,
                Mark       = mark,
                SalesPrice = price,
                Barcode    = barcode
            };
        }
    }

    // â"€â"€ BULK INSERT into PhotoCheckingResult (SqlBulkCopy  -  one round-trip) â"€â"€â"€

    private async Task BulkInsertPhotoCheckingResultAsync(
        SqlConnection c, List<ContProcessResultRow> results, bool isRobo, CancellationToken ct)
    {
        var now     = DateTime.Now;
        var dateVal = now.Date;
        var timeStr = now.ToString("HH:mm:ss");

        var dt = new System.Data.DataTable();
        dt.Columns.Add("ContNo",           typeof(string));
        dt.Columns.Add("TrnDate",          typeof(DateTime));
        dt.Columns.Add("Time1",            typeof(string));
        dt.Columns.Add("UPC",              typeof(string));
        dt.Columns.Add("Itemcode",         typeof(string));
        dt.Columns.Add("GroupCode",        typeof(string));
        dt.Columns.Add("Season",           typeof(string));
        dt.Columns.Add("Department",       typeof(string));
        dt.Columns.Add("Division",         typeof(string));
        dt.Columns.Add("Result",           typeof(string));
        dt.Columns.Add("FinalResult",      typeof(string));
        dt.Columns.Add("ResultType",       typeof(string));
        dt.Columns.Add("Qty",              typeof(int));
        dt.Columns.Add("QtyIssue",         typeof(int));
        dt.Columns.Add("OrPrice",          typeof(double));
        dt.Columns.Add("PrintFlag",        typeof(string));
        dt.Columns.Add("RfidFlag",         typeof(string));
        dt.Columns.Add("Company",          typeof(string));
        dt.Columns.Add("ShopCode",         typeof(string));
        dt.Columns.Add("Itemname",         typeof(string));
        dt.Columns.Add("Barcode",          typeof(string));
        dt.Columns.Add("SalesPrice",       typeof(double));
        dt.Columns.Add("RefNo",            typeof(string));
        dt.Columns.Add("Mark",             typeof(string));
        dt.Columns.Add("Uid",              typeof(int));
        dt.Columns.Add("RStatus",          typeof(string));
        dt.Columns.Add("Excess",           typeof(string));
        dt.Columns.Add("TcmContno",        typeof(string));
        dt.Columns.Add("BuildingCategory", typeof(string));
        dt.Columns.Add("LPMDt",            typeof(object));
        dt.Columns.Add("ORAPONo",          typeof(string));
        dt.Columns.Add("Style",            typeof(string));
        dt.Columns.Add("Remarks",          typeof(string));

        foreach (var r in results)
        {
            dt.Rows.Add(
                r.Contno, dateVal, timeStr,
                r.Upc, r.Itemcode, r.GroupCode, r.Season, r.Department, r.Division,
                r.Result, r.FinalResult ?? r.Result, r.ResultType ?? "",
                1, 0, 0.0, "", "",
                r.Company, r.ShopCode, r.Itemname ?? "",
                string.IsNullOrEmpty(r.Barcode) ? r.Itemcode : r.Barcode,
                r.SalesPrice, r.RefNo, r.Mark,
                0, "N", "N", r.Contno,
                r.BuildingCategory ?? "",
                r.LpmDt.HasValue ? (object)r.LpmDt.Value : DBNull.Value,
                r.OraPoNo ?? "",
                r.Style ?? "", "");
        }

        using var bulk = new Microsoft.Data.SqlClient.SqlBulkCopy(c)
        {
            DestinationTableName = "ONLINE.dbo.PhotoCheckingResult",
            BatchSize            = 1000,
            BulkCopyTimeout      = 300
        };

        // Map by name so column order in the DataTable doesn't have to match the table
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

        await bulk.WriteToServerAsync(dt, ct);
    }

    // â"€â"€ Push container to Robo server â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    public async Task<(bool Ok, string? Error, int RowCount)> PushContainerToRoboAsync(
        string contno, CancellationToken ct = default)
    {
        await using var main = Open();

        // Read all result rows from the main server
        var dynRows = (await main.QueryAsync<dynamic>(new CommandDefinition(
            @"SELECT ContNo, UPC, Itemcode, GroupCode, Season, Department, Division,
                     Result, FinalResult, ResultType, Itemname, BuildingCategory,
                     LPMDt, ORAPONo, Style,
                     ISNULL(Company,'')   AS Company,
                     ISNULL(ShopCode,'')  AS ShopCode,
                     ISNULL(RefNo,'')     AS RefNo,
                     ISNULL(Mark,'')      AS Mark,
                     ISNULL(Barcode,'')   AS Barcode,
                     ISNULL(SalesPrice,0) AS SalesPrice
              FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
              WHERE ContNo = @c",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct))).ToList();

        if (dynRows.Count == 0)
            return (false, $"No data found for container {contno}. Process the container first.", 0);

        static string S(object? v) => v == null || v is DBNull ? "" : v.ToString()!;
        static string? Sn(object? v) => v == null || v is DBNull ? null : v.ToString();
        static double D(object? v) => v == null || v is DBNull ? 0.0 : Convert.ToDouble(v);

        var rows = dynRows.Select(r => new ContProcessResultRow(
            S(r.ContNo), S(r.UPC), S(r.Itemcode), S(r.GroupCode), S(r.Season),
            S(r.Department), S(r.Division), S(r.Result),
            Sn(r.FinalResult), Sn(r.ResultType), Sn(r.Itemname), Sn(r.BuildingCategory),
            r.LPMDt as DateTime?, Sn(r.ORAPONo), Sn(r.Style),
            S(r.Company), S(r.ShopCode), S(r.RefNo), S(r.Mark),
            S(r.Barcode), D(r.SalesPrice)
        )).ToList();

        // Connect to dedicated Robo server
        var roboCs = new SqlConnectionStringBuilder
        {
            DataSource             = "10.23.8.251",
            UserID                 = "AIAPI",
            Password               = "T9#vQ2!mLp7@Xs4",
            TrustServerCertificate = true,
            Encrypt                = false,
            ConnectTimeout         = 30,
            ApplicationName        = "AIWMS",
        }.ConnectionString;

        await using var robo = new SqlConnection(roboCs);
        await robo.OpenAsync(ct);

        // Skip PhotoCheckingResult insert if already pushed; always run ShopinShop/DataSettings
        int existing = await robo.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT COUNT(1) FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK) WHERE ContNo=@c",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct)) ?? 0;
        if (existing == 0)
            await BulkInsertPhotoCheckingResultAsync(robo, rows, isRobo: true, ct);

        // ShopinShop  -  Season S, non-Photo Buffer
        await robo.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO BFLDATA.dbo.ShopinShop
            SELECT DISTINCT
                Result,
                SubShop = result + '-' + BuildingCategory + '-S-' + ORAPONo + '-' + CAST(LPMDt AS varchar),
                BuildingCategory, Result, 'S', GETDATE(), 'N', ResultType, LPMDt, ORAPONo
            FROM ONLINE.dbo.PhotoCheckingResult
            WHERE ContNo = @c AND Result <> 'Photo Buffer'
              AND result + '-' + BuildingCategory + '-S-' + ORAPONo + '-' + CAST(LPMDt AS varchar)
                  NOT IN (SELECT SubShop FROM BFLDATA.dbo.ShopinShop WHERE Season = 'S')",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));

        // ShopinShop  -  Season S, Photo Buffer
        await robo.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO BFLDATA.dbo.ShopinShop
            SELECT DISTINCT
                ResultType,
                SubShop = ResultType + '-S-' + ORAPONo + '-' + CAST(LPMDt AS varchar),
                '', Result, 'S', GETDATE(), 'N', ResultType, LPMDt, ORAPONo
            FROM ONLINE.dbo.PhotoCheckingResult
            WHERE ContNo = @c AND Result = 'Photo Buffer'
              AND ResultType + '-S-' + ORAPONo + '-' + CAST(LPMDt AS varchar)
                  NOT IN (SELECT SubShop FROM BFLDATA.dbo.ShopinShop WHERE Season = 'S')",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));

        // ShopinShop  -  Season W, non-Photo Buffer
        await robo.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO BFLDATA.dbo.ShopinShop
            SELECT DISTINCT
                Result,
                SubShop = result + '-' + BuildingCategory + '-W-' + ORAPONo + '-' + CAST(LPMDt AS varchar),
                BuildingCategory, Result, 'W', GETDATE(), 'N', ResultType, LPMDt, ORAPONo
            FROM ONLINE.dbo.PhotoCheckingResult
            WHERE ContNo = @c AND Result <> 'Photo Buffer'
              AND result + '-' + BuildingCategory + '-W-' + ORAPONo + '-' + CAST(LPMDt AS varchar)
                  NOT IN (SELECT SubShop FROM BFLDATA.dbo.ShopinShop WHERE Season = 'W')",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));

        // ShopinShop  -  Season W, Photo Buffer
        await robo.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO BFLDATA.dbo.ShopinShop
            SELECT DISTINCT
                ResultType,
                SubShop = ResultType + '-W-' + ORAPONo + '-' + CAST(LPMDt AS varchar),
                '', Result, 'W', GETDATE(), 'N', ResultType, LPMDt, ORAPONo
            FROM ONLINE.dbo.PhotoCheckingResult
            WHERE ContNo = @c AND Result = 'Photo Buffer'
              AND ResultType + '-W-' + ORAPONo + '-' + CAST(LPMDt AS varchar)
                  NOT IN (SELECT SubShop FROM BFLDATA.dbo.ShopinShop WHERE Season = 'W')",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));

        // DataSettings  -  add any new SubShops that don't exist yet
        await robo.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO BFLDATA.dbo.DataSettings
            SELECT SubShop, '', '', 'ROB', 1, 0, '', '', '', '', '', 'Y', 'Y', '', '', 'Y', '', 'J', 'N', '', 'Y'
            FROM BFLDATA.dbo.ShopinShop
            WHERE SubShop NOT IN (SELECT ShopName FROM BFLDATA.dbo.DataSettings)",
            commandTimeout: 300, cancellationToken: ct));

        // Open each container in IQ Hybrid, then trigger master data sync
        var roboContNos = (await robo.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT ContNo FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK) WHERE TcmContNo=@c OR ContNo=@c",
            new { c = contno }, commandTimeout: 60, cancellationToken: ct))).ToList();

        _roboHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RoboApiToken);

        foreach (var cn in roboContNos)
        {
            var body = new StringContent($"{{\"container_number\":\"{cn}\"}}", Encoding.UTF8, "application/json");
            var resp = await _roboHttp.PostAsync($"{RoboApiBase}/containers/open", body, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"Open Container API failed for {cn}: HTTP {(int)resp.StatusCode}", 0);
        }

        var syncResp = await _roboHttp.PostAsync($"{RoboApiBase}/master/sync",
            new StringContent("", Encoding.UTF8, "application/json"), ct);
        if (!syncResp.IsSuccessStatusCode)
            return (false, $"Master Sync API failed: HTTP {(int)syncResp.StatusCode}", 0);

        string? note = existing > 0
            ? $"Container {contno} already existed in Robo ({existing} row(s)) - PhotoCheckingResult skipped, ShopinShop/DataSettings updated."
            : null;
        return (true, note, rows.Count);
    }

    // â"€â"€ Per-item rows for already-processed containers â"€â"€

    public async Task<List<(string Itemcode, string Itemname, string? Bc, string Result, DateTime? LpmDt, int Qty)>>
        GetExistingItemRowsAsync(string contno, CancellationToken ct = default)
    {
        await using var c = Open();
        var rows = await c.QueryAsync(new CommandDefinition(
            @"SELECT Itemcode, Itemname, BuildingCategory, Result, LPMDt, Qty = SUM(Qty)
              FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
              WHERE ContNo = @c
              GROUP BY Itemcode, Itemname, BuildingCategory, Result, LPMDt
              ORDER BY Itemcode",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));
        return rows.Select(r => (
            Itemcode: (string)r.Itemcode,
            Itemname: (string)(r.Itemname ?? ""),
            Bc:       (string?)r.BuildingCategory,
            Result:   (string)r.Result,
            LpmDt:    (DateTime?)r.LPMDt,
            Qty:      (int)r.Qty
        )).ToList();
    }

    // â"€â"€ Outcome summary for already-processed containers â"€â"€

    public async Task<ContProcessOutcome> GetExistingOutcomeAsync(string contno, CancellationToken ct = default)
    {
        await using var c = Open();
        var summary = (await c.QueryAsync<ContProcessSummaryRow>(new CommandDefinition(
            @"SELECT Result, FinalResult, PalletType = CAST(NULL AS varchar), Qty = SUM(Qty)
              FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
              WHERE ContNo = @c
              GROUP BY Result, FinalResult",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct))).ToList();

        int totalQty = summary.Sum(r => r.Qty);
        int totalSku = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT COUNT(DISTINCT Itemcode) FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK) WHERE ContNo=@c",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct)) ?? 0;

        return new ContProcessOutcome(true, null, totalSku, totalQty, summary);
    }

    // â"€â"€ Chute location summary (read from PhotoCheckingResult after process) â"€â"€

    public async Task<List<ChuteLocationRow>> GetChuteLocationSummaryAsync(string contno, CancellationToken ct = default)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<ChuteLocationRow>(new CommandDefinition(
            @"SELECT Result,
                     LpmDt       = CONVERT(varchar, LPMDt, 103),
                     ChuteLocation = Result + '-' + ISNULL(BuildingCategory,'') + '-' + Season + '-' + CONVERT(varchar, LPMDt, 103),
                     Qty         = SUM(Qty)
              FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
              WHERE ContNo = @c AND Result <> 'Photo Buffer'
              GROUP BY Result, BuildingCategory, Season, LPMDt
              UNION ALL
              SELECT Result,
                     LpmDt       = CONVERT(varchar, LPMDt, 103),
                     ChuteLocation = Result + '-' + Season + '-' + CONVERT(varchar, LPMDt, 103),
                     Qty         = SUM(Qty)
              FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
              WHERE ContNo = @c AND Result = 'Photo Buffer'
              GROUP BY Result, ResultType, Season, LPMDt
              ORDER BY 1",
            new { c = contno }, commandTimeout: 300, cancellationToken: ct));
        return rows.ToList();
    }

    // ── Chute Location ──────────────────────────────────────────────────

    private static readonly string[] _exportShopCols = ["EX2KSA", "EX2QATAR", "EX2KUWAIT", "EX2BAHRAIN", "BFLP2MYS"];

    public List<AllocationExcelRow> ParseAllocationExcel(Stream stream)
    {
        var rows = new List<AllocationExcelRow>();
        var raw = stream.Query(useHeaderRow: true).ToList();
        foreach (IDictionary<string, object?> rawRow in raw)
        {
            var row = rawRow.ToDictionary(
                kv => kv.Key?.Trim().ToUpperInvariant() ?? "",
                kv => kv.Value,
                StringComparer.OrdinalIgnoreCase);

            var contno   = row.TryGetValue("CONTNO",   out var cn) ? cn?.ToString()?.Trim() ?? "" : "";
            var itemcode = row.TryGetValue("ITEMCODE",  out var ic) ? ic?.ToString()?.Trim() ?? "" : "";
            if (string.IsNullOrEmpty(itemcode)) continue;

            int get(string col)
            {
                if (!row.TryGetValue(col, out var v) || v == null) return 0;
                var s = v.ToString()?.Trim();
                if (string.IsNullOrEmpty(s) || s == "-") return 0;
                try { return (int)Convert.ToDecimal(s); } catch { return 0; }
            }

            rows.Add(new AllocationExcelRow(contno, itemcode,
                get("EX2KSA"), get("EX2QATAR"), get("EX2KUWAIT"), get("EX2BAHRAIN"), get("BFLP2MYS")));
        }
        return rows;
    }

    public async Task<AllocationImportResult> ImportAllocationExcelAsync(
        string contno, List<AllocationExcelRow> rows, CancellationToken ct = default)
    {
        await using var c = Open();
        var errors = new List<string>();
        int imported = 0;

        // Validate: all Excel rows must belong to the same container as entered on screen
        var mismatch = rows
            .Where(r => !string.IsNullOrEmpty(r.Contno) &&
                        !string.Equals(r.Contno, contno, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Contno)
            .Distinct()
            .ToList();
        if (mismatch.Count > 0)
            return new AllocationImportResult(rows.Count, 0,
                [$"Excel contains container(s) {string.Join(", ", mismatch)} which do not match the entered container {contno}. Please verify and re-upload."]);

        foreach (var row in rows)
        {
            var shopQtys = new (string Shop, int Qty)[]
            {
                ("EX2KSA",     row.EX2KSA),
                ("EX2QATAR",   row.EX2QATAR),
                ("EX2KUWAIT",  row.EX2KUWAIT),
                ("EX2BAHRAIN", row.EX2BAHRAIN),
                ("BFLP2MYS",   row.BFLP2MYS),
            };

            foreach (var (shop, qty) in shopQtys)
            {
                if (qty <= 0) continue;
                await c.ExecuteAsync(new CommandDefinition(
                    @"MERGE ONLINE.dbo.ExportAllocation_Itemcodes AS t
                      USING (SELECT @c AS ContNo, @i AS ItemCode, @s AS ShopName) AS src
                             ON t.ContNo=src.ContNo AND t.ItemCode=src.ItemCode AND t.ShopName=src.ShopName
                      WHEN MATCHED THEN UPDATE SET Allocation_ReqQty=@q
                      WHEN NOT MATCHED THEN INSERT(ContNo,ItemCode,ShopName,Allocation_ReqQty,Allocation_CheckedQty)
                           VALUES(@c,@i,@s,@q,0);",
                    new { c = contno, i = row.Itemcode, s = shop, q = qty },
                    commandTimeout: 30, cancellationToken: ct));
                imported++;
            }
        }
        return new AllocationImportResult(rows.Count, imported, errors);
    }

    public async Task<List<AllocationSummaryRow>> GetAllocationSummaryAsync(string contno, CancellationToken ct = default)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<AllocationSummaryRow>(new CommandDefinition(
            @"WITH DistinctItems AS (
                  SELECT DISTINCT Result, Itemcode
                  FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
                  WHERE ContNo = @c
              ),
              OrgPerResult AS (
                  SELECT di.Result,
                         Required = ISNULL(SUM(ISNULL(o.orgqty, l.orgqty)), 0)
                  FROM DistinctItems di
                  LEFT JOIN usa.dbo.USAOrgFile       o WITH (NOLOCK) ON o.contno = @c AND o.itemcode = di.Itemcode
                  LEFT JOIN usa.dbo.usaorgfile_LPM   l WITH (NOLOCK) ON l.contno = @c AND l.itemcode = di.Itemcode
                  GROUP BY di.Result
              ),
              AllocPerResult AS (
                  SELECT Result, Allocated = CAST(SUM(Qty) AS INT)
                  FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
                  WHERE ContNo = @c
                  GROUP BY Result
              ),
              PhotoBufferReqd AS (
                  SELECT Required = ISNULL(CAST(SUM(shopqty) AS INT), 0)
                  FROM ONLINE.dbo.ToysInitialAllocationDetail WITH (NOLOCK)
                  WHERE ContNo = @c
              ),
              ExportBrandsReqd AS (
                  SELECT ShopName, Required = CAST(SUM(AllocationReqQty) AS INT)
                  FROM ONLINE.dbo.ExportAllocation_Brands WITH (NOLOCK)
                  WHERE ContNo = @c
                  GROUP BY ShopName
              )
              SELECT
                  a.Result,
                  req.Required,
                  a.Allocated,
                  AllocationPct = CAST(ROUND(100.0 * a.Allocated / NULLIF(req.Required, 0), 1) AS DECIMAL(5,1))
              FROM AllocPerResult a
              LEFT JOIN OrgPerResult r      ON r.Result   = a.Result
              LEFT JOIN ExportBrandsReqd eb ON eb.ShopName = a.Result
              CROSS APPLY (SELECT Required = CAST(
                  CASE
                      WHEN eb.Required IS NOT NULL   THEN eb.Required
                      WHEN a.Result = 'Photo Buffer' THEN (SELECT Required FROM PhotoBufferReqd)
                      WHEN a.Result = 'SHOP'         THEN 0
                      ELSE ISNULL(r.Required, 0)
                  END AS INT)) req
              ORDER BY a.Result",
            new { c = contno }, commandTimeout: 60, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<BuildingReportRow>> GetBuildingReportAsync(string contno, CancellationToken ct = default)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<BuildingReportRow>(new CommandDefinition(
            @"SELECT Itemcode, Itemname, BuildingCategory AS Bc, Result, LPMDt, ORAPONo AS OraPONo,
                     Qty = CAST(SUM(Qty) AS INT)
              FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
              WHERE ContNo = @c
              GROUP BY Itemcode, Itemname, BuildingCategory, Result, LPMDt, ORAPONo
              ORDER BY Itemcode, Result",
            new { c = contno }, commandTimeout: 120, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<LpmDateRow>> GetLpmDateSummaryAsync(string contno, CancellationToken ct = default)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<LpmDateRow>(new CommandDefinition(
            @"SELECT OraPONo, LPM, LpmQty = CAST(SUM(OrgQty) AS INT)
              FROM USA.dbo.usaorgfile_LPM WITH (NOLOCK)
              WHERE Contno = @c
              GROUP BY OraPONo, LPM
              ORDER BY LPM, OraPONo",
            new { c = contno }, commandTimeout: 60, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<(bool HasData, List<ChuteLocationShopRow> Rows)> GetChuteLocationsAsync(
        string contno, CancellationToken ct = default)
    {
        var roboCs = new SqlConnectionStringBuilder
        {
            DataSource             = "10.23.8.251",
            UserID                 = "AIAPI",
            Password               = "T9#vQ2!mLp7@Xs4",
            TrustServerCertificate = true,
            Encrypt                = false,
            ConnectTimeout         = 30,
            ApplicationName        = "AIWMS",
        }.ConnectionString;

        await using var robo = new SqlConnection(roboCs);
        await robo.OpenAsync(ct);

        int pcrCount = await robo.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(1) FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK) WHERE ContNo = @c OR TcmContNo = @c",
            new { c = contno }, commandTimeout: 60, cancellationToken: ct));

        if (pcrCount == 0)
            return (false, []);

        var rows = await robo.QueryAsync<ChuteLocationShopRow>(new CommandDefinition(
            @"WITH SubShops AS (
                  SELECT DISTINCT result + '-' + BuildingCategory + '-S-' + CAST(LPMDt AS varchar) AS ShopName
                  FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
                  WHERE ContNo = @c AND Result <> 'Photo Buffer'
                  UNION
                  SELECT DISTINCT ResultType + '-S-' + CAST(LPMDt AS varchar)
                  FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
                  WHERE ContNo = @c AND Result = 'Photo Buffer'
                  UNION
                  SELECT DISTINCT result + '-' + BuildingCategory + '-W-' + CAST(LPMDt AS varchar)
                  FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
                  WHERE ContNo = @c AND Result <> 'Photo Buffer'
                  UNION
                  SELECT DISTINCT ResultType + '-W-' + CAST(LPMDt AS varchar)
                  FROM ONLINE.dbo.PhotoCheckingResult WITH (NOLOCK)
                  WHERE ContNo = @c
              )
              SELECT ds.RoboShopId, ds.ShopName,
                     Chutes = STRING_AGG(CAST(cc.ChuteId AS varchar), ', ') WITHIN GROUP (ORDER BY cc.ChuteId)
              FROM SubShops ss
              JOIN BFLDATA.dbo.DataSettings ds WITH (NOLOCK) ON ds.ShopName = ss.ShopName
              LEFT JOIN ROBOTICS.dbo.ChuteConfiguration cc WITH (NOLOCK) ON cc.ShopId = ds.RoboShopId
              GROUP BY ds.RoboShopId, ds.ShopName
              ORDER BY ds.ShopName",
            new { c = contno }, commandTimeout: 120, cancellationToken: ct));

        return (true, rows.ToList());
    }
}
