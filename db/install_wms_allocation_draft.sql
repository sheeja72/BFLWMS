/* =============================================================================
   WmsAllocationDraft — Azure SQL WMS database.
   Holds an in-flight allocation snapshot per (Country, ContNo) so the user can
   re-process freely, save a draft, view it, then confirm-and-save into
   LPMSIM.WMS_ContAllocationData later.

   Run inside the WMS database on bfl-wms-sql.database.windows.net.
   Idempotent.
   ============================================================================= */

IF OBJECT_ID('dbo.WmsAllocationDraft','U') IS NULL
CREATE TABLE dbo.WmsAllocationDraft (
    Country   NVARCHAR(20)   NOT NULL,
    ContNo    VARCHAR(50)    NOT NULL,
    DraftJson NVARCHAR(MAX)  NOT NULL,   -- serialised List<AllocationRow>
    RowCount1 INT            NOT NULL,
    TotalQty  INT            NOT NULL,
    SavedTS   DATETIME2(0)   NOT NULL CONSTRAINT DF_WmsAllocationDraft_TS DEFAULT(SYSDATETIME()),
    SavedBy   NVARCHAR(100)  NOT NULL,
    CONSTRAINT PK_WmsAllocationDraft PRIMARY KEY (Country, ContNo)
);

PRINT 'WmsAllocationDraft ready.';
