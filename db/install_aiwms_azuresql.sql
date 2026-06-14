/* =============================================================================
   AIWMS install script — Azure SQL Database
   Run inside the AIWMS database on bfl-wms-sql.database.windows.net.
   Idempotent: rerun-safe.

   Differences from on-prem install_aiwms.sql:
     - No CREATE DATABASE / USE  (Azure SQL DB is already provisioned per-DB)
     - No cross-DB index sections (lpm/usa/bfldata/datareporting stay on-prem)
     - Seeds sheeja@bflgroup.ae as the first Admin (UAE)

   Per-country on-prem connections are NOT stored here — they live in Azure
   App Service → Configuration → Connection strings (same pattern as LPMSIM).
   ============================================================================= */

/* ---------- AppConfig: app-level key/value store (kept for compat) ---------- */
IF OBJECT_ID('dbo.AppConfig','U') IS NULL
CREATE TABLE dbo.AppConfig (
    [Key]        NVARCHAR(100)   NOT NULL PRIMARY KEY,
    [Value]      NVARCHAR(MAX)   NULL,
    UpdatedTS    DATETIME2(0)    NOT NULL CONSTRAINT DF_AppConfig_TS DEFAULT (SYSDATETIME()),
    UpdatedBy    NVARCHAR(100)   NOT NULL CONSTRAINT DF_AppConfig_By DEFAULT (SUSER_SNAME())
);

/* ---------- Roles ---------- */
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

/* ---------- Users ---------- */
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

/* ---------- UserRoles ---------- */
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
    Context       NVARCHAR(200)  NULL,
    ChangesJson   NVARCHAR(MAX)  NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiwmsAuditLog_Entity' AND object_id=OBJECT_ID('dbo.AiwmsAuditLog'))
    CREATE INDEX IX_AiwmsAuditLog_Entity ON dbo.AiwmsAuditLog (EntityName, EntityKey, ChangedTS DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiwmsAuditLog_User' AND object_id=OBJECT_ID('dbo.AiwmsAuditLog'))
    CREATE INDEX IX_AiwmsAuditLog_User ON dbo.AiwmsAuditLog (ChangedBy, ChangedTS DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiwmsAuditLog_TS' AND object_id=OBJECT_ID('dbo.AiwmsAuditLog'))
    CREATE INDEX IX_AiwmsAuditLog_TS ON dbo.AiwmsAuditLog (ChangedTS DESC);

/* ---------- BoxSequence ---------- */
IF OBJECT_ID('dbo.AiwmsBoxSequence','U') IS NULL
CREATE TABLE dbo.AiwmsBoxSequence (
    Contno      VARCHAR(50)  NOT NULL PRIMARY KEY,
    NextSeq     INT          NOT NULL CONSTRAINT DF_AiwmsBoxSeq_Next DEFAULT (1),
    UpdatedTS   DATETIME2(0) NOT NULL CONSTRAINT DF_AiwmsBoxSeq_TS DEFAULT (SYSDATETIME())
);

/* ---------- Open box staging ---------- */
IF OBJECT_ID('dbo.AiwmsOpenBox','U') IS NULL
CREATE TABLE dbo.AiwmsOpenBox (
    BoxNo         VARCHAR(50)   NOT NULL PRIMARY KEY,
    Contno        VARCHAR(50)   NOT NULL,
    UserId        NVARCHAR(100) NOT NULL,
    PalletType    NVARCHAR(50)  NOT NULL,
    Division      NVARCHAR(50)  NULL,
    Season        NVARCHAR(50)  NULL,
    LPMDt         DATE          NULL,
    ToteID        NVARCHAR(50)  NULL,
    LogisticsBoxNo NVARCHAR(100) NULL,
    CreateTS      DATETIME2(0)  NOT NULL CONSTRAINT DF_AiwmsOpenBox_TS DEFAULT (SYSDATETIME())
);

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.AiwmsOpenBox') AND name = 'ToteID' AND is_nullable = 0)
    ALTER TABLE dbo.AiwmsOpenBox ALTER COLUMN ToteID NVARCHAR(50) NULL;

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
    PCRowId     BIGINT        NULL,
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

/* ---------- AiwmsOpenBoxScan ---------- */
IF OBJECT_ID('dbo.AiwmsOpenBoxScan','U') IS NULL
CREATE TABLE dbo.AiwmsOpenBoxScan (
    Id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    BoxNo       VARCHAR(50)   NOT NULL,
    ItemCode    NVARCHAR(20)  NOT NULL,
    PcrAction   CHAR(1)       NOT NULL,
    PcrId       BIGINT        NULL,
    ScannedTS   DATETIME2(0)  NOT NULL CONSTRAINT DF_AiwmsOpenBoxScan_TS DEFAULT (SYSDATETIME()),
    CONSTRAINT FK_AiwmsOpenBoxScan_Box FOREIGN KEY (BoxNo) REFERENCES dbo.AiwmsOpenBox(BoxNo) ON DELETE CASCADE
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_AiwmsOpenBoxScan_Box' AND object_id=OBJECT_ID('dbo.AiwmsOpenBoxScan'))
    CREATE INDEX IX_AiwmsOpenBoxScan_Box ON dbo.AiwmsOpenBoxScan (BoxNo, ScannedTS DESC);

/* ---------- ContainerPhotoCheck ---------- */
IF OBJECT_ID('dbo.AiwmsContainerPhotoCheck','U') IS NULL
CREATE TABLE dbo.AiwmsContainerPhotoCheck (
    Contno          VARCHAR(50)   NOT NULL PRIMARY KEY,
    PhotoQty        INT           NOT NULL,
    OrgQty          INT           NOT NULL,
    Matched         BIT           NOT NULL,
    CheckedTS       DATETIME2(0)  NOT NULL CONSTRAINT DF_AiwmsCPC_TS DEFAULT (SYSDATETIME()),
    CheckedBy       NVARCHAR(100) NOT NULL
);

/* =============================================================================
   Seed: first Admin user
   ============================================================================= */
IF NOT EXISTS (SELECT 1 FROM dbo.AiwmsUser WHERE Username = 'sheeja@bflgroup.ae')
INSERT INTO dbo.AiwmsUser (Username, DisplayName, Email, Country, IsActive, CreatedBy)
VALUES ('sheeja@bflgroup.ae', 'Sheeja Varghese', 'sheeja@bflgroup.ae', 'UAE', 1, 'install_aiwms_azuresql.sql');

IF NOT EXISTS (SELECT 1 FROM dbo.AiwmsUserRole WHERE Username = 'sheeja@bflgroup.ae' AND RoleCode = 'Admin')
INSERT INTO dbo.AiwmsUserRole (Username, RoleCode)
VALUES ('sheeja@bflgroup.ae', 'Admin');

PRINT 'AIWMS install complete (Azure SQL).';
PRINT 'Admin seeded: sheeja@bflgroup.ae (UAE).';
PRINT 'Next: grant the App Service Managed Identity (e.g. bfl-wms-app) via:';
PRINT '   CREATE USER [bfl-wms-app] FROM EXTERNAL PROVIDER;';
PRINT '   ALTER ROLE db_owner ADD MEMBER [bfl-wms-app];';
