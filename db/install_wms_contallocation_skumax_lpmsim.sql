/* =============================================================================
   Add SkuMax column to WMS_ContAllocationData.

   Run inside LPMSIM. Idempotent — re-running is safe.

   SkuMax was being computed during Process but never persisted on the detail
   row, so re-loading a batch always showed 0 in the Item x Store view. Adding
   the column lets the load path round-trip it.
   ============================================================================= */

IF COL_LENGTH('dbo.WMS_ContAllocationData','SkuMax') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD SkuMax INT NULL;

PRINT 'WMS_ContAllocationData.SkuMax ready.';
