namespace Wms.Data.Lpm;

/// <summary>One row in the "approved containers" picker on the Data Sync page.
/// A container is listed if at least one of its WMS_Cont_Allocation_Header
/// rows has ApprovedDt &gt; NULL. AlreadySynced flips to true once any entry
/// for this ContNo exists in WMS_ContAllocationDataSync_Log — once synced the
/// container is permanently locked from further syncs (per spec Q4).</summary>
public record ApprovedContnoRow(
    string    ContNo,
    int       BatchCount,
    int       TotalAllocatedQty,
    DateTime  LatestApprovedDt,
    bool      AlreadySynced);

/// <summary>One row in the "Recent Sync Activity" table — last N rows from
/// WMS_ContAllocationDataSync_Log, newest first.</summary>
public record DataSyncActivityRow(
    int       SyncId,
    string    ContNo,
    int?      BatchNo,
    string    Destination,
    int?      TotalAllocatedQty,
    string    Status,
    string?   ErrorMessage,
    string?   SyncedBy,
    DateTime  SyncedTS);

/// <summary>Outcome of a sync attempt — returned to the page so it can show
/// a success / error message AND refresh the activity table.</summary>
public record DataSyncResult(
    bool     Ok,
    string?  Message,
    int?     SyncId,
    int      RowsCopied);

/// <summary>The destinations the Data Sync page can write to. AzureWmsDb /
/// WmsProductionDb are user-selectable in the UI for the allocation copy.
/// WmsKnbBoxes is internal — written as part of the same Sync click but
/// always targets dbo.WmsKNBBoxes on Azure WMS.</summary>
public enum DataSyncDestination
{
    AzureWmsDb       = 0,   // bfl-wms Azure SQL — dbo.WMS_ContAllocationData
    WmsProductionDb  = 1,   // On-prem WmsProductionDb — online.dbo.PhotoCheckingResult
    WmsKnbBoxes      = 2    // bfl-wms Azure SQL — dbo.WmsKNBBoxes (auxiliary, runs alongside allocation sync)
}
