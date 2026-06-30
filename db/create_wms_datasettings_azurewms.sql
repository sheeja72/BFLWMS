/* =============================================================================
   Create dbo.WMS_DataSettings on the Azure WMS DB — incremental mirror of
   bfldata.dbo.DataSettings (138 cols), used by the Tote / Box / per-country
   sync features as the runtime country -> DataName lookup so the WMS app
   does not need OnPremBackup for that lookup on every Sync click.

   Sync strategy: incremental on CreateDate. The Data Sync page's "Sync Data
   Settings" button reads MAX(CreateDate) on this table and pulls rows from
   the source where CreateDate > that high-water mark. First sync pulls all
   rows. Updates to existing source rows are NOT picked up (creator field,
   not modifier) — re-mirror by truncating and re-running if needed.

   Surrogate IdNo IDENTITY PK so duplicates from re-syncs are storable
   without violating constraint; SyncedTS for audit. Indexes on SIMCountry
   (lookup key) and CreateDate (high-water query).

   Run inside the Azure WMS DB. Idempotent.
   ============================================================================= */

IF OBJECT_ID('dbo.WMS_DataSettings', 'U') IS NULL
CREATE TABLE dbo.WMS_DataSettings (
    IdNo                       INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ShopName                   VARCHAR(15)    NULL,
    Dataname                   VARCHAR(10)    NULL,
    UnitCode                   VARCHAR(3)     NULL,
    FCCode                     VARCHAR(3)     NULL,
    FCRate                     NUMERIC(18,4)  NULL,
    CeilingType                CHAR(1)        NULL,
    CostCodeFrom               VARCHAR(3)     NULL,
    LocCodeFrom                VARCHAR(3)     NULL,
    CostCodeTo                 VARCHAR(3)     NULL,
    LocCodeTo                  VARCHAR(3)     NULL,
    Decimals                   INT            NULL,
    TCMItemCode                VARCHAR(15)    NULL,
    USAItemCode                VARCHAR(15)    NULL,
    Transfer                   CHAR(1)        NULL,
    TargetServer               VARCHAR(100)   NULL,
    TargetDatabase             VARCHAR(20)    NULL,
    CostCode                   VARCHAR(3)     NULL,
    Import                     VARCHAR(1)     NULL,
    TargetPath                 VARCHAR(300)   NULL,
    CalcInv                    CHAR(1)        NULL,
    AttendancePath             VARCHAR(50)    NULL,
    ExportData                 CHAR(1)        NULL,
    BranchCode                 VARCHAR(6)     NULL,
    Form69F                    VARCHAR(15)    NULL,
    barshopname                VARCHAR(100)   NULL,
    barcompname                VARCHAR(100)   NULL,
    DailyQuota                 INT            NULL,
    RepRowNo                   INT            NULL,
    USA                        VARCHAR(20)    NULL,
    Add1                       VARCHAR(100)   NULL,
    Add2                       VARCHAR(100)   NULL,
    Add3                       VARCHAR(100)   NULL,
    Add4                       VARCHAR(100)   NULL,
    Add5                       VARCHAR(100)   NULL,
    Add6                       VARCHAR(100)   NULL,
    MaxQtyField                VARCHAR(20)    NULL,
    Itemdisc                   VARCHAR(1)     NULL,
    TCMTarget                  INT            NULL,
    ShopLetter                 VARCHAR(2)     NULL,
    CurrStock                  INT            NULL,
    SalesQty                   INT            NULL,
    TCMHTarget                 INT            NULL,
    TCMCTarget                 INT            NULL,
    TCMWTarget                 INT            NULL,
    PRCreditCode               VARCHAR(6)     NULL,
    Transport                  INT            NULL,
    DueToFromAc                VARCHAR(6)     NULL,
    NewTCMPrice                VARCHAR(1)     NULL,
    MaxQty                     INT            NULL,
    TrfQty                     INT            NULL,
    StopDel                    VARCHAR(1)     NULL,
    TCMStock                   INT            NULL,
    OpenDate                   SMALLDATETIME  NULL,
    RFId                       VARCHAR(1)     NULL,
    RFTag                      VARCHAR(1)     NULL,
    Area                       VARCHAR(100)   NULL,
    [Size]                     VARCHAR(2)     NULL,
    Active                     VARCHAR(1)     NULL,
    OracleLocation             VARCHAR(25)    NULL,
    MaxQtyW                    INT            NULL,
    CurrStockW                 INT            NULL,
    TrfQtyW                    INT            NULL,
    MaxQtyH                    INT            NULL,
    CurrStockH                 INT            NULL,
    TrfQtyH                    INT            NULL,
    AmzDb                      VARCHAR(20)    NULL,
    PalletPrefix               VARCHAR(3)     NULL,
    Production                 VARCHAR(1)     NULL,
    PalletType                 VARCHAR(3)     NULL,
    RetailNext                 VARCHAR(1)     NULL,
    StoreID                    VARCHAR(25)    NULL,
    ERPCostcode                VARCHAR(4)     NULL,
    ShopSizeSQFt               NUMERIC(18,4)  NULL,
    EmaarStore                 VARCHAR(15)    NULL,
    Emaar_TenantCode           VARCHAR(10)    NULL,
    DefaultMinQty              INT            NULL,
    ShopEmail                  VARCHAR(50)    NULL,
    OnlineCountryId            INT            NULL,
    erploccode                 VARCHAR(5)     NULL,
    Company                    VARCHAR(25)    NULL,
    MUYShop                    NVARCHAR(1)    NULL,
    CollectionSize             VARCHAR(1)     NULL,
    Remarks                    VARCHAR(100)   NULL,
    DraftORPercMax             NUMERIC(18,4)  NULL,
    ExportActive               VARCHAR(1)     NULL,
    MixMaxFLAG                 VARCHAR(30)    NULL,
    GRPMIXFLAG                 VARCHAR(30)    NULL,
    CollectionDay              NVARCHAR(9)    NULL,
    ShopInShop                 VARCHAR(1)     NULL,
    R1ToGo                     VARCHAR(1)     NULL,
    AnyP                       VARCHAR(15)    NULL,
    MuyStoreID                 INT            NULL,
    IAQtyField                 VARCHAR(25)    NULL,
    IATrfQtyField              VARCHAR(25)    NULL,
    AddSalesPricePerc          NUMERIC(18,4)  NULL,
    R1Prod                     VARCHAR(1)     NULL,
    ShopGrade                  VARCHAR(20)    NULL,
    shift                      VARCHAR(2)     NULL,
    SalesIntegrated            VARCHAR(15)    NULL,
    PrintWasNow                VARCHAR(1)     NULL,
    CountryCode                VARCHAR(3)     NULL,
    AttendancePort             VARCHAR(10)    NULL,
    POS                        INT            NULL,
    BANK                       VARCHAR(50)    NULL,
    Country                    VARCHAR(20)    NULL,
    CalcVat                    VARCHAR(1)     NULL,
    ExportWH                   VARCHAR(1)     NULL,
    ExportCountryCode          VARCHAR(3)     NULL,
    ERPLedgerID                VARCHAR(20)    NULL,
    SizeSqMtTotal              NUMERIC(18,4)  NULL,
    SizeTCMSqMt                NUMERIC(18,4)  NULL,
    ExportP2                   VARCHAR(1)     NULL,
    ProductionRWH              VARCHAR(1)     NULL,
    PalletTypeW                VARCHAR(3)     NULL,
    SalesIntegration           VARCHAR(25)    NULL,
    RouteId                    INT            NULL,
    ISOCountryCode             VARCHAR(5)     NULL,
    VATPerc                    NUMERIC(18,4)  NULL,
    ShopCode                   VARCHAR(5)     NULL,
    ShopSupervisor             VARCHAR(100)   NULL,
    bckbarshopname             VARCHAR(50)    NULL,
    TelNo                      VARCHAR(20)    NULL,
    ProdActiveFromJafza        VARCHAR(1)     NULL,
    GradeLetter                VARCHAR(3)     NULL,
    ShopType                   VARCHAR(15)    NULL,
    RoboShopId                 SMALLINT       NULL,
    spcode                     VARCHAR(10)    NULL,
    ActiveStore                VARCHAR(1)     NULL,
    RMSStoreID                 INT            NULL,
    CoffeeShopLetter           VARCHAR(2)     NULL,
    OnlinePriceAPI             CHAR(1)        NULL,
    ExpDataName                VARCHAR(25)    NULL,
    ExpCostCode                VARCHAR(3)     NULL,
    PrintFcCode                VARCHAR(3)     NULL,
    PrintPriceSticker          VARCHAR(1)     NULL,
    ExpLocCode                 VARCHAR(2)     NULL,
    PBFullname                 VARCHAR(200)   NULL,
    CalcVatForOnlineReturn     VARCHAR(1)     NULL,
    ExpInterCompAc             VARCHAR(15)    NULL,
    Concept                    VARCHAR(20)    NULL,
    CloseDate                  DATE           NULL,
    GcpOpenDate                SMALLDATETIME  NULL,
    CreateDate                 SMALLDATETIME  NULL,
    MFCSSOH                    VARCHAR(1)     NULL,
    CountryID                  VARCHAR(3)     NULL,
    SIMCountry                 VARCHAR(20)    NULL,
    SyncedTS                   DATETIME2(0)   NOT NULL CONSTRAINT DF_WDS_SyncedTS DEFAULT(SYSDATETIME())
);

/* SIMCountry is the lookup key from the WMS Tote sync side. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_WDS_SIMCountry'
                 AND object_id = OBJECT_ID('dbo.WMS_DataSettings'))
    CREATE INDEX IX_WDS_SIMCountry ON dbo.WMS_DataSettings (SIMCountry)
        INCLUDE (Dataname, StoreID, ShopCode, ActiveStore);

/* Used by the incremental sync's MAX(CreateDate) high-water query. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_WDS_CreateDate'
                 AND object_id = OBJECT_ID('dbo.WMS_DataSettings'))
    CREATE INDEX IX_WDS_CreateDate ON dbo.WMS_DataSettings (CreateDate DESC);

PRINT 'Azure WMS dbo.WMS_DataSettings ready.';
