/* =============================================================================
   Create dbo.WMS_ContAllocationData on the Azure WMS DB.

   Mirrors the LPMSIM.dbo.WMS_ContAllocationData column set so the new
   Data Sync feature can SqlBulkCopy detail rows from LPMSIM into here
   for the "Azure WMS DB" destination.

   Run inside the Azure WMS DB (bfl-wms-sql / database WMS). Idempotent.
   ============================================================================= */

IF OBJECT_ID('dbo.WMS_ContAllocationData','U') IS NULL
CREATE TABLE dbo.WMS_ContAllocationData (
    IdNo             INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    BatchNo          INT          NULL,
    ContNo           VARCHAR(15)  NULL,
    Country          VARCHAR(20)  NULL,
    TrnDate          DATE         NULL,
    Time1            TIME(0)      NULL,
    UPC              VARCHAR(25)  NULL,
    Itemcode         VARCHAR(15)  NULL,
    Barcode          VARCHAR(30)  NULL,
    GroupCode        VARCHAR(5)   NULL,
    Qty              INT          NULL,
    SkuMax           INT          NULL,
    AllocatedQty     INT          NULL,
    PrevAllocatedQty INT          NULL,
    QtyIssue         INT          NULL,
    StoreID          VARCHAR(15)  NULL,
    TcmContno        VARCHAR(15)  NULL,
    Itemname         VARCHAR(150) NULL,
    BuildingCategory VARCHAR(250) NULL,
    LPMDt            DATE         NULL,
    LPMBoxNO         VARCHAR(100) NULL,
    ORAPONo          VARCHAR(50)  NULL,
    Division         VARCHAR(150) NULL,
    Brand            VARCHAR(150) NULL,
    DivCode          INT          NULL,
    Department       VARCHAR(150) NULL,
    Season           VARCHAR(3)   NULL,
    Style            VARCHAR(50)  NULL,
    [Size]           VARCHAR(20)  NULL,
    SalesPrice       DECIMAL(18,4) NULL,
    ResultType       VARCHAR(3)   NULL,
    FinalResult      VARCHAR(100) NULL,
    Result           VARCHAR(100) NULL,
    Remarks          VARCHAR(50)  NULL,
    OTS              FLOAT        NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AzureCAD_ContNo'
               AND object_id = OBJECT_ID('dbo.WMS_ContAllocationData'))
    CREATE INDEX IX_AzureCAD_ContNo ON dbo.WMS_ContAllocationData (ContNo);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AzureCAD_BatchNo'
               AND object_id = OBJECT_ID('dbo.WMS_ContAllocationData'))
    CREATE INDEX IX_AzureCAD_BatchNo ON dbo.WMS_ContAllocationData (BatchNo);

PRINT 'Azure WMS dbo.WMS_ContAllocationData ready.';
