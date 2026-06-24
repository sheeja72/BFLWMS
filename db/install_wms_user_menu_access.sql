/* =============================================================================
   WmsUserMenuAccess — per-user explicit menu grants.
   Run inside the Azure SQL WMS database.
   Idempotent.
   ============================================================================= */
IF OBJECT_ID('dbo.WmsUserMenuAccess','U') IS NULL
CREATE TABLE dbo.WmsUserMenuAccess (
    Username   NVARCHAR(100) NOT NULL,
    MenuKey    NVARCHAR(50)  NOT NULL,
    GrantedTS  DATETIME2(0)  NOT NULL CONSTRAINT DF_WmsUserMenuAccess_TS DEFAULT(SYSDATETIME()),
    GrantedBy  NVARCHAR(100) NOT NULL,
    CONSTRAINT PK_WmsUserMenuAccess PRIMARY KEY (Username, MenuKey)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsUserMenuAccess_User' AND object_id=OBJECT_ID('dbo.WmsUserMenuAccess'))
    CREATE INDEX IX_WmsUserMenuAccess_User ON dbo.WmsUserMenuAccess (Username);

PRINT 'WmsUserMenuAccess ready.';
