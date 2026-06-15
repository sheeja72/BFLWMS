/* =============================================================================
   WMS Azure SQL — Phase 2c schema additions
   Adds the 8 tables that BuildingService will switch to in Phase 2d, moving
   the Manual Building hot path off on-prem (lpm/bfldata/usa) and into Azure.

   Run inside the WMS database on bfl-wms-sql.database.windows.net.
   Idempotent: rerun-safe (uses IF NOT EXISTS / OBJECT_ID).

   Multi-tenancy: every operational table gets a Country column. Composite PKs
   include Country so UAE 'ABC' and KSA 'ABC' can coexist.

   NOTE on column types: types/lengths below are best-effort matches against
   the on-prem schemas. Review the on-prem CREATE TABLE before running in
   production — adjust NVARCHAR vs VARCHAR, lengths, and precision as needed.
   ============================================================================= */

/* ============================================================================
   1. WmsPCR — denormalised PCR (replaces lpm.dbo.PhotoCheckingResultLPM)
   Extra columns added per Phase 2b decisions: ItemName, Size, Color, Brand,
   Season, Gender, Hscode, ManifestQty (so USAOrgFile + UPCbarcodes can be
   dropped entirely).
   ============================================================================ */
IF OBJECT_ID('dbo.WmsPCR','U') IS NULL
CREATE TABLE dbo.WmsPCR (
    Country       NVARCHAR(20)   NOT NULL,
    IdNO          BIGINT IDENTITY(1,1) NOT NULL,
    Contno        VARCHAR(50)    NOT NULL,
    Itemcode      NVARCHAR(20)   NOT NULL,
    OraPoNO       NVARCHAR(50)   NULL,
    LPMDT         DATE           NULL,
    Result        NVARCHAR(20)   NULL,
    ResultType    NVARCHAR(50)   NULL,
    QtyIssue      INT            NOT NULL CONSTRAINT DF_WmsPCR_QtyIssue DEFAULT(0),
    BoxNo         VARCHAR(50)    NULL,
    Style         NVARCHAR(40)   NULL,
    -- denormalised from old usa.dbo.USAOrgFile (dropped)
    ItemName      NVARCHAR(200)  NULL,
    Size          NVARCHAR(20)   NULL,
    Color         NVARCHAR(40)   NULL,
    Brand         NVARCHAR(60)   NULL,
    Season        NVARCHAR(50)   NULL,
    Gender        NVARCHAR(20)   NULL,
    Hscode        NVARCHAR(40)   NULL,
    ManifestQty   INT            NULL,
    CreateTS      DATETIME2(0)   NOT NULL CONSTRAINT DF_WmsPCR_TS DEFAULT(SYSDATETIME()),
    CONSTRAINT PK_WmsPCR PRIMARY KEY (Country, IdNO)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsPCR_CCIPo' AND object_id=OBJECT_ID('dbo.WmsPCR'))
    CREATE INDEX IX_WmsPCR_CCIPo ON dbo.WmsPCR (Country, Contno, Itemcode, OraPoNO, QtyIssue)
        INCLUDE (LPMDT, Result, ResultType);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsPCR_CStyle' AND object_id=OBJECT_ID('dbo.WmsPCR'))
    CREATE INDEX IX_WmsPCR_CStyle ON dbo.WmsPCR (Country, Contno, Style, LPMDT);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsPCR_CBox' AND object_id=OBJECT_ID('dbo.WmsPCR'))
    CREATE INDEX IX_WmsPCR_CBox ON dbo.WmsPCR (Country, Contno, BoxNo);

/* ============================================================================
   2. WmsOpenUSACont (replaces usa.dbo.OpenUSACont)
   Columns from user: contno, Closed, Trndate, Time1, Userid, contDesc, Whouse, OpenReason
   ============================================================================ */
IF OBJECT_ID('dbo.WmsOpenUSACont','U') IS NULL
CREATE TABLE dbo.WmsOpenUSACont (
    Country       NVARCHAR(20)   NOT NULL,
    contno        VARCHAR(50)    NOT NULL,
    Closed        CHAR(1)        NULL,
    Trndate       DATE           NULL,
    Time1         TIME(0)        NULL,
    Userid        NVARCHAR(100)  NULL,
    contDesc      NVARCHAR(200)  NULL,
    Whouse        NVARCHAR(50)   NULL,
    OpenReason    NVARCHAR(200)  NULL,
    CONSTRAINT PK_WmsOpenUSACont PRIMARY KEY (Country, contno)
);

/* ============================================================================
   3. WmsKNBBoxes (replaces usa.dbo.KNBBoxes)
   Columns: palletno, Boxno, trndate, userid, closed, Contno, Remarks, whouse
   ============================================================================ */
IF OBJECT_ID('dbo.WmsKNBBoxes','U') IS NULL
CREATE TABLE dbo.WmsKNBBoxes (
    Country       NVARCHAR(20)   NOT NULL,
    palletno      VARCHAR(50)    NULL,
    Boxno         VARCHAR(50)    NOT NULL,
    Contno        VARCHAR(50)    NOT NULL,
    trndate       DATE           NULL,
    userid        NVARCHAR(100)  NULL,
    closed        CHAR(1)        NULL,
    Remarks       NVARCHAR(200)  NULL,
    whouse        NVARCHAR(50)   NULL,
    CONSTRAINT PK_WmsKNBBoxes PRIMARY KEY (Country, Contno, Boxno)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsKNBBoxes_CCont' AND object_id=OBJECT_ID('dbo.WmsKNBBoxes'))
    CREATE INDEX IX_WmsKNBBoxes_CCont ON dbo.WmsKNBBoxes (Country, Contno);

/* ============================================================================
   4. WmsBuildingCompletion (replaces bfldata.dbo.buildingcompletion)
   Columns: Sn, Trndate, ContNo, TotalQty, BuildingQty, CleaningQty, DamageQty,
   MissingQty, ExcessQty, Details, UserID, RepairQty, WrittenOffQty, TrnTime,
   BuildGrp, CheckedQty, ContWOBuild, ReturnToSuppQty, ExpiredQty
   ============================================================================ */
IF OBJECT_ID('dbo.WmsBuildingCompletion','U') IS NULL
CREATE TABLE dbo.WmsBuildingCompletion (
    Country         NVARCHAR(20)   NOT NULL,
    Sn              BIGINT IDENTITY(1,1) NOT NULL,
    Trndate         DATE           NULL,
    ContNo          VARCHAR(50)    NOT NULL,
    TotalQty        INT            NULL,
    BuildingQty     INT            NULL,
    CleaningQty     INT            NULL,
    DamageQty       INT            NULL,
    MissingQty      INT            NULL,
    ExcessQty       INT            NULL,
    Details         NVARCHAR(MAX)  NULL,
    UserID          NVARCHAR(100)  NULL,
    RepairQty       INT            NULL,
    WrittenOffQty   INT            NULL,
    TrnTime         TIME(0)        NULL,
    BuildGrp        NVARCHAR(50)   NULL,
    CheckedQty      INT            NULL,
    ContWOBuild     CHAR(1)        NULL,
    ReturnToSuppQty INT            NULL,
    ExpiredQty      INT            NULL,
    CONSTRAINT PK_WmsBuildingCompletion PRIMARY KEY (Country, Sn)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsBC_CCont' AND object_id=OBJECT_ID('dbo.WmsBuildingCompletion'))
    CREATE INDEX IX_WmsBC_CCont ON dbo.WmsBuildingCompletion (Country, ContNo);

/* ============================================================================
   5. WmsBlueToteIDMaster (replaces bfldata.dbo.BlueToteIDMaster)
   Columns: ToteID, CurrDate, Remarks
   ============================================================================ */
IF OBJECT_ID('dbo.WmsBlueToteIDMaster','U') IS NULL
CREATE TABLE dbo.WmsBlueToteIDMaster (
    Country       NVARCHAR(20)   NOT NULL,
    ToteID        NVARCHAR(50)   NOT NULL,
    CurrDate      DATETIME2(0)   NULL,
    Remarks       NVARCHAR(200)  NULL,
    CONSTRAINT PK_WmsBlueToteIDMaster PRIMARY KEY (Country, ToteID)
);

/* ============================================================================
   6. WmsUPCBoxHead (replaces lpm.dbo.UPCBoxHeadLPM)
   Columns derived from BuildingService.CheckoutBoxAsync INSERT statement.
   ============================================================================ */
IF OBJECT_ID('dbo.WmsUPCBoxHead','U') IS NULL
CREATE TABLE dbo.WmsUPCBoxHead (
    Country       NVARCHAR(20)   NOT NULL,
    BoxNo         VARCHAR(50)    NOT NULL,
    TrnDate       DATE           NULL,
    Time1         TIME(0)        NULL,
    NewPallet     CHAR(1)        NULL,
    PreparedBy    NVARCHAR(100)  NULL,
    Remarks       NVARCHAR(200)  NULL,
    Userid        NVARCHAR(100)  NULL,
    PalletType    NVARCHAR(50)   NULL,
    Closed        CHAR(1)        NOT NULL CONSTRAINT DF_WmsUPCBoxHead_Closed DEFAULT('N'),
    GroupCode     NVARCHAR(20)   NULL,
    OldBoxNo      VARCHAR(50)    NULL,
    Prepared1     NVARCHAR(100)  NULL,
    Prepared2     NVARCHAR(100)  NULL,
    WHouse        NVARCHAR(50)   NULL,
    FWType        NVARCHAR(50)   NULL,
    FPreparedBy   NVARCHAR(100)  NULL,
    FPalletType   NVARCHAR(50)   NULL,
    ISize         NVARCHAR(20)   NULL,
    Gender        NVARCHAR(20)   NULL,
    ToteID        NVARCHAR(50)   NULL,
    LPMDT         DATE           NULL,
    CONSTRAINT PK_WmsUPCBoxHead PRIMARY KEY (Country, BoxNo)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsUPCBoxHead_CTote' AND object_id=OBJECT_ID('dbo.WmsUPCBoxHead'))
    CREATE INDEX IX_WmsUPCBoxHead_CTote ON dbo.WmsUPCBoxHead (Country, ToteID, Closed);

/* ============================================================================
   7. WmsUPCBoxDet (replaces lpm.dbo.UPCBoxDetLPM)
   Columns derived from BuildingService INSERT.
   ============================================================================ */
IF OBJECT_ID('dbo.WmsUPCBoxDet','U') IS NULL
CREATE TABLE dbo.WmsUPCBoxDet (
    Country       NVARCHAR(20)   NOT NULL,
    BoxNo         VARCHAR(50)    NOT NULL,
    Itemcode      NVARCHAR(20)   NOT NULL,
    SrNo          INT            NOT NULL,
    Qty           INT            NOT NULL,
    QtyIssued     INT            NOT NULL CONSTRAINT DF_WmsUPCBoxDet_QtyIssued DEFAULT(0),
    Status        NVARCHAR(20)   NULL,
    UPC           NVARCHAR(50)   NULL,
    imgfile       NVARCHAR(200)  NULL,
    CONSTRAINT PK_WmsUPCBoxDet PRIMARY KEY (Country, BoxNo, SrNo)
);

/* ============================================================================
   8. WmsPhotochecking (replaces lpm.dbo.PhotocheckingLPM)
   Columns derived from BuildingService INSERT. One row per scan.
   ============================================================================ */
IF OBJECT_ID('dbo.WmsPhotochecking','U') IS NULL
CREATE TABLE dbo.WmsPhotochecking (
    Country          NVARCHAR(20)   NOT NULL,
    Sn               BIGINT IDENTITY(1,1) NOT NULL,
    ContNo           VARCHAR(50)    NOT NULL,
    TrnDate          DATE           NULL,
    Time1            TIME(0)        NULL,
    UPC              NVARCHAR(50)   NULL,
    PhotoSize        NVARCHAR(20)   NULL,
    Result           NVARCHAR(20)   NULL,
    CheckedBy        NVARCHAR(100)  NULL,
    CmpName          NVARCHAR(100)  NULL,
    BoxSize          NVARCHAR(20)   NULL,
    Photo            NVARCHAR(MAX)  NULL,
    Style            NVARCHAR(40)   NULL,
    Color            NVARCHAR(40)   NULL,
    GroupCode        NVARCHAR(20)   NULL,
    ItemName         NVARCHAR(200)  NULL,
    Warehouse        NVARCHAR(50)   NULL,
    PhotoCheckType   NVARCHAR(50)   NULL,
    RRP              DECIMAL(18,2)  NULL,
    Logistics_BoxNo  NVARCHAR(100)  NULL,
    Season           NVARCHAR(50)   NULL,
    ToteID           NVARCHAR(50)   NULL,
    RoboStatus       CHAR(1)        NULL,
    BarCode          NVARCHAR(100)  NULL,
    CONSTRAINT PK_WmsPhotochecking PRIMARY KEY (Country, Sn)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsPhotochecking_CCont' AND object_id=OBJECT_ID('dbo.WmsPhotochecking'))
    CREATE INDEX IX_WmsPhotochecking_CCont ON dbo.WmsPhotochecking (Country, ContNo);

PRINT 'Phase 2c install complete. 8 new tables created in WMS DB.';
PRINT 'Next: Phase 2d refactors BuildingService to query these instead of on-prem.';
PRINT 'Data migration from on-prem to these new tables is a separate effort — populate with INSERT scripts or sync workers before Manual Building works end-to-end.';
