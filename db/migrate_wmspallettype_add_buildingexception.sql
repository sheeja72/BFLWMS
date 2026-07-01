/* =============================================================================
   Add BuildingException column to dbo.WmsPalletType on the Azure WMS DB.

   Source column already exists on bfldata.dbo.pallettype (per user 2026-07-01).
   Values are 'Y' when the PalletType is available in the Manual Building
   "Exception" dropdown, NULL otherwise. Populated by the PalletType Master
   sync, which TRUNCATE-and-reloads from source.

   Idempotent. Run inside the Azure WMS DB.
   ============================================================================= */

IF COL_LENGTH('dbo.WmsPalletType', 'BuildingException') IS NULL
    ALTER TABLE dbo.WmsPalletType ADD BuildingException CHAR(1) NULL;
GO

PRINT 'dbo.WmsPalletType.BuildingException ready.';
