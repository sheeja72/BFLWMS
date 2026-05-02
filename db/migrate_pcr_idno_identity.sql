/* =============================================================================
   AIWMS — Migration: convert lpm.dbo.PhotoCheckingResultLPM.IdNO to IDENTITY

   WHAT IT DOES
   ────────────────────────────────────────────────────────────────────────────
   SQL Server cannot ALTER an existing column to IDENTITY in-place. This script
   does the supported equivalent:
     1. Pre-flight checks (table/column exist; not already IDENTITY; no blocking FKs)
     2. Captures existing IdNO values and the table's column DDL from sys.columns
     3. Creates a new table  PhotoCheckingResultLPM_NEW  with IdNO as IDENTITY
     4. Copies all rows with SET IDENTITY_INSERT ON (so existing IdNO values are preserved)
     5. Verifies row count + max(IdNO) match
     6. Captures, drops, then re-creates the table's indexes on the new table
     7. Drops original PhotoCheckingResultLPM, renames _NEW → PhotoCheckingResultLPM
     8. Reseeds IDENTITY to MAX(IdNO)+1
     9. Final verification

   SAFETY
   ────────────────────────────────────────────────────────────────────────────
   • Wrapped in a single transaction with TRY/CATCH and ROLLBACK on any failure
   • Idempotent: re-running after a successful migration is a no-op
   • BACK UP THE DATABASE FIRST. This script will not back up for you.
   • Run during a maintenance window — the table is exclusively locked while it runs.
   • Other apps inserting/updating PCR will block until commit.

   LIMITATIONS — review before running
   ────────────────────────────────────────────────────────────────────────────
   • Aborts if any FOREIGN KEYS reference dbo.PhotoCheckingResultLPM(IdNO).
     If found, drop them, run this, then recreate them.
   • Captures indexes (CREATE [UNIQUE] [CLUSTERED|NONCLUSTERED] INDEX … with INCLUDE
     and basic filter). Does NOT capture: triggers, computed columns, full-text
     indexes, partition schemes, statistics, extended properties. Re-create those
     manually if applicable.
   • PK / UNIQUE constraints created via CREATE INDEX are handled. PK constraints
     created via ALTER TABLE … ADD CONSTRAINT are also recreated.
   ============================================================================= */

USE lpm;
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @TblSchema  SYSNAME = 'dbo';
DECLARE @TblName    SYSNAME = 'PhotoCheckingResultLPM';
DECLARE @IdCol      SYSNAME = 'IdNO';
DECLARE @TmpName    SYSNAME = 'PhotoCheckingResultLPM_NEW';
DECLARE @FullName   NVARCHAR(300) = QUOTENAME(@TblSchema)+'.'+QUOTENAME(@TblName);
DECLARE @TmpFull    NVARCHAR(300) = QUOTENAME(@TblSchema)+'.'+QUOTENAME(@TmpName);
DECLARE @ObjId      INT = OBJECT_ID(@FullName);

PRINT '==============================================================';
PRINT 'PCR IdNO → IDENTITY migration — ' + CONVERT(VARCHAR(30), SYSDATETIME(), 121);
PRINT '==============================================================';

/* ------------------ Step 1: pre-flight ------------------ */
IF @ObjId IS NULL
BEGIN
    RAISERROR('Table %s not found. Aborting.', 16, 1, @FullName);
    RETURN;
END;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @ObjId AND name = @IdCol)
BEGIN
    RAISERROR('Column %s not found in %s. Aborting.', 16, 1, @IdCol, @FullName);
    RETURN;
END;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @ObjId AND name = @IdCol AND is_identity = 1)
BEGIN
    PRINT 'Column ' + @IdCol + ' is already IDENTITY in ' + @FullName + '. Nothing to do. Exiting.';
    RETURN;
END;

DECLARE @fkCount INT = (
    SELECT COUNT(*) FROM sys.foreign_keys WHERE referenced_object_id = @ObjId);
IF @fkCount > 0
BEGIN
    PRINT 'FOREIGN KEYS referencing this table:';
    SELECT
        fk.name        AS fk_name,
        OBJECT_NAME(fk.parent_object_id) AS parent_table,
        OBJECT_SCHEMA_NAME(fk.parent_object_id) AS parent_schema
    FROM sys.foreign_keys fk
    WHERE fk.referenced_object_id = @ObjId;
    RAISERROR('Drop the FKs above first, run this, then recreate them. Aborting.', 16, 1);
    RETURN;
END;

DECLARE @rowCountBefore BIGINT = (SELECT COUNT_BIG(*) FROM lpm.dbo.PhotoCheckingResultLPM);
DECLARE @maxIdBefore   BIGINT;
SELECT @maxIdBefore = ISNULL(MAX(IdNO), 0) FROM lpm.dbo.PhotoCheckingResultLPM;

PRINT 'Pre-flight OK.';
PRINT '  Row count   : ' + CAST(@rowCountBefore AS VARCHAR(50));
PRINT '  MAX(IdNO)   : ' + CAST(@maxIdBefore   AS VARCHAR(50));
PRINT '  Next IDENTITY seed will be: ' + CAST(@maxIdBefore + 1 AS VARCHAR(50));

/* ------------------ Step 2: capture column DDL ------------------ */
DECLARE @colList NVARCHAR(MAX), @colDefs NVARCHAR(MAX);

;WITH cols AS (
    SELECT
        c.column_id,
        c.name,
        ty = TYPE_NAME(c.user_type_id),
        c.max_length,
        c.precision,
        c.scale,
        c.is_nullable,
        c.collation_name,
        is_target = CASE WHEN c.name = @IdCol THEN 1 ELSE 0 END
    FROM sys.columns c
    WHERE c.object_id = @ObjId
)
SELECT
    @colList = STRING_AGG(QUOTENAME(name), ', ') WITHIN GROUP (ORDER BY column_id),
    @colDefs = STRING_AGG(
        CAST(
            QUOTENAME(name) + ' '
            + CASE WHEN is_target = 1
                   THEN 'BIGINT IDENTITY(' + CAST(@maxIdBefore + 1 AS VARCHAR(20)) + ',1) NOT NULL'
                   ELSE
                       ty +
                       CASE
                           WHEN ty IN ('varchar','char')
                                THEN '(' + IIF(max_length = -1, 'MAX', CAST(max_length AS VARCHAR(10))) + ')'
                           WHEN ty IN ('nvarchar','nchar')
                                THEN '(' + IIF(max_length = -1, 'MAX', CAST(max_length/2 AS VARCHAR(10))) + ')'
                           WHEN ty IN ('decimal','numeric')
                                THEN '(' + CAST(precision AS VARCHAR(10)) + ',' + CAST(scale AS VARCHAR(10)) + ')'
                           WHEN ty IN ('datetime2','time','datetimeoffset')
                                THEN '(' + CAST(scale AS VARCHAR(10)) + ')'
                           ELSE ''
                       END
                       + CASE WHEN ty IN ('varchar','char','nvarchar','nchar','text','ntext') AND collation_name IS NOT NULL
                              THEN ' COLLATE ' + collation_name ELSE '' END
                       + CASE WHEN is_nullable = 0 THEN ' NOT NULL' ELSE ' NULL' END
              END
            AS NVARCHAR(MAX)),
        ', ') WITHIN GROUP (ORDER BY column_id)
FROM cols;

DECLARE @createSql NVARCHAR(MAX) =
    'CREATE TABLE ' + @TmpFull + ' (' + @colDefs + ');';

PRINT '';
PRINT 'New table DDL:';
PRINT @createSql;

/* ------------------ Step 3: capture indexes for re-creation ------------------ */
IF OBJECT_ID('tempdb..#idx_recreate') IS NOT NULL DROP TABLE #idx_recreate;
CREATE TABLE #idx_recreate (seq INT IDENTITY(1,1) PRIMARY KEY, sql NVARCHAR(MAX));

;WITH ix AS (
    SELECT
        i.name AS ix_name,
        i.is_unique,
        i.is_primary_key,
        i.type_desc,
        i.has_filter,
        i.filter_definition,
        i.index_id
    FROM sys.indexes i
    WHERE i.object_id = @ObjId
      AND i.index_id > 0           -- exclude heaps
      AND i.is_hypothetical = 0
)
INSERT INTO #idx_recreate (sql)
SELECT
    CASE
        WHEN ix.is_primary_key = 1 THEN
            'ALTER TABLE ' + @TmpFull + ' ADD CONSTRAINT ' + QUOTENAME(ix.ix_name)
            + ' PRIMARY KEY ' + CASE WHEN ix.type_desc = 'CLUSTERED' THEN 'CLUSTERED' ELSE 'NONCLUSTERED' END
            + ' (' + STUFF((SELECT ', ' + QUOTENAME(c.name)
                              + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                            FROM sys.index_columns ic
                            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                            WHERE ic.object_id = @ObjId AND ic.index_id = ix.index_id AND ic.is_included_column = 0
                            ORDER BY ic.key_ordinal
                            FOR XML PATH(''), TYPE).value('.','NVARCHAR(MAX)'), 1, 2, '')
            + ');'
        ELSE
            'CREATE ' + CASE WHEN ix.is_unique = 1 THEN 'UNIQUE ' ELSE '' END
            + CASE WHEN ix.type_desc = 'CLUSTERED' THEN 'CLUSTERED ' ELSE 'NONCLUSTERED ' END
            + 'INDEX ' + QUOTENAME(ix.ix_name) + ' ON ' + @TmpFull
            + ' (' + STUFF((SELECT ', ' + QUOTENAME(c.name)
                              + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                            FROM sys.index_columns ic
                            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                            WHERE ic.object_id = @ObjId AND ic.index_id = ix.index_id AND ic.is_included_column = 0
                            ORDER BY ic.key_ordinal
                            FOR XML PATH(''), TYPE).value('.','NVARCHAR(MAX)'), 1, 2, '')
            + ')'
            + ISNULL(' INCLUDE ('
                  + STUFF((SELECT ', ' + QUOTENAME(c.name)
                            FROM sys.index_columns ic
                            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                            WHERE ic.object_id = @ObjId AND ic.index_id = ix.index_id AND ic.is_included_column = 1
                            FOR XML PATH(''), TYPE).value('.','NVARCHAR(MAX)'), 1, 2, '')
                  + ')', '')
            + CASE WHEN ix.has_filter = 1 THEN ' WHERE ' + ix.filter_definition ELSE '' END
            + ';'
    END
FROM ix
WHERE EXISTS (SELECT 1 FROM sys.index_columns ic
              WHERE ic.object_id = @ObjId AND ic.index_id = ix.index_id AND ic.is_included_column = 0);

PRINT '';
PRINT 'Indexes to recreate:';
SELECT seq, sql FROM #idx_recreate ORDER BY seq;

/* ------------------ Step 4: do the migration ------------------ */
BEGIN TRY
    BEGIN TRAN PCR_IDENTITY_MIGRATION;

    PRINT '';
    PRINT '→ Creating new table...';
    EXEC sp_executesql @createSql;

    PRINT '→ Copying rows with SET IDENTITY_INSERT ON...';
    DECLARE @copy NVARCHAR(MAX) =
        'SET IDENTITY_INSERT ' + @TmpFull + ' ON;
         INSERT INTO ' + @TmpFull + ' (' + @colList + ')
         SELECT ' + @colList + ' FROM ' + @FullName + ';
         SET IDENTITY_INSERT ' + @TmpFull + ' OFF;';
    EXEC sp_executesql @copy;

    DECLARE @rowCountAfter BIGINT, @maxIdAfter BIGINT;
    SELECT @rowCountAfter = COUNT_BIG(*), @maxIdAfter = ISNULL(MAX(IdNO), 0)
    FROM lpm.dbo.PhotoCheckingResultLPM_NEW;

    PRINT '→ Verifying...';
    PRINT '  Original rows : ' + CAST(@rowCountBefore AS VARCHAR(50));
    PRINT '  New rows      : ' + CAST(@rowCountAfter  AS VARCHAR(50));
    PRINT '  Original max  : ' + CAST(@maxIdBefore    AS VARCHAR(50));
    PRINT '  New max       : ' + CAST(@maxIdAfter     AS VARCHAR(50));

    IF @rowCountAfter <> @rowCountBefore OR @maxIdAfter <> @maxIdBefore
    BEGIN
        RAISERROR('Verification FAILED — counts/max don''t match. Rolling back.', 16, 1);
    END;

    PRINT '→ Dropping original table...';
    DECLARE @drop NVARCHAR(MAX) = 'DROP TABLE ' + @FullName + ';';
    EXEC sp_executesql @drop;

    PRINT '→ Renaming new → original...';
    EXEC sp_rename 'lpm.dbo.PhotoCheckingResultLPM_NEW', 'PhotoCheckingResultLPM';

    PRINT '→ Recreating indexes...';
    DECLARE @ixSeq INT = 1, @ixSql NVARCHAR(MAX), @maxSeq INT = (SELECT MAX(seq) FROM #idx_recreate);
    WHILE @ixSeq <= ISNULL(@maxSeq, 0)
    BEGIN
        SELECT @ixSql = sql FROM #idx_recreate WHERE seq = @ixSeq;
        SET @ixSql = REPLACE(@ixSql, @TmpFull, @FullName);   -- back to original name
        PRINT '   ' + @ixSql;
        EXEC sp_executesql @ixSql;
        SET @ixSeq = @ixSeq + 1;
    END;

    PRINT '→ Reseeding IDENTITY (DBCC CHECKIDENT)...';
    DECLARE @reseed NVARCHAR(MAX) = 'DBCC CHECKIDENT(''' + @FullName + ''', RESEED);';
    EXEC sp_executesql @reseed;

    COMMIT TRAN PCR_IDENTITY_MIGRATION;

    PRINT '';
    PRINT '✓ Migration committed successfully.';
    PRINT '  IdNO is now IDENTITY. Next inserted row will get IdNO = ' + CAST(@maxIdBefore + 1 AS VARCHAR(50));

    /* Final sanity */
    SELECT
        is_identity, seed_value = IDENT_SEED(@FullName), increment = IDENT_INCR(@FullName), current = IDENT_CURRENT(@FullName)
    FROM sys.columns WHERE object_id = OBJECT_ID(@FullName) AND name = @IdCol;
END TRY
BEGIN CATCH
    DECLARE @errMsg NVARCHAR(4000) = ERROR_MESSAGE(),
            @errSev INT            = ERROR_SEVERITY(),
            @errSta INT            = ERROR_STATE();
    IF XACT_STATE() <> 0 ROLLBACK TRAN PCR_IDENTITY_MIGRATION;
    /* Cleanup the staging table if it was created and we rolled back */
    IF OBJECT_ID(@TmpFull,'U') IS NOT NULL
    BEGIN
        DECLARE @cleanup NVARCHAR(MAX) = 'DROP TABLE ' + @TmpFull + ';';
        EXEC sp_executesql @cleanup;
    END;

    PRINT '';
    PRINT '✗ Migration FAILED — rolled back. Original table untouched.';
    RAISERROR(@errMsg, @errSev, @errSta);
END CATCH;
GO
