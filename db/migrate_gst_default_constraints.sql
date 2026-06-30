/* =============================================================================
   Switch the auto-stamp DEFAULTs on Azure WMS from server time (SYSDATETIME,
   which is UTC on Azure SQL) to Gulf Standard Time (UTC+4, no DST).

   Tables affected (and their DEFAULT constraints):
     - dbo.WMSContBuildScanData          (ScannedTS) — DF_BSD_ScannedTS
     - dbo.WMS_ContAllocationDataSync_Log (SyncedTS)  — DF_CADSL_TS
     - dbo.WMS_DataSettings               (SyncedTS)  — DF_WDS_SyncedTS
     - dbo.WmsPalletType                  (SyncedTS)  — DF_WPT_SyncedTS

   Existing rows are NOT modified — they keep their UTC value. Only new rows
   (where the INSERT omits the column and the DEFAULT fires) get GST.

   The C# services have also been updated to inline DATEADD(hour, 4,
   SYSUTCDATETIME()) wherever they explicitly write SYSDATETIME() /
   GETDATE() so explicit-stamp INSERTs land in GST too.

   Idempotent. Run inside the Azure WMS DB.
   ============================================================================= */

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_BSD_ScannedTS'
           AND parent_object_id = OBJECT_ID('dbo.WMSContBuildScanData'))
    ALTER TABLE dbo.WMSContBuildScanData DROP CONSTRAINT DF_BSD_ScannedTS;
GO
ALTER TABLE dbo.WMSContBuildScanData
    ADD CONSTRAINT DF_BSD_ScannedTS
    DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())) FOR ScannedTS;
GO

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_CADSL_TS'
           AND parent_object_id = OBJECT_ID('dbo.WMS_ContAllocationDataSync_Log'))
    ALTER TABLE dbo.WMS_ContAllocationDataSync_Log DROP CONSTRAINT DF_CADSL_TS;
GO
ALTER TABLE dbo.WMS_ContAllocationDataSync_Log
    ADD CONSTRAINT DF_CADSL_TS
    DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())) FOR SyncedTS;
GO

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_WDS_SyncedTS'
           AND parent_object_id = OBJECT_ID('dbo.WMS_DataSettings'))
    ALTER TABLE dbo.WMS_DataSettings DROP CONSTRAINT DF_WDS_SyncedTS;
GO
ALTER TABLE dbo.WMS_DataSettings
    ADD CONSTRAINT DF_WDS_SyncedTS
    DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())) FOR SyncedTS;
GO

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_WPT_SyncedTS'
           AND parent_object_id = OBJECT_ID('dbo.WmsPalletType'))
    ALTER TABLE dbo.WmsPalletType DROP CONSTRAINT DF_WPT_SyncedTS;
GO
ALTER TABLE dbo.WmsPalletType
    ADD CONSTRAINT DF_WPT_SyncedTS
    DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())) FOR SyncedTS;
GO

PRINT 'GST defaults applied to WMSContBuildScanData / WMS_ContAllocationDataSync_Log / WMS_DataSettings / WmsPalletType.';
