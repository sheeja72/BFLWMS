/* =============================================================================
   Create dbo.WmsManualAllocation on the Azure WMS DB.

   Persists the enriched upload from the LPM "Manual Allocation Upload" page.
   Idempotent per (Country, ContNo): the service DELETEs prior rows for that
   pair before INSERTing the fresh set, so re-uploading corrects rather than
   duplicates.

   Idempotent DDL. Run inside the Azure WMS DB.
   ============================================================================= */

IF OBJECT_ID('dbo.WmsManualAllocation', 'U') IS NULL
CREATE TABLE dbo.WmsManualAllocation (
    IdNo           INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Country        NVARCHAR(20)  NOT NULL,
    StoreID        VARCHAR(25)   NOT NULL,
    ContNo         VARCHAR(15)   NOT NULL,
    Itemcode       VARCHAR(15)   NOT NULL,
    AllocationQty  INT           NOT NULL,
    Division       VARCHAR(150)  NULL,
    POQty          INT           NULL,
    eComSOH        INT           NULL,
    SkuMax         INT           NULL,
    SkuBalance     INT           NULL,   -- SkuMax - eComSOH
    QualifiedQty   INT           NULL,   -- MIN(SkuBalance, AllocationQty)
    DivEOM         INT           NULL,
    DivSOH         INT           NULL,
    EomBalance     INT           NULL,   -- DivEOM - DivSOH
    UploadedBy     NVARCHAR(100) NOT NULL,
    UploadedTS     DATETIME2(0)  NOT NULL
        CONSTRAINT DF_WMA_UploadedTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME()))
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_WMA_CountryContno'
                 AND object_id = OBJECT_ID('dbo.WmsManualAllocation'))
    CREATE INDEX IX_WMA_CountryContno
        ON dbo.WmsManualAllocation (Country, ContNo);
GO

PRINT 'Azure WMS dbo.WmsManualAllocation ready.';
