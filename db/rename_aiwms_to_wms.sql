/* =============================================================================
   Rename Aiwms* tables to Wms* (drop the "Ai" prefix).
   Idempotent: skips tables that are already renamed.

   Run inside the WMS database on bfl-wms-sql.database.windows.net.
   Renames are metadata-only — data, indexes, constraints, FKs, and the
   Managed Identity grant all carry over automatically.

   AppConfig (no Aiwms prefix) is left alone.
   ============================================================================= */

IF OBJECT_ID('dbo.AiwmsAuditLog','U') IS NOT NULL
    EXEC sp_rename 'dbo.AiwmsAuditLog', 'WmsAuditLog';

IF OBJECT_ID('dbo.AiwmsBoxSequence','U') IS NOT NULL
    EXEC sp_rename 'dbo.AiwmsBoxSequence', 'WmsBoxSequence';

IF OBJECT_ID('dbo.AiwmsContainerPhotoCheck','U') IS NOT NULL
    EXEC sp_rename 'dbo.AiwmsContainerPhotoCheck', 'WmsContainerPhotoCheck';

IF OBJECT_ID('dbo.AiwmsOpenBox','U') IS NOT NULL
    EXEC sp_rename 'dbo.AiwmsOpenBox', 'WmsOpenBox';

IF OBJECT_ID('dbo.AiwmsOpenBoxItem','U') IS NOT NULL
    EXEC sp_rename 'dbo.AiwmsOpenBoxItem', 'WmsOpenBoxItem';

IF OBJECT_ID('dbo.AiwmsOpenBoxScan','U') IS NOT NULL
    EXEC sp_rename 'dbo.AiwmsOpenBoxScan', 'WmsOpenBoxScan';

IF OBJECT_ID('dbo.AiwmsRole','U') IS NOT NULL
    EXEC sp_rename 'dbo.AiwmsRole', 'WmsRole';

IF OBJECT_ID('dbo.AiwmsUser','U') IS NOT NULL
    EXEC sp_rename 'dbo.AiwmsUser', 'WmsUser';

IF OBJECT_ID('dbo.AiwmsUserRole','U') IS NOT NULL
    EXEC sp_rename 'dbo.AiwmsUserRole', 'WmsUserRole';

IF OBJECT_ID('dbo.AiwmsWHMaster','U') IS NOT NULL
    EXEC sp_rename 'dbo.AiwmsWHMaster', 'WmsWHMaster';

PRINT 'Rename complete. 10 tables renamed: AiwmsAuditLog/BoxSequence/ContainerPhotoCheck/OpenBox/OpenBoxItem/OpenBoxScan/Role/User/UserRole/WHMaster -> Wms*';
