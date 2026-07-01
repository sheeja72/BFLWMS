/* =============================================================================
   dbo.WmsAppConfig — small key/value config table on Azure WMS DB, for
   runtime-tunable knobs that shouldn't require a redeploy.

   Seed row for the new FillSKUMax+RoundRobin allocation option — the
   "Top 25 stores" cutoff for the Priority Fill pass.

   Idempotent DDL + seed. Run inside the Azure WMS DB.
   ============================================================================= */

IF OBJECT_ID('dbo.WmsAppConfig', 'U') IS NULL
CREATE TABLE dbo.WmsAppConfig (
    ConfigKey   VARCHAR(100)  NOT NULL PRIMARY KEY,
    ConfigValue NVARCHAR(500) NOT NULL,
    UpdatedTS   DATETIME2(0)  NOT NULL
        CONSTRAINT DF_WAC_UpdatedTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
    UpdatedBy   NVARCHAR(100) NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.WmsAppConfig
               WHERE ConfigKey = 'ContainerAlloc.FillSKUMaxRoundRobin.TopN')
    INSERT INTO dbo.WmsAppConfig (ConfigKey, ConfigValue, UpdatedBy)
    VALUES ('ContainerAlloc.FillSKUMaxRoundRobin.TopN', '25', 'system');
GO

PRINT 'Azure WMS dbo.WmsAppConfig ready.';
