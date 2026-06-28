/* =============================================================================
   Create dbo.WMS_ContAllocationDataSync_Log on the Azure WMS DB.

   One row per Data Sync attempt. Drives the "Recent Activity" table on the
   Data Sync page AND the already-synced gate (per Q4, one sync per ContNo,
   irrespective of batch or destination — any prior row blocks future syncs).

   Run inside the Azure WMS DB. Idempotent.
   ============================================================================= */

IF OBJECT_ID('dbo.WMS_ContAllocationDataSync_Log','U') IS NULL
CREATE TABLE dbo.WMS_ContAllocationDataSync_Log (
    SyncId            INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ContNo            VARCHAR(15)   NOT NULL,
    BatchNo           INT           NULL,
    Destination       VARCHAR(50)   NOT NULL,   -- 'AzureWmsDb' / 'WmsProductionDb'
    TotalAllocatedQty INT           NULL,
    Status            VARCHAR(20)   NOT NULL,   -- 'Success' / 'Failed'
    ErrorMessage      NVARCHAR(2000) NULL,
    SyncedBy          VARCHAR(100)  NULL,
    SyncedTS          DATETIME2(0)  NOT NULL CONSTRAINT DF_CADSL_TS DEFAULT(SYSDATETIME())
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CADSL_ContNo'
               AND object_id = OBJECT_ID('dbo.WMS_ContAllocationDataSync_Log'))
    CREATE INDEX IX_CADSL_ContNo ON dbo.WMS_ContAllocationDataSync_Log (ContNo);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CADSL_RecentDesc'
               AND object_id = OBJECT_ID('dbo.WMS_ContAllocationDataSync_Log'))
    CREATE INDEX IX_CADSL_RecentDesc ON dbo.WMS_ContAllocationDataSync_Log (SyncedTS DESC);

PRINT 'Azure WMS dbo.WMS_ContAllocationDataSync_Log ready.';
