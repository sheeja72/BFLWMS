/* =============================================================================
   WMS_ContAllocationDraftHeader + WMS_ContAllocationDraftDetail
   Replaces the JSON-blob approach (Azure WmsAllocationDraft).

   Run inside the LPMSIM database (same DB that holds WMS_ContAllocationData).
   Idempotent.
   ============================================================================= */

/* ---------- Draft Header — one row per (Country, ContNo) ---------- */
IF OBJECT_ID('dbo.WMS_ContAllocationDraftHeader','U') IS NULL
CREATE TABLE dbo.WMS_ContAllocationDraftHeader (
    Country     VARCHAR(20)   NOT NULL,
    ContNo      VARCHAR(15)   NOT NULL,
    RowCount1   INT           NOT NULL,
    TotalQty    INT           NOT NULL,
    SavedTS     DATETIME2(0)  NOT NULL CONSTRAINT DF_WMS_ContAllocDraftHdr_TS DEFAULT(SYSDATETIME()),
    SavedBy     VARCHAR(100)  NOT NULL,
    CONSTRAINT PK_WMS_ContAllocationDraftHeader PRIMARY KEY (Country, ContNo)
);

/* ---------- Draft Detail — same column shape as WMS_ContAllocationData ---------- */
IF OBJECT_ID('dbo.WMS_ContAllocationDraftDetail','U') IS NULL
CREATE TABLE dbo.WMS_ContAllocationDraftDetail (
    IdNo              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Country           VARCHAR(20)   NOT NULL,
    ContNo            VARCHAR(15)   NOT NULL,
    TrnDate           DATE          NULL,
    Time1             TIME(0)       NULL,
    UPC               VARCHAR(25)   NULL,
    Itemcode          VARCHAR(15)   NULL,
    GroupCode         VARCHAR(5)    NULL,
    Season            VARCHAR(3)    NULL,
    Department        VARCHAR(150)  NULL,
    Division          VARCHAR(150)  NULL,
    Result            VARCHAR(100)  NULL,
    FinalResult       VARCHAR(100)  NULL,
    ResultType        VARCHAR(3)    NULL,
    Qty               INT           NULL,
    QtyIssue          INT           NULL,
    OrPrice           FLOAT         NULL,
    PrintFlag         VARCHAR(1)    NULL,
    RfidFlag          VARCHAR(1)    NULL,
    Company           VARCHAR(30)   NULL,
    ShopCode          VARCHAR(5)    NULL,
    Itemname          VARCHAR(150)  NULL,
    Barcode           VARCHAR(30)   NULL,
    SalesPrice        VARCHAR(30)   NULL,
    RefNo             VARCHAR(15)   NULL,
    Mark              VARCHAR(5)    NULL,
    Uid               VARCHAR(5)    NULL,
    RStatus           VARCHAR(1)    NULL,
    RDateTime         SMALLDATETIME NULL,
    PStatus           VARCHAR(1)    NULL,
    PDateTime         SMALLDATETIME NULL,
    Excess            VARCHAR(1)    NULL,
    TcmContno         VARCHAR(15)   NULL,
    BuildingCategory  VARCHAR(250)  NULL,
    LPMDt             DATE          NULL,
    LPMBoxNO          VARCHAR(100)  NULL,
    ORAPONo           VARCHAR(50)   NULL,
    Style             VARCHAR(50)   NULL,
    Remarks           VARCHAR(50)   NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WMS_ContAllocDraftDet_CC' AND object_id=OBJECT_ID('dbo.WMS_ContAllocationDraftDetail'))
    CREATE INDEX IX_WMS_ContAllocDraftDet_CC ON dbo.WMS_ContAllocationDraftDetail (Country, ContNo);

PRINT 'WMS_ContAllocationDraftHeader + Detail ready.';
