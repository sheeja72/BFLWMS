/* =============================================================================
   WMS_Cont_Allocation Header + WMS_ContAllocationData / Blocked schema refresh.

   Run inside LPMSIM (on-prem). Idempotent — re-running is safe.

   This script:
     1. Wipes all existing allocation rows (per user instruction: start fresh).
     2. Creates the Header table keyed by BatchNo IDENTITY.
     3. Adds BatchNo, Size, AllocatedQty, PrevAllocatedQty to
        WMS_ContAllocationData (Country column already exists per user).
     4. Adds BatchNo to WMS_ContAllocationBlocked.

   The page-level RunOption stays in the C# input for now; the service writes
   it into Header.RunOption from Phase 1 onward. The legacy RunOption column
   on WMS_ContAllocationData / Blocked is left in place (unused) — can be
   dropped in a later phase once nothing reads from it.
   ============================================================================= */

-- 1) Wipe existing allocation data so BatchNo back-fill isn't required.
DELETE FROM dbo.WMS_ContAllocationData;
DELETE FROM dbo.WMS_ContAllocationBlocked;
DELETE FROM dbo.WMS_ContAllocationDraftDetail;
DELETE FROM dbo.WMS_ContAllocationDraftHeader;

-- 2) Header table — one row per Process run.
IF OBJECT_ID('dbo.WMS_Cont_Allocation_Header','U') IS NULL
CREATE TABLE dbo.WMS_Cont_Allocation_Header (
    BatchNo       INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ContNo        VARCHAR(15)   NOT NULL,
    Warehouse     VARCHAR(20)   NULL,
    GenCountry    VARCHAR(20)   NOT NULL,   -- SIM Generation Country (single)
    Country       VARCHAR(200)  NOT NULL,   -- Allocation destinations, comma-separated
    RunOption     VARCHAR(20)   NOT NULL,   -- 'FillSKUMax' / 'RoundRobin'
    RowCount1     INT           NULL,
    TotalQty      INT           NULL,
    ProcessedTS   DATETIME2(0)  NOT NULL CONSTRAINT DF_CAH_PT DEFAULT(SYSDATETIME()),
    ProcessedBy   VARCHAR(100)  NULL,
    ApprovedDt    DATETIME2(0)  NULL,
    ApprovedBy    VARCHAR(100)  NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CAH_GenCntCont'
               AND object_id=OBJECT_ID('dbo.WMS_Cont_Allocation_Header'))
    CREATE INDEX IX_CAH_GenCntCont
      ON dbo.WMS_Cont_Allocation_Header(GenCountry, ContNo);

-- 3) WMS_ContAllocationData additions (Country column already exists per Q-B).
IF COL_LENGTH('dbo.WMS_ContAllocationData','BatchNo')          IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD BatchNo          INT          NULL;
IF COL_LENGTH('dbo.WMS_ContAllocationData','Size')             IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD [Size]          VARCHAR(20)  NULL;
IF COL_LENGTH('dbo.WMS_ContAllocationData','AllocatedQty')     IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD AllocatedQty     INT          NULL;
IF COL_LENGTH('dbo.WMS_ContAllocationData','PrevAllocatedQty') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD PrevAllocatedQty INT          NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CAD_BatchNo'
               AND object_id=OBJECT_ID('dbo.WMS_ContAllocationData'))
    CREATE INDEX IX_CAD_BatchNo ON dbo.WMS_ContAllocationData(BatchNo);

-- 4) WMS_ContAllocationBlocked: add BatchNo for traceability.
IF COL_LENGTH('dbo.WMS_ContAllocationBlocked','BatchNo') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationBlocked ADD BatchNo INT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_BLK_BatchNo'
               AND object_id=OBJECT_ID('dbo.WMS_ContAllocationBlocked'))
    CREATE INDEX IX_BLK_BatchNo ON dbo.WMS_ContAllocationBlocked(BatchNo);

PRINT 'WMS_Cont_Allocation_Header + WMS_ContAllocationData/Blocked refresh ready.';
