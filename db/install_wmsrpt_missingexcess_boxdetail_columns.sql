/* =============================================================================
   Reshape WmsRptMissingExcess_BoxDetail: drop Type + Diff, add MissingQty + ExcessQty.

   Run inside the Azure WMS database. Idempotent.

   Existing rows are wiped — they have to be re-built via the snapshot job
   (Nightly Batches → Backfill) under the new semantics:
     Missing = qty − QtyIssued when Status = '' and QtyIssued < qty
     Excess  = QtyIssued       when Status <> ''
   so each item appears once per box with both contributions.
   ============================================================================= */

DELETE FROM dbo.WmsRptMissingExcess_BoxDetail;

-- Drop legacy columns.
IF COL_LENGTH('dbo.WmsRptMissingExcess_BoxDetail','Type') IS NOT NULL
    ALTER TABLE dbo.WmsRptMissingExcess_BoxDetail DROP COLUMN [Type];

IF COL_LENGTH('dbo.WmsRptMissingExcess_BoxDetail','Diff') IS NOT NULL
    ALTER TABLE dbo.WmsRptMissingExcess_BoxDetail DROP COLUMN Diff;

-- Add new columns.
IF COL_LENGTH('dbo.WmsRptMissingExcess_BoxDetail','MissingQty') IS NULL
    ALTER TABLE dbo.WmsRptMissingExcess_BoxDetail
        ADD MissingQty INT NOT NULL CONSTRAINT DF_WmsRptBoxDetail_MissingQty DEFAULT 0;

IF COL_LENGTH('dbo.WmsRptMissingExcess_BoxDetail','ExcessQty') IS NULL
    ALTER TABLE dbo.WmsRptMissingExcess_BoxDetail
        ADD ExcessQty  INT NOT NULL CONSTRAINT DF_WmsRptBoxDetail_ExcessQty  DEFAULT 0;

PRINT 'WmsRptMissingExcess_BoxDetail reshaped — wiped + (MissingQty, ExcessQty) added.';
