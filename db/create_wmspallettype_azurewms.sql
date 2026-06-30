/* =============================================================================
   Create dbo.WmsPalletType on the Azure WMS DB — full mirror of
   bfldata.dbo.pallettype (30 cols). Same master across all countries.

   The Data Sync page's "Sync PalletType Master" button truncates this table
   and bulk-reloads from source on every click (table is tiny — full reload
   is simpler than MERGE and avoids stale rows after a SIM-side delete).

   Run inside the Azure WMS DB. Idempotent.
   ============================================================================= */

IF OBJECT_ID('dbo.WmsPalletType', 'U') IS NULL
CREATE TABLE dbo.WmsPalletType (
    PalletType             VARCHAR(4)    NOT NULL PRIMARY KEY,
    TypeName               VARCHAR(500)  NULL,
    TrnDate                SMALLDATETIME NULL,
    Reserved               VARCHAR(1)    NULL,
    GroupType              VARCHAR(3)    NULL,
    Exclude                VARCHAR(1)    NULL,
    Remarks                VARCHAR(100)  NULL,
    Export                 VARCHAR(1)    NULL,
    PalletPick             VARCHAR(2)    NULL,
    Report                 VARCHAR(15)   NULL,
    Remarks1               VARCHAR(500)  NULL,
    Season                 VARCHAR(1)    NULL,
    Order1                 INT           NULL,
    toTechno               VARCHAR(1)    NULL,
    BuildCategoryMixAllow  NVARCHAR(1)   NULL,
    PartofHOStock          VARCHAR(1)    NULL,
    ShopEligible           VARCHAR(1)    NULL,
    BlueBox                VARCHAR(1)    NULL,
    DirectProduction       VARCHAR(1)    NULL,
    ShopPalletType         BIT           NULL,
    BuildSelItems          VARCHAR(1)    NULL,
    NonTrade               VARCHAR(1)    NULL,
    ValidateHoStock        VARCHAR(1)    NULL,
    AllowInvalidItem       VARCHAR(1)    NULL,
    RegSIMExclude          VARCHAR(1)    NULL,
    PalletType_Shop        VARCHAR(50)   NULL,
    NegativePurchase       VARCHAR(1)    NULL,
    PalletCategory         VARCHAR(15)   NULL,
    ToWHLocation           VARCHAR(25)   NULL,
    ExcludeFromLPR         VARCHAR(1)    NULL,
    SyncedTS               DATETIME2(0)  NOT NULL CONSTRAINT DF_WPT_SyncedTS DEFAULT(SYSDATETIME())
);

PRINT 'Azure WMS dbo.WmsPalletType ready.';
