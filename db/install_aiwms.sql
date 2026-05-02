/* =============================================================================
   AIWMS install script — creates AIWMS database (if absent) plus all app tables
   and indexes. Idempotent: rerun-safe.
   Default target instance: 192.168.10.72 (admin can reconfigure in app).
   ============================================================================= */

IF DB_ID('AIWMS') IS NULL
    EXEC('CREATE DATABASE AIWMS');
GO

USE AIWMS;
GO

/* ---------- AppConfig: encrypted connection settings + version flag ---------- */
IF OBJECT_ID('dbo.AppConfig','U') IS NULL
CREATE TABLE dbo.AppConfig (
    [Key]        NVARCHAR(100)   NOT NULL PRIMARY KEY,
    [Value]      NVARCHAR(MAX)   NULL,
    UpdatedTS    DATETIME2(0)    NOT NULL CONSTRAINT DF_AppConfig_TS DEFAULT (SYSDATETIME()),
    UpdatedBy    NVARCHAR(100)   NOT NULL CONSTRAINT DF_AppConfig_By DEFAULT (SUSER_SNAME())
);

/* ---------- Users / Roles / UserRoles ---------- */
IF OBJECT_ID('dbo.AiwmsRole','U') IS NULL
CREATE TABLE dbo.AiwmsRole (
    RoleCode    NVARCHAR(20)   NOT NULL PRIMARY KEY,
    RoleName    NVARCHAR(100)  NOT NULL,
    CreateTS    DATETIME2(0)   NOT NULL CONSTRAINT DF_AiwmsRole_TS DEFAULT (SYSDATETIME())
);

IF NOT EXISTS (SELECT 1 FROM dbo.AiwmsRole)
INSERT INTO dbo.AiwmsRole (RoleCode, RoleName) VALUES
    ('Admin',         'Administrator'),
    ('WHAssociate',   'WH Associate'),
    ('WHSupervisor',  'WH Supervisor'),
    ('WHManager',     'WH Manager');

IF OBJECT_ID('dbo.AiwmsUser','U') IS NULL
CREATE TABLE dbo.AiwmsUser (
    Username      NVARCHAR(100) NOT NULL PRIMARY KEY,
    DisplayName   NVARCHAR(200) NULL,
    Email         NVARCHAR(200) NULL,
    Country       NVARCHAR(20)  NULL,
    Warehouse     NVARCHAR(50)  NULL,
    IsActive      BIT           NOT NULL CONSTRAINT DF_AiwmsUser_Active DEFAULT (1),
    CreateTS      DATETIME2(0)  NOT NULL CONSTRAINT DF_AiwmsUser_TS DEFAULT (SYSDATETIME()),
    CreatedBy     NVARCHAR(100) NOT NULL
);

IF COL_LENGTH('dbo.AiwmsUser','Country') IS NULL
    ALTER TABLE dbo.AiwmsUser ADD Country NVARCHAR(20) NULL;
IF COL_LENGTH('dbo.AiwmsUser','Warehouse') IS NULL
    ALTER TABLE dbo.AiwmsUser ADD Warehouse NVARCHAR(50) NULL;

/* ---------- WHMaster: warehouses per country (admin-managed) ---------- */
IF OBJECT_ID('dbo.AiwmsWHMaster','U') IS NULL
CREATE TABLE dbo.AiwmsWHMaster (
    Country     NVARCHAR(20)  NOT NULL,
    Warehouse   NVARCHAR(50)  NOT NULL,
    Active      BIT           NOT NULL CONSTRAINT DF_AiwmsWH_Active DEFAULT (1),
    CreateTS    DATETIME2(0)  NOT NULL CONSTRAINT DF_AiwmsWH_TS DEFAULT (SYSDATETIME()),
    CreatedBy   NVARCHAR(100) NOT NULL CONSTRAINT DF_AiwmsWH_By DEFAULT (SUSER_SNAME()),
    CONSTRAINT PK_AiwmsWHMaster PRIMARY KEY (Country, Warehouse)
);

IF OBJECT_ID('dbo.AiwmsUserRole','U') IS NULL
CREATE TABLE dbo.AiwmsUserRole (
    Username   NVARCHAR(100) NOT NULL,
    RoleCode   NVARCHAR(20)  NOT NULL,
    CreateTS   DATETIME2(0)  NOT NULL CONSTRAINT DF_AiwmsUserRole_TS DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_AiwmsUserRole PRIMARY KEY (Username, RoleCode),
    CONSTRAINT FK_AiwmsUserRole_User FOREIGN KEY (Username) REFERENCES dbo.AiwmsUser(Username) ON DELETE CASCADE,
    CONSTRAINT FK_AiwmsUserRole_Role FOREIGN KEY (RoleCode) REFERENCES dbo.AiwmsRole(RoleCode) ON DELETE CASCADE
);

/* ---------- AuditLog ---------- */
IF OBJECT_ID('dbo.AiwmsAuditLog','U') IS NULL
CREATE TABLE dbo.AiwmsAuditLog (
    Id            BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    EntityName    NVARCHAR(100)  NOT NULL,
    EntityKey     NVARCHAR(200)  NOT NULL,
    Action        CHAR(1)        NOT NULL, -- I,U,D,X(custom)
    ChangedBy     NVARCHAR(100)  NOT NULL,
    ChangedTS     DATETIME2(0)   NOT NULL,
    ClientIp      NVARCHAR(45)   NULL,
    Context       NVARCHAR(200)  NULL,     -- e.g. "Building/AEINT6078/0001"
    ChangesJson   NVARCHAR(MAX)  NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiwmsAuditLog_Entity' AND object_id=OBJECT_ID('dbo.AiwmsAuditLog'))
    CREATE INDEX IX_AiwmsAuditLog_Entity ON dbo.AiwmsAuditLog (EntityName, EntityKey, ChangedTS DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiwmsAuditLog_User' AND object_id=OBJECT_ID('dbo.AiwmsAuditLog'))
    CREATE INDEX IX_AiwmsAuditLog_User ON dbo.AiwmsAuditLog (ChangedBy, ChangedTS DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiwmsAuditLog_TS' AND object_id=OBJECT_ID('dbo.AiwmsAuditLog'))
    CREATE INDEX IX_AiwmsAuditLog_TS ON dbo.AiwmsAuditLog (ChangedTS DESC);

/* ---------- BoxSequence: per-container monotonic sequence (concurrency-safe) ---------- */
IF OBJECT_ID('dbo.AiwmsBoxSequence','U') IS NULL
CREATE TABLE dbo.AiwmsBoxSequence (
    Contno      VARCHAR(50)  NOT NULL PRIMARY KEY,
    NextSeq     INT          NOT NULL CONSTRAINT DF_AiwmsBoxSeq_Next DEFAULT (1),
    UpdatedTS   DATETIME2(0) NOT NULL CONSTRAINT DF_AiwmsBoxSeq_TS DEFAULT (SYSDATETIME())
);

/* ---------- Open box staging (between check-in and check-out) ----------
   Rows live here until the user clicks Check Out, at which point we INSERT
   lpm.UPCBoxHeadLPM/UPCBoxDetLPM/PhotocheckingLPM and DELETE the staging rows.
   Uniqueness enforced so a BoxNo can never duplicate even under concurrent users. */
IF OBJECT_ID('dbo.AiwmsOpenBox','U') IS NULL
CREATE TABLE dbo.AiwmsOpenBox (
    BoxNo         VARCHAR(50)   NOT NULL PRIMARY KEY,
    Contno        VARCHAR(50)   NOT NULL,
    UserId        NVARCHAR(100) NOT NULL,
    PalletType    NVARCHAR(50)  NOT NULL,
    Division      NVARCHAR(50)  NULL,
    Season        NVARCHAR(50)  NULL,
    LPMDt         DATE          NULL,
    ToteID        NVARCHAR(50)  NULL,             -- nullable; tote attached via per-row Check-In after items scanned
    LogisticsBoxNo NVARCHAR(100) NULL,
    CreateTS      DATETIME2(0)  NOT NULL CONSTRAINT DF_AiwmsOpenBox_TS DEFAULT (SYSDATETIME())
);

/* If pre-existing table had ToteID NOT NULL, relax it. */
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.AiwmsOpenBox') AND name = 'ToteID' AND is_nullable = 0)
    ALTER TABLE dbo.AiwmsOpenBox ALTER COLUMN ToteID NVARCHAR(50) NULL;

/* Unique index that allows NULL but blocks duplicate non-null tote attachment. */
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name='UQ_AiwmsOpenBox_Tote' AND object_id=OBJECT_ID('dbo.AiwmsOpenBox'))
    DROP INDEX UQ_AiwmsOpenBox_Tote ON dbo.AiwmsOpenBox;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UQ_AiwmsOpenBox_Tote' AND object_id=OBJECT_ID('dbo.AiwmsOpenBox'))
    CREATE UNIQUE INDEX UQ_AiwmsOpenBox_Tote ON dbo.AiwmsOpenBox (ToteID) WHERE ToteID IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiwmsOpenBox_Contno' AND object_id=OBJECT_ID('dbo.AiwmsOpenBox'))
    CREATE INDEX IX_AiwmsOpenBox_Contno ON dbo.AiwmsOpenBox (Contno);

IF OBJECT_ID('dbo.AiwmsOpenBoxItem','U') IS NULL
CREATE TABLE dbo.AiwmsOpenBoxItem (
    Id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    BoxNo       VARCHAR(50)   NOT NULL,
    ItemCode    NVARCHAR(20)  NOT NULL,
    Qty         INT           NOT NULL CONSTRAINT DF_AiwmsOpenBoxItem_Qty DEFAULT (1),
    SrNo        INT           NOT NULL,
    Result      NVARCHAR(20)  NULL,
    PCRowId     BIGINT        NULL,             -- lpm.PCR.IdNO we incremented or inserted
    Size        NVARCHAR(20)  NULL,
    Color       NVARCHAR(40)  NULL,
    Style       NVARCHAR(40)  NULL,
    GroupCode   NVARCHAR(20)  NULL,
    Season      NVARCHAR(50)  NULL,
    ScannedTS   DATETIME2(0)  NOT NULL CONSTRAINT DF_AiwmsOpenBoxItem_TS DEFAULT (SYSDATETIME()),
    CONSTRAINT FK_AiwmsOpenBoxItem_Box FOREIGN KEY (BoxNo) REFERENCES dbo.AiwmsOpenBox(BoxNo) ON DELETE CASCADE
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiwmsOpenBoxItem_Box' AND object_id=OBJECT_ID('dbo.AiwmsOpenBoxItem'))
    CREATE INDEX IX_AiwmsOpenBoxItem_Box ON dbo.AiwmsOpenBoxItem (BoxNo, SrNo);

/* ---------- AiwmsOpenBoxScan: per-scan PCR effect log (used by Clear/cancel to roll back) ---------- */
IF OBJECT_ID('dbo.AiwmsOpenBoxScan','U') IS NULL
CREATE TABLE dbo.AiwmsOpenBoxScan (
    Id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    BoxNo       VARCHAR(50)   NOT NULL,
    ItemCode    NVARCHAR(20)  NOT NULL,
    PcrAction   CHAR(1)       NOT NULL,         -- 'U' = QtyIssue+=1 on existing PCR row; 'I' = INSERT new PCR row
    PcrId       BIGINT        NULL,             -- the PCR.IdNO that was affected
    ScannedTS   DATETIME2(0)  NOT NULL CONSTRAINT DF_AiwmsOpenBoxScan_TS DEFAULT (SYSDATETIME()),
    CONSTRAINT FK_AiwmsOpenBoxScan_Box FOREIGN KEY (BoxNo) REFERENCES dbo.AiwmsOpenBox(BoxNo) ON DELETE CASCADE
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiwmsOpenBoxScan_Box' AND object_id=OBJECT_ID('dbo.AiwmsOpenBoxScan'))
    CREATE INDEX IX_AiwmsOpenBoxScan_Box ON dbo.AiwmsOpenBoxScan (BoxNo, ScannedTS DESC);

/* ---------- ContainerPhotoCheck: cache the one-time qty match per container ---------- */
IF OBJECT_ID('dbo.AiwmsContainerPhotoCheck','U') IS NULL
CREATE TABLE dbo.AiwmsContainerPhotoCheck (
    Contno          VARCHAR(50)   NOT NULL PRIMARY KEY,
    PhotoQty        INT           NOT NULL,
    OrgQty          INT           NOT NULL,
    Matched         BIT           NOT NULL,
    CheckedTS       DATETIME2(0)  NOT NULL CONSTRAINT DF_AiwmsCPC_TS DEFAULT (SYSDATETIME()),
    CheckedBy       NVARCHAR(100) NOT NULL
);

GO

/* =============================================================================
   Recommended indexes on EXISTING shared DBs
   These are CREATE-IF-NOT-EXISTS so safe to rerun. Review with DBA before
   running in production — they speed the LPM Manual Building hot path.
   ============================================================================= */

/* lpm.dbo.PhotoCheckingResultLPM — biggest hot path (4-tier allocation lookup) */
IF DB_ID('lpm') IS NOT NULL
BEGIN
    EXEC('USE lpm;
    IF OBJECT_ID(''dbo.PhotoCheckingResultLPM'',''U'') IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_PCR_Cont_Item_PO_Qty'' AND object_id=OBJECT_ID(''dbo.PhotoCheckingResultLPM''))
            CREATE INDEX IX_PCR_Cont_Item_PO_Qty ON dbo.PhotoCheckingResultLPM (Contno, Itemcode, OraPoNO, QtyIssue) INCLUDE (LPMDT, Result, ResultType);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_PCR_Cont_Style'' AND object_id=OBJECT_ID(''dbo.PhotoCheckingResultLPM''))
            CREATE INDEX IX_PCR_Cont_Style ON dbo.PhotoCheckingResultLPM (Contno, Style, LPMDT);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_PCR_Cont_Box'' AND object_id=OBJECT_ID(''dbo.PhotoCheckingResultLPM''))
            CREATE INDEX IX_PCR_Cont_Box ON dbo.PhotoCheckingResultLPM (Contno, BoxNo);
    END');
END
GO

/* Each index guarded by COL_LENGTH so columns missing in your DB are silently skipped. */
IF DB_ID('usa') IS NOT NULL
BEGIN
    EXEC('USE usa;
    IF OBJECT_ID(''dbo.KNBBoxes'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.KNBBoxes'',''contno'') IS NOT NULL
       AND COL_LENGTH(''dbo.KNBBoxes'',''boxno'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_KNBBoxes_Cont_Box'' AND object_id=OBJECT_ID(''dbo.KNBBoxes''))
        CREATE INDEX IX_KNBBoxes_Cont_Box ON dbo.KNBBoxes (contno, boxno);

    IF OBJECT_ID(''dbo.OpenUSACont'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.OpenUSACont'',''contno'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_OpenUSACont_Cont'' AND object_id=OBJECT_ID(''dbo.OpenUSACont''))
        CREATE INDEX IX_OpenUSACont_Cont ON dbo.OpenUSACont (contno);

    IF OBJECT_ID(''dbo.USAOrgFile'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.USAOrgFile'',''contno'') IS NOT NULL
       AND COL_LENGTH(''dbo.USAOrgFile'',''itemcode'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_USAOrgFile_Cont_Item'' AND object_id=OBJECT_ID(''dbo.USAOrgFile''))
        CREATE INDEX IX_USAOrgFile_Cont_Item ON dbo.USAOrgFile (contno, itemcode);

    IF OBJECT_ID(''dbo.USAOrgFile'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.USAOrgFile'',''contno'') IS NOT NULL
       AND COL_LENGTH(''dbo.USAOrgFile'',''OraPONo'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_USAOrgFile_Cont_OraPONo'' AND object_id=OBJECT_ID(''dbo.USAOrgFile''))
        CREATE INDEX IX_USAOrgFile_Cont_OraPONo ON dbo.USAOrgFile (contno, OraPONo);

    IF OBJECT_ID(''dbo.UPCbarcodes'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.UPCbarcodes'',''itemcode'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_UPCbarcodes_Item'' AND object_id=OBJECT_ID(''dbo.UPCbarcodes''))
        CREATE INDEX IX_UPCbarcodes_Item ON dbo.UPCbarcodes (itemcode);');
END
GO

IF DB_ID('lpm') IS NOT NULL
BEGIN
    EXEC('USE lpm;
    IF OBJECT_ID(''dbo.UPCBoxHeadLPM'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.UPCBoxHeadLPM'',''ToteID'') IS NOT NULL
       AND COL_LENGTH(''dbo.UPCBoxHeadLPM'',''Closed'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_UPCBoxHeadLPM_Tote_Closed'' AND object_id=OBJECT_ID(''dbo.UPCBoxHeadLPM''))
        CREATE INDEX IX_UPCBoxHeadLPM_Tote_Closed ON dbo.UPCBoxHeadLPM (ToteID, Closed);

    IF OBJECT_ID(''dbo.UPCBoxHeadLPM'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.UPCBoxHeadLPM'',''BoxNo'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_UPCBoxHeadLPM_Box'' AND object_id=OBJECT_ID(''dbo.UPCBoxHeadLPM''))
        CREATE INDEX IX_UPCBoxHeadLPM_Box ON dbo.UPCBoxHeadLPM (BoxNo);');
END
GO

IF DB_ID('bfldata') IS NOT NULL
BEGIN
    EXEC('USE bfldata;
    IF OBJECT_ID(''dbo.buildingcompletion'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.buildingcompletion'',''Contno'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_buildingcompletion_Cont'' AND object_id=OBJECT_ID(''dbo.buildingcompletion''))
        CREATE INDEX IX_buildingcompletion_Cont ON dbo.buildingcompletion (Contno);

    IF OBJECT_ID(''dbo.contreceipt'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.contreceipt'',''refno'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_contreceipt_Refno'' AND object_id=OBJECT_ID(''dbo.contreceipt''))
        CREATE INDEX IX_contreceipt_Refno ON dbo.contreceipt (refno);

    IF OBJECT_ID(''dbo.BlueToteIDMaster'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.BlueToteIDMaster'',''ToteID'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_BlueToteIDMaster_Tote'' AND object_id=OBJECT_ID(''dbo.BlueToteIDMaster''))
        CREATE INDEX IX_BlueToteIDMaster_Tote ON dbo.BlueToteIDMaster (ToteID);');
END
GO

IF DB_ID('datareporting') IS NOT NULL
BEGIN
    EXEC('USE datareporting;
    IF OBJECT_ID(''dbo.upc_subclass'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.upc_subclass'',''itemcode'') IS NOT NULL
       AND COL_LENGTH(''dbo.upc_subclass'',''MH4ID'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_upc_subclass_Item'' AND object_id=OBJECT_ID(''dbo.upc_subclass''))
        CREATE INDEX IX_upc_subclass_Item ON dbo.upc_subclass (itemcode) INCLUDE (MH4ID);

    IF OBJECT_ID(''dbo.SubclassMaster'',''U'') IS NOT NULL
       AND COL_LENGTH(''dbo.SubclassMaster'',''MH4ID'') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=''IX_SubclassMaster_MH4'' AND object_id=OBJECT_ID(''dbo.SubclassMaster''))
        CREATE INDEX IX_SubclassMaster_MH4 ON dbo.SubclassMaster (MH4ID);');
END
GO

PRINT 'AIWMS install complete. Remember to seed first admin user via the app /setup page.';
GO
