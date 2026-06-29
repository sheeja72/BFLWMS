/* =============================================================================
   Phase A.3 — Drop legacy building tables on the Azure WMS DB.

       dbo.WmsPCR                 — replaced by:
                                      dbo.WMS_ContAllocationData (allocation)
                                      dbo.WMSContBuildScanData   (scan ledger)
       dbo.WmsContainerPhotoCheck — photo-qty match step removed from the flow

   THIS IS DESTRUCTIVE — Phase A.1 + A.2 must be deployed first.

   Drop order is safe: nothing else FKs into either table (verified Phase A.1).
   ============================================================================= */

IF OBJECT_ID('dbo.WmsPCR', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.WmsPCR;
    PRINT 'Dropped dbo.WmsPCR.';
END
ELSE
    PRINT 'dbo.WmsPCR already absent.';
GO

IF OBJECT_ID('dbo.WmsContainerPhotoCheck', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.WmsContainerPhotoCheck;
    PRINT 'Dropped dbo.WmsContainerPhotoCheck.';
END
ELSE
    PRINT 'dbo.WmsContainerPhotoCheck already absent.';
GO
