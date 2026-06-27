/* =============================================================================
   Add Brand + DivCode columns to WMS_ContAllocationData.

   Run inside LPMSIM. Idempotent — re-running is safe.

   Persisting Brand on the detail row lets reports read it directly instead of
   joining usa.USAOrgFile on every load. DivCode lets the load skip the
   vupc_subclass round-trip when computing MerchNeedMonth.

   Legacy batches (processed before this deploy) will have NULL Brand/DivCode
   until re-processed.
   ============================================================================= */

IF COL_LENGTH('dbo.WMS_ContAllocationData','Brand') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Brand   VARCHAR(150) NULL;

IF COL_LENGTH('dbo.WMS_ContAllocationData','DivCode') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD DivCode INT          NULL;

PRINT 'WMS_ContAllocationData.Brand + DivCode ready.';
