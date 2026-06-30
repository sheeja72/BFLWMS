/* =============================================================================
   Phase 2.A — Add Used column to dbo.WmsBlueToteIDMaster on Azure WMS.

   The ToteID Master Sync inserts new totes with Used = 'N', then sets
   Used = 'Y' for ToteIDs currently held by:
     - UAE  : racks.dbo.whboxitems       (on OnPremBackup)
     - other: <DataName>.dbo.WHboxitemsexport (on OnPremBackup, via 3-part name)

   Idempotent. Run inside the Azure WMS DB before deploying Phase 2.
   ============================================================================= */

IF COL_LENGTH('dbo.WmsBlueToteIDMaster', 'Used') IS NULL
    ALTER TABLE dbo.WmsBlueToteIDMaster ADD Used CHAR(1) NULL;
GO

PRINT 'dbo.WmsBlueToteIDMaster: Used column ready.';
