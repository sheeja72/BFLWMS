/* =============================================================================
   WMS_ContAllocationBlocked + OTS column on WMS_ContAllocationData
   Run inside LPMSIM (on-prem). Idempotent.
   ============================================================================= */

-- OTS column on the existing final table.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE name = 'OTS'
       AND object_id = OBJECT_ID('dbo.WMS_ContAllocationData'))
BEGIN
    ALTER TABLE dbo.WMS_ContAllocationData ADD OTS FLOAT NULL;
END

-- New per-container blocked-items log.
IF OBJECT_ID('dbo.WMS_ContAllocationBlocked','U') IS NULL
CREATE TABLE dbo.WMS_ContAllocationBlocked (
    IdNo        INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Country     VARCHAR(20)  NOT NULL,
    ContNo      VARCHAR(15)  NOT NULL,
    RunOption   VARCHAR(20)  NULL,
    ItemCode    VARCHAR(15)  NOT NULL,
    ItemName    VARCHAR(150) NULL,
    StoreID     VARCHAR(20)  NOT NULL,
    StoreName   VARCHAR(150) NULL,
    DivCode     INT          NULL,
    Division    VARCHAR(150) NULL,
    Department  VARCHAR(150) NULL,
    PoQty       INT          NULL,
    BlockReason VARCHAR(50)  NOT NULL,
    CreatedTS   DATETIME2(0) NOT NULL CONSTRAINT DF_BlkTS DEFAULT(SYSDATETIME()),
    CreatedBy   VARCHAR(100) NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE name='IX_WMS_ContAllocBlocked_CCR'
                  AND object_id=OBJECT_ID('dbo.WMS_ContAllocationBlocked'))
    CREATE INDEX IX_WMS_ContAllocBlocked_CCR
        ON dbo.WMS_ContAllocationBlocked (Country, ContNo, RunOption);

PRINT 'WMS_ContAllocationData.OTS + WMS_ContAllocationBlocked ready.';
