/* =============================================================================
   Phase A.1 — Add enrichment + manual-scan columns to dbo.WMS_ContAllocationData
   on the Azure WMS DB.

   New columns:
     - Color, Gender, HsCode                 (from usa..usaorgfile at sync time)
     - Class, Family, Subclass               (from datareporting..vupc_subclass
                                              + datareporting..SubclassMaster at
                                              sync time)
     - Manual         CHAR(1)                'Y' when the row was inserted at
                                              scan-time for an item that was
                                              NOT in the original SIM allocation
                                              (Tier-3 fallback). NULL otherwise.
     - ItemSource     VARCHAR(50)            Source used when Manual='Y'
                                              (e.g. 'usa.upcbarcodes').

   Idempotent — re-runnable. Run inside the Azure WMS DB.
   ============================================================================= */

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Color') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Color VARCHAR(50) NULL;
GO
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Gender') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Gender VARCHAR(20) NULL;
GO
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'HsCode') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD HsCode VARCHAR(50) NULL;
GO
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Class') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD [Class] VARCHAR(150) NULL;
GO
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Family') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Family VARCHAR(150) NULL;
GO
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Subclass') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Subclass VARCHAR(150) NULL;
GO
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Manual') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Manual CHAR(1) NULL;
GO
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'ItemSource') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD ItemSource VARCHAR(50) NULL;
GO

/* Helper index for the scan-time Tier-1/Tier-2 lookup
   (ContNo + Itemcode + ORAPONo, ordered by QtyIssue vs Qty). */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_AzureCAD_ContItemPo'
                 AND object_id = OBJECT_ID('dbo.WMS_ContAllocationData'))
    CREATE INDEX IX_AzureCAD_ContItemPo
        ON dbo.WMS_ContAllocationData (ContNo, Itemcode, ORAPONo)
        INCLUDE (Qty, QtyIssue, StoreID, Division, Result, IdNo);
GO

PRINT 'Azure WMS dbo.WMS_ContAllocationData enrichment columns + lookup index ready.';
