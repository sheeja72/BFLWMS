/* =============================================================================
   Performance indexes for the Container Allocation Process flow.

   Two slow spots reported in production:
     (1) "Validating: completion status" — last validation step. Hits the
         Azure WMS DB's WmsOpenBox + WmsBuildingCompletion tables.
     (2) "Saving: cleaning prior data" — when re-Processing a container, the
         service deletes prior batches' detail + blocked + header rows on
         LPMSIM. Indexes on BatchNo + the Header lookup were added in P1
         (IX_CAD_BatchNo, IX_BLK_BatchNo, IX_CAH_GenCntCont), so this script
         only confirms / repeats them idempotently.

   Run order:
     • Section 1 — run inside the Azure WMS DB (bfl-wms-sql).
     • Section 2 — run inside LPMSIM (on-prem) — same as the P1 migration,
                   safe to re-run.

   Idempotent.
   ============================================================================= */

/* ----------------------------------------------------------------------------
   Section 1 — Azure WMS DB
   ---------------------------------------------------------------------------- */

-- WmsOpenBox: lookup by Contno (validation step 4).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WmsOpenBox_Contno'
               AND object_id = OBJECT_ID('dbo.WmsOpenBox'))
    CREATE INDEX IX_WmsOpenBox_Contno ON dbo.WmsOpenBox (Contno);

-- WmsBuildingCompletion: lookup by (Country, ContNo) (validation step 5,
-- AND also the PrevAllocatedQty seed in ProcessAllocationAsync).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WmsBuildingCompletion_Country_ContNo'
               AND object_id = OBJECT_ID('dbo.WmsBuildingCompletion'))
    CREATE INDEX IX_WmsBuildingCompletion_Country_ContNo
        ON dbo.WmsBuildingCompletion (Country, ContNo);

PRINT 'Section 1 (Azure WMS) — WmsOpenBox + WmsBuildingCompletion indexes ready.';

/* ----------------------------------------------------------------------------
   Section 2 — LPMSIM (on-prem)
   These were added by P1's install_wms_cont_allocation_header_lpmsim.sql,
   repeated here so this single file is enough to re-run all process-perf
   indexes if something drifted.
   ---------------------------------------------------------------------------- */

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CAH_GenCntCont'
               AND object_id = OBJECT_ID('dbo.WMS_Cont_Allocation_Header'))
    CREATE INDEX IX_CAH_GenCntCont
        ON dbo.WMS_Cont_Allocation_Header (GenCountry, ContNo);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CAD_BatchNo'
               AND object_id = OBJECT_ID('dbo.WMS_ContAllocationData'))
    CREATE INDEX IX_CAD_BatchNo ON dbo.WMS_ContAllocationData (BatchNo);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BLK_BatchNo'
               AND object_id = OBJECT_ID('dbo.WMS_ContAllocationBlocked'))
    CREATE INDEX IX_BLK_BatchNo ON dbo.WMS_ContAllocationBlocked (BatchNo);

PRINT 'Section 2 (LPMSIM) — Header/Data/Blocked BatchNo indexes ready.';
