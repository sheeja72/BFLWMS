/* =============================================================================
   Rename dbo.WmsPhotochecking -> dbo.WMSContBuilding on Azure SQL WMS DB.
   Only needed if you already ran install_wms_azuresql_phase2c.sql before the
   table got renamed. Otherwise the install script now creates WMSContBuilding
   directly, so this script is a no-op.

   Run inside the WMS database on bfl-wms-sql.database.windows.net.
   Idempotent.
   ============================================================================= */

IF OBJECT_ID('dbo.WmsPhotochecking','U') IS NOT NULL
   AND OBJECT_ID('dbo.WMSContBuilding','U') IS NULL
BEGIN
    EXEC sp_rename 'dbo.WmsPhotochecking', 'WMSContBuilding';
    PRINT 'Renamed dbo.WmsPhotochecking -> dbo.WMSContBuilding';
END
ELSE
BEGIN
    PRINT 'No rename needed.';
END
