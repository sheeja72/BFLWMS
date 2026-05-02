/* =============================================================================
   AIWMS — Column existence QA
   Checks every column reference in BuildingService.cs against the actual schemas
   on the connected SQL server. Run this against the server configured in /setup.

   For each row:
       status  = 'OK'      → column exists by that exact name (case-insensitive default collation)
       status  = 'MISSING' → column not found, query will fail at runtime
       status  = '— DB MISSING' → the database itself isn't on this server
   ============================================================================= */

SET NOCOUNT ON;

DECLARE @refs TABLE (
    db_name      SYSNAME,
    schema_name  SYSNAME,
    object_name  SYSNAME,
    column_name  SYSNAME,
    used_for     NVARCHAR(200)
);

INSERT INTO @refs(db_name, schema_name, object_name, column_name, used_for) VALUES

/* bfldata */
('bfldata','dbo','buildingcompletion','Contno',     'block container if already built'),
('bfldata','dbo','contreceipt','refno',             'require receipt'),
('bfldata','dbo','BlueToteIDMaster','ToteID',       'tote validity'),
('bfldata','dbo','DataSettings','Country',          'country dropdown'),
('bfldata','dbo','DataSettings','ActiveStore',      'country dropdown filter'),

/* usa */
('usa','dbo','OpenUSACont','contno',                'container open status'),
('usa','dbo','OpenUSACont','closed',                'container open status'),
('usa','dbo','KNBBoxes','contno',                   'physical-box validation'),
('usa','dbo','KNBBoxes','boxno',                    'physical-box validation'),
('usa','dbo','KNBBoxes','closed',                   'physical-box validation'),
('usa','dbo','USAOrgFile','contno',                 'item lookup, photo-qty SUM'),
('usa','dbo','USAOrgFile','OraPONo',                'PO match validation'),
('usa','dbo','USAOrgFile','itemcode',               'item attribute lookup'),
('usa','dbo','USAOrgFile','orgqty',                 'manifest qty SUM'),
('usa','dbo','USAOrgFile','itemname',               'item Description'),
('usa','dbo','USAOrgFile','style',                  'item Style'),
('usa','dbo','USAOrgFile','size',                   'item Size'),
('usa','dbo','USAOrgFile','color',                  'item Color'),
('usa','dbo','USAOrgFile','vendor',                 'item Brand (=vendor)'),
('usa','dbo','USAOrgFile','season',                 'item Season'),
('usa','dbo','USAOrgFile','gender',                 'item Gender'),
('usa','dbo','USAOrgFile','hscode',                 'item HS Code'),
('usa','dbo','USAOrgFile','lpm',                    'item LPM'),
('usa','dbo','USAOrgFile','groupcode',              'item Group code'),
('usa','dbo','UPCbarcodes','itemcode',              'item-master fallback'),

/* hodata */
('hodata','dbo','itemgroup','groupcode',            'group-name lookup'),
('hodata','dbo','itemgroup','Description',          'group-name display'),

/* datareporting */
('datareporting','dbo','upc_subclass','itemcode',   'MH4ID lookup'),
('datareporting','dbo','upc_subclass','MH4ID',      'MH4 hierarchy'),
('datareporting','dbo','SubclassMaster','MH4ID',    'MH4 hierarchy join'),
('datareporting','dbo','SubclassMaster','Division',  'MH4 Division'),
('datareporting','dbo','SubclassMaster','Department','MH4 Department'),
('datareporting','dbo','SubclassMaster','Class',     'MH4 Class'),
('datareporting','dbo','SubclassMaster','Family',    'MH4 Family'),
('datareporting','dbo','SubclassMaster','Subclass',  'MH4 Subclass'),

/* lpm.PhotoCheckingResultLPM (read+write) */
('lpm','dbo','PhotoCheckingResultLPM','IdNO',       'tier-1 update target'),
('lpm','dbo','PhotoCheckingResultLPM','Contno',     'allocation key'),
('lpm','dbo','PhotoCheckingResultLPM','Itemcode',   'allocation key'),
('lpm','dbo','PhotoCheckingResultLPM','OraPoNO',    'allocation key (PO)'),
('lpm','dbo','PhotoCheckingResultLPM','LPMDT',      'allocation order/return'),
('lpm','dbo','PhotoCheckingResultLPM','Result',     'SHOP/etc allocation result'),
('lpm','dbo','PhotoCheckingResultLPM','ResultType', 'box grouping (was PalletType)'),
('lpm','dbo','PhotoCheckingResultLPM','QtyIssue',   'tier-1 increment'),
('lpm','dbo','PhotoCheckingResultLPM','Style',      'tier-3 fallback'),
('lpm','dbo','PhotoCheckingResultLPM','BoxNo',      'box # stamp on checkout'),
('lpm','dbo','PhotoCheckingResultLPM','qty',        'photo-qty SUM'),

/* lpm.UPCBoxHeadLPM (write at checkout) */
('lpm','dbo','UPCBoxHeadLPM','BoxNo',           'pk'),
('lpm','dbo','UPCBoxHeadLPM','TrnDate',         'date'),
('lpm','dbo','UPCBoxHeadLPM','Time1',           'time'),
('lpm','dbo','UPCBoxHeadLPM','NewPallet',       'hardcoded Y'),
('lpm','dbo','UPCBoxHeadLPM','PreparedBy',      'checkout user'),
('lpm','dbo','UPCBoxHeadLPM','Remarks',         'hardcoded "from AIWMS"'),
('lpm','dbo','UPCBoxHeadLPM','Userid',          'checkin user'),
('lpm','dbo','UPCBoxHeadLPM','PalletType',      'from staging'),
('lpm','dbo','UPCBoxHeadLPM','Closed',          'hardcoded N'),
('lpm','dbo','UPCBoxHeadLPM','GroupCode',       'empty'),
('lpm','dbo','UPCBoxHeadLPM','OldBoxNo',        'empty'),
('lpm','dbo','UPCBoxHeadLPM','Prepared1',       'checkout user'),
('lpm','dbo','UPCBoxHeadLPM','Prepared2',       'Division (repurposed)'),
('lpm','dbo','UPCBoxHeadLPM','WHouse',          'user.Warehouse'),
('lpm','dbo','UPCBoxHeadLPM','FWType',          'empty'),
('lpm','dbo','UPCBoxHeadLPM','FPreparedBy',     'checkout user'),
('lpm','dbo','UPCBoxHeadLPM','FPalletType',     '=PalletType'),
('lpm','dbo','UPCBoxHeadLPM','ISize',           'empty'),
('lpm','dbo','UPCBoxHeadLPM','Gender',          'empty'),
('lpm','dbo','UPCBoxHeadLPM','ToteID',          'staged tote'),
('lpm','dbo','UPCBoxHeadLPM','LPMDT',           'staged LPMDt'),

/* lpm.UPCBoxDetLPM */
('lpm','dbo','UPCBoxDetLPM','BoxNo',            'header link'),
('lpm','dbo','UPCBoxDetLPM','Itemcode',         'item'),
('lpm','dbo','UPCBoxDetLPM','Qty',              'aggregated qty'),
('lpm','dbo','UPCBoxDetLPM','QtyIssued',        'hardcoded 0'),
('lpm','dbo','UPCBoxDetLPM','SrNo',             'per-box sequence'),
('lpm','dbo','UPCBoxDetLPM','Status',           'empty'),
('lpm','dbo','UPCBoxDetLPM','UPC',              '=Itemcode'),
('lpm','dbo','UPCBoxDetLPM','imgfile',          '=Contno'),

/* lpm.PhotocheckingLPM (one row per scan) */
('lpm','dbo','PhotocheckingLPM','ContNo',           'container'),
('lpm','dbo','PhotocheckingLPM','TrnDate',          'date'),
('lpm','dbo','PhotocheckingLPM','Time1',            'time'),
('lpm','dbo','PhotocheckingLPM','UPC',              '=Itemcode'),
('lpm','dbo','PhotocheckingLPM','PhotoSize',        '=size'),
('lpm','dbo','PhotocheckingLPM','Result',           'allocation result'),
('lpm','dbo','PhotocheckingLPM','CheckedBy',        'user name'),
('lpm','dbo','PhotocheckingLPM','CmpName',          'PC name (reverse-DNS)'),
('lpm','dbo','PhotocheckingLPM','BoxSize',          '=size'),
('lpm','dbo','PhotocheckingLPM','Photo',            'empty'),
('lpm','dbo','PhotocheckingLPM','Style',            'item style'),
('lpm','dbo','PhotocheckingLPM','Color',            'item color'),
('lpm','dbo','PhotocheckingLPM','GroupCode',        'item group'),
('lpm','dbo','PhotocheckingLPM','ItemName',         'empty'),
('lpm','dbo','PhotocheckingLPM','Warehouse',        'user.Warehouse'),
('lpm','dbo','PhotocheckingLPM','PhotoCheckType',   'empty'),
('lpm','dbo','PhotocheckingLPM','RRP',              'hardcoded 0'),
('lpm','dbo','PhotocheckingLPM','Logistics_BoxNo',  'supplier box scanned'),
('lpm','dbo','PhotocheckingLPM','Season',           'item season'),
('lpm','dbo','PhotocheckingLPM','ToteID',           'staged tote'),
('lpm','dbo','PhotocheckingLPM','RoboStatus',       'hardcoded N'),
('lpm','dbo','PhotocheckingLPM','BarCode',          'empty');

DECLARE @sql NVARCHAR(MAX) = N'';

SELECT @sql = STRING_AGG(CAST(
    'SELECT ' + QUOTENAME(db_name,'''') + ' AS db_name, '
              + QUOTENAME(object_name,'''') + ' AS object_name, '
              + QUOTENAME(column_name,'''') + ' AS column_name, '
              + QUOTENAME(used_for,'''') + ' AS used_for, '
              + 'CASE WHEN DB_ID(' + QUOTENAME(db_name,'''') + ') IS NULL THEN ''— DB MISSING''
                       WHEN COL_LENGTH(' + QUOTENAME(db_name + '.' + schema_name + '.' + object_name,'''')
                       + ',' + QUOTENAME(column_name,'''') + ') IS NOT NULL THEN ''OK''
                       ELSE ''MISSING'' END AS status'
    AS NVARCHAR(MAX)), ' UNION ALL ')
FROM @refs;

EXEC sp_executesql @sql;
