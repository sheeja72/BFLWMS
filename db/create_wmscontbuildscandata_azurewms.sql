/* =============================================================================
   Phase A.2 — Create dbo.WMSContBuildScanData on the Azure WMS DB.

   The scan ledger: one row per piece scanned in Manual Building.
   Replaces dbo.WmsPCR (dropped in Phase A.3) as the audit trail of which
   physical pieces were placed in which box / tote, and which allocation row
   they consumed.

   One scan = one row. WMS_ContAllocationData.QtyIssue is incremented in the
   same transaction; the cap is QtyIssue < Qty. Round-robin / OTS picks insert
   NEW rows into WMS_ContAllocationData with Qty=1 + QtyIssue=1.

   Idempotent. Run inside the Azure WMS DB.
   ============================================================================= */

IF OBJECT_ID('dbo.WMSContBuildScanData', 'U') IS NULL
CREATE TABLE dbo.WMSContBuildScanData (
    ScanId          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Country         VARCHAR(20)  NOT NULL,
    ContNo          VARCHAR(15)  NOT NULL,
    Itemcode        VARCHAR(15)  NOT NULL,
    StoreID         VARCHAR(15)  NULL,         -- store the allocation row resolved to
    Result          VARCHAR(100) NULL,         -- mirror of the allocation row's Result at scan time
    Division        VARCHAR(150) NULL,         -- mirror at scan time, used by OTS recompute lookup
    BoxNo           VARCHAR(50)  NOT NULL,     -- WmsOpenBox.BoxNo this piece was placed in
    ToteID          VARCHAR(50)  NULL,         -- ToteID attached to the box at scan time (may be NULL until check-in)
    AllocationIdNo  INT          NOT NULL,     -- FK to dbo.WMS_ContAllocationData.IdNo (no enforced FK — cross-restore safety)
    Tier            TINYINT      NOT NULL,     -- 1 = Tier-1 hit, 2 = OTS overflow insert, 3 = manual / new item
    Manual          CHAR(1)      NULL,         -- 'Y' only when Tier=3
    Reversed        CHAR(1)      NOT NULL CONSTRAINT DF_BSD_Reversed DEFAULT('N'),
    ReversedTS      DATETIME2(0) NULL,
    ReversedBy      NVARCHAR(100) NULL,
    ScannedBy       NVARCHAR(100) NOT NULL,
    ScannedTS       DATETIME2(0) NOT NULL CONSTRAINT DF_BSD_ScannedTS DEFAULT(SYSDATETIME())
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_BSD_ContItem'
                 AND object_id = OBJECT_ID('dbo.WMSContBuildScanData'))
    CREATE INDEX IX_BSD_ContItem
        ON dbo.WMSContBuildScanData (ContNo, Itemcode)
        INCLUDE (StoreID, Division, Tier, Reversed);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_BSD_Box'
                 AND object_id = OBJECT_ID('dbo.WMSContBuildScanData'))
    CREATE INDEX IX_BSD_Box
        ON dbo.WMSContBuildScanData (BoxNo)
        INCLUDE (Itemcode, AllocationIdNo, Reversed);
GO

/* For OTS-recompute scans:
       SUM(scans) per (ContNo, StoreID, Division)
   filtered by Reversed = 'N'. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_BSD_OtsLookup'
                 AND object_id = OBJECT_ID('dbo.WMSContBuildScanData'))
    CREATE INDEX IX_BSD_OtsLookup
        ON dbo.WMSContBuildScanData (ContNo, StoreID, Division)
        INCLUDE (Reversed);
GO

PRINT 'Azure WMS dbo.WMSContBuildScanData (scan ledger) ready.';
