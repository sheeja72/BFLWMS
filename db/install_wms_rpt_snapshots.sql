/* =============================================================================
   Missing / Excess from Production — snapshot tables + country config + job log.
   Run inside the Azure SQL WMS database (bfl-wms-sql).
   Idempotent.

   Tables created:
     dbo.WmsRptCountryConfig                  (which countries the nightly job processes)
     dbo.WmsRptJobRun                         (one row per backfill / daily / on-demand run)
     dbo.WmsRptMissingExcess_BoxSummary       (per Country × BoxNo × ClosedDt)
     dbo.WmsRptMissingExcess_BoxDetail        (per Country × Type × BoxNo × ItemCode)
     dbo.WmsRptMissingExcess_ItemSummary      (per Country × ClosedDt × ItemCode)

   After running, seed UAE to start:
     INSERT INTO dbo.WmsRptCountryConfig (Country, IsActive, UpdatedBy)
     VALUES ('UAE', 1, SYSTEM_USER);
   ============================================================================= */

IF OBJECT_ID('dbo.WmsRptCountryConfig','U') IS NULL
CREATE TABLE dbo.WmsRptCountryConfig (
    Country     NVARCHAR(20)  NOT NULL PRIMARY KEY,
    IsActive    BIT           NOT NULL CONSTRAINT DF_WmsRptCountryConfig_IsActive DEFAULT(0),
    UpdatedTS   DATETIME2(0)  NOT NULL CONSTRAINT DF_WmsRptCountryConfig_TS DEFAULT(SYSDATETIME()),
    UpdatedBy   NVARCHAR(100) NOT NULL CONSTRAINT DF_WmsRptCountryConfig_By DEFAULT(SUSER_NAME())
);

IF OBJECT_ID('dbo.WmsRptJobRun','U') IS NULL
CREATE TABLE dbo.WmsRptJobRun (
    RunId          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    JobName        NVARCHAR(100) NOT NULL,                 -- e.g. 'MissingExcessSnapshot'
    Country        NVARCHAR(20)  NULL,
    Mode           NVARCHAR(20)  NOT NULL,                 -- 'Backfill' / 'Daily' / 'OnDemand'
    StartTS        DATETIME2(0)  NOT NULL,
    EndTS          DATETIME2(0)  NULL,
    Status         NVARCHAR(20)  NOT NULL,                 -- 'Running' / 'Success' / 'Failed'
    RowsProcessed  INT           NULL,
    DatesProcessed INT           NULL,
    ErrorMessage   NVARCHAR(MAX) NULL,
    TriggeredBy    NVARCHAR(100) NULL                      -- 'Timer' or username for on-demand
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsRptJobRun_Name_TS' AND object_id=OBJECT_ID('dbo.WmsRptJobRun'))
    CREATE INDEX IX_WmsRptJobRun_Name_TS ON dbo.WmsRptJobRun (JobName, StartTS DESC);

-- IDENTITY PK + helper index: a box can be closed multiple times in a day
-- (different ClosedBy users), so (Country, BoxNo, ClosedDt) isn't unique.
IF OBJECT_ID('dbo.WmsRptMissingExcess_BoxSummary','U') IS NULL
CREATE TABLE dbo.WmsRptMissingExcess_BoxSummary (
    Id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Country     NVARCHAR(20)  NOT NULL,
    BoxNo       NVARCHAR(50)  NOT NULL,
    ClosedDt    DATE          NOT NULL,
    ClosedBy    NVARCHAR(100) NULL,
    MissQty     INT           NOT NULL,
    ExcessQty   INT           NOT NULL,
    SnapshotTS  DATETIME2(0)  NOT NULL CONSTRAINT DF_WmsRpt_BoxSummary_TS DEFAULT(SYSDATETIME())
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsRpt_BoxSummary_CtryDt' AND object_id=OBJECT_ID('dbo.WmsRptMissingExcess_BoxSummary'))
    CREATE INDEX IX_WmsRpt_BoxSummary_CtryDt ON dbo.WmsRptMissingExcess_BoxSummary (Country, ClosedDt);

IF OBJECT_ID('dbo.WmsRptMissingExcess_BoxDetail','U') IS NULL
CREATE TABLE dbo.WmsRptMissingExcess_BoxDetail (
    Id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Country     NVARCHAR(20)  NOT NULL,
    [Type]      NVARCHAR(10)  NOT NULL,    -- 'Missing' / 'Excess'
    ClosedDt    DATE          NOT NULL,
    BoxNo       NVARCHAR(50)  NOT NULL,
    PreparedBy  NVARCHAR(100) NULL,
    ItemCode    NVARCHAR(50)  NOT NULL,
    Qty         INT           NOT NULL,
    QtyIssued   INT           NOT NULL,
    Diff        INT           NOT NULL,
    SnapshotTS  DATETIME2(0)  NOT NULL CONSTRAINT DF_WmsRpt_BoxDetail_TS DEFAULT(SYSDATETIME())
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsRpt_BoxDetail_CtryDt' AND object_id=OBJECT_ID('dbo.WmsRptMissingExcess_BoxDetail'))
    CREATE INDEX IX_WmsRpt_BoxDetail_CtryDt ON dbo.WmsRptMissingExcess_BoxDetail (Country, ClosedDt);

IF OBJECT_ID('dbo.WmsRptMissingExcess_ItemSummary','U') IS NULL
CREATE TABLE dbo.WmsRptMissingExcess_ItemSummary (
    Country     NVARCHAR(20)  NOT NULL,
    ClosedDt    DATE          NOT NULL,
    ItemCode    NVARCHAR(50)  NOT NULL,
    ItemName    NVARCHAR(200) NULL,
    Division    NVARCHAR(150) NULL,
    Department  NVARCHAR(150) NULL,
    MissingQty  INT           NOT NULL,
    ExcessQty   INT           NOT NULL,
    HOStock     INT           NOT NULL,
    SnapshotTS  DATETIME2(0)  NOT NULL CONSTRAINT DF_WmsRpt_ItemSummary_TS DEFAULT(SYSDATETIME()),
    CONSTRAINT PK_WmsRpt_ItemSummary PRIMARY KEY (Country, ClosedDt, ItemCode)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsRpt_ItemSummary_Ctry' AND object_id=OBJECT_ID('dbo.WmsRptMissingExcess_ItemSummary'))
    CREATE INDEX IX_WmsRpt_ItemSummary_Ctry ON dbo.WmsRptMissingExcess_ItemSummary (Country);

PRINT 'WmsRpt snapshot + config + job-run tables ready. Seed UAE in WmsRptCountryConfig when ready.';
