/* =============================================================================
   WMS_ContAllocationData — created in LPMSIM database (on-prem).
   Written to by the Container Allocation Process (new module).

   Run inside the LPMSIM database (NOT the Azure WMS DB).
   Idempotent.
   ============================================================================= */

IF OBJECT_ID('dbo.WMS_ContAllocationData','U') IS NULL
CREATE TABLE dbo.WMS_ContAllocationData (
    IdNo              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ContNo            VARCHAR(15)       NOT NULL,
    TrnDate           DATE              NULL,
    Time1             TIME(0)           NULL,
    UPC               VARCHAR(25)       NULL,
    Itemcode          VARCHAR(15)       NULL,
    GroupCode         VARCHAR(5)        NULL,
    Season            VARCHAR(3)        NULL,
    Department        VARCHAR(150)      NULL,
    Division          VARCHAR(150)      NULL,
    Result            VARCHAR(100)      NULL,
    FinalResult       VARCHAR(100)      NULL,
    ResultType        VARCHAR(3)        NULL,
    Qty               INT               NULL,
    QtyIssue          INT               NULL,
    OrPrice           FLOAT             NULL,
    PrintFlag         VARCHAR(1)        NULL,
    RfidFlag          VARCHAR(1)        NULL,
    Company           VARCHAR(30)       NULL,
    StoreID           VARCHAR(20)       NULL,
    Itemname          VARCHAR(150)      NULL,
    Barcode           VARCHAR(30)       NULL,
    SalesPrice        VARCHAR(30)       NULL,
    RefNo             VARCHAR(15)       NULL,
    Mark              VARCHAR(5)        NULL,
    Uid               VARCHAR(5)        NULL,
    RStatus           VARCHAR(1)        NULL,
    RDateTime         SMALLDATETIME     NULL,
    PStatus           VARCHAR(1)        NULL,
    PDateTime         SMALLDATETIME     NULL,
    Excess            VARCHAR(1)        NULL,
    TcmContno         VARCHAR(15)       NULL,
    BuildingCategory  VARCHAR(250)      NULL,
    LPMDt             DATE              NULL,
    LPMBoxNO          VARCHAR(100)      NULL,
    ORAPONo           VARCHAR(50)       NULL,
    Style             VARCHAR(50)       NULL,
    Remarks           VARCHAR(50)       NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WMS_ContAllocationData_ContNo' AND object_id=OBJECT_ID('dbo.WMS_ContAllocationData'))
    CREATE INDEX IX_WMS_ContAllocationData_ContNo ON dbo.WMS_ContAllocationData (ContNo);

PRINT 'WMS_ContAllocationData created in LPMSIM.';
