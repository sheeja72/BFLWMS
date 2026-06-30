/* =============================================================================
   Create dbo.WmsLogisticsBoxClosure_Log on the Azure WMS DB.

   Audit ledger for the "Close Logistics" button on LPM Manual Building.
   One row per operator click that closes a logistics-box label (e.g.
   AELOC6928-487476/025/026). The actual closure side-effect is
       UPDATE dbo.WmsKNBBoxes SET closed = 'Y'
        WHERE Country = ... AND Contno = ... AND Boxno = ...
   This log keeps the audit trail.

   Default timestamp is GST (UTC+4, no DST).

   Idempotent. Run inside the Azure WMS DB.
   ============================================================================= */

IF OBJECT_ID('dbo.WmsLogisticsBoxClosure_Log', 'U') IS NULL
CREATE TABLE dbo.WmsLogisticsBoxClosure_Log (
    ClosureId   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Country     NVARCHAR(20)  NOT NULL,
    ContNo      VARCHAR(15)   NOT NULL,
    Boxno       VARCHAR(50)   NOT NULL,   -- the WmsKNBBoxes.Boxno label
    PcsScanned  INT           NULL,
    ClosedBy    NVARCHAR(100) NOT NULL,
    ClosedTS    DATETIME2(0)  NOT NULL
        CONSTRAINT DF_WLBCL_TS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME()))
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_WLBCL_ContBox'
                 AND object_id = OBJECT_ID('dbo.WmsLogisticsBoxClosure_Log'))
    CREATE INDEX IX_WLBCL_ContBox
        ON dbo.WmsLogisticsBoxClosure_Log (Country, ContNo, Boxno)
        INCLUDE (ClosedTS, ClosedBy);
GO

PRINT 'Azure WMS dbo.WmsLogisticsBoxClosure_Log ready.';
