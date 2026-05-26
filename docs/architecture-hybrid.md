# AIWMS Hybrid Architecture — Design Document

| Field | Value |
|---|---|
| **Status** | DRAFT — pending review |
| **Last updated** | (auto: when committed) |
| **Replaces** | The single-MSSQL on-prem architecture currently running on BFLITH (192.168.10.4) |
| **DB engine choice** | Azure SQL Database (NOT PostgreSQL — see Section 13 #1 for why) |
| **`lpm` → `AIWMS` strategy** | Hard rename (Option A) — see Section 9 for breaking-change impact |

---

## 1. Executive summary

AIWMS moves from a **single on-prem deployment per country (7 EXE copies, one shared MSSQL per country)** to a **single Azure-hosted Blazor Server app, multi-tenant by country, backed by a hybrid Azure SQL Database + per-country on-prem MSSQL data layer.**

The goal is to **simplify deployment, reduce on-prem DB load, and unify user/auth/admin across all 7 countries**, while keeping legacy heavy operations (Container Process, Store Distribution) where they already work efficiently — on each country's on-prem MSSQL.

The web app, identity, audit, item-master cache, and the LPM Manual Building flow run against **Azure SQL Database**. Container Process continues on-prem and writes a denormalized result to Azure SQL via background sync. Checkout writes (UPCBoxHeadLPM / UPCBoxDetLPM / PhotocheckingLPM) happen on Azure SQL first and sync back to the country's on-prem MSSQL on a tunable interval (default 30 min), with a forced sync barrier at building completion to guarantee downstream on-prem apps see consistent data. On-prem WMS reads partial in-progress container data during build (every sync cycle) so pallet building + racking work continues without waiting for full container completion.

Item-master lookups (items not present in the current container's PCR) are served directly from **Google BigQuery** using the same pattern Barcode Generator uses today — eliminating a sync dependency on `usa.dbo.UPCbarcodes`.

The on-prem `lpm` database is **renamed to `AIWMS`** during the migration. This is a breaking change for any other on-prem app that reads `lpm.dbo.*` — coordination required (see Section 9).

---

## 2. Why this change

### 2.1 Problems with current architecture

1. **Multi-country deployment friction** — every release requires copying the AIWMS EXE to 7 country servers. Version drift, manual error.
2. **Shared on-prem MSSQL is the bottleneck** — Manual Building (100+ users/sec scanning), Store Distribution, Container Process, and other legacy apps all compete for the same DB server. Visible slowness reported by warehouse users.
3. **No unified identity** — each country runs its own Windows auth against its own AD context, no central user roster, no central audit.
4. **No centralized admin** — adding a user / warehouse / role requires touching one country's DB at a time.

### 2.2 Goals

- One Azure-hosted deployment serves all 7 countries
- Identity, authentication, audit, user/role/warehouse admin centralized in Azure
- Per-country on-prem MSSQL retained for legacy heavy operations
- Manual Building moves off the on-prem hot path (scan-time writes go to Azure SQL)
- Eventually consistent reads on-prem (default ≤30 min for non-completed containers, strict consistency on building completion)
- WMS on-prem can read partial in-progress container data for pallet building + racking
- Tooling familiar to the team — SQL Server everywhere, T-SQL throughout, no PostgreSQL learning curve

### 2.3 Explicit non-goals

- Migrating Store Distribution to Azure — too many tables, too tightly coupled to on-prem joins
- Migrating Container Process to Azure — heavy stored-procedure logic, runs against legacy DBs
- Replacing the legacy `usa`/`bfldata`/`hodata`/`datareporting` databases — they continue to be read by other on-prem apps
- Distributed transactions across Azure SQL and on-prem MSSQL — we use **eventual consistency with synchronization barriers** instead
- Historical PCR backfill from on-prem to Azure — fresh-start, only new containers land in Azure SQL

---

## 3. Target architecture (high-level)

```
                       ┌───────────────────────────────────────────────┐
                       │              GLOBAL — AZURE                    │
                       │                                                │
   ┌────────────┐      │   ┌────────────────────────────────────────┐   │
   │ Browser    │──────┼──▶│   Azure App Service (Linux, .NET 9)    │   │
   │ (warehouse │  WSS │   │   bflwms.bflgroup.ae                   │   │
   │ workstns)  │      │   │   Blazor Server (Aiwms.Web)            │   │
   └────────────┘      │   └────────┬────────────────┬──────────────┘   │
                       │            │                │                  │
                       │            │ OIDC           │ T-SQL            │
                       │            ▼                ▼                  │
                       │   ┌────────────────┐  ┌──────────────────────┐ │
                       │   │ Microsoft      │  │ Azure SQL Database   │ │
                       │   │ Entra ID       │  │ database = AIWMS     │ │
                       │   │ (bflgroup.ae)  │  │ schemas: dbo,        │ │
                       │   │                │  │  staging, mirror,    │ │
                       │   │                │  │  outbox, ref         │ │
                       │   └────────────────┘  └──────────────────────┘ │
                       │                              │                 │
                       │       ┌──────────────────────┘                 │
                       │       │ sync workers                           │
                       │       │ (IHostedService in Web)                │
                       │       │                                        │
                       │       │              ┌─────────────────────┐   │
                       │       │              │ Google BigQuery     │   │
                       │       │              │ temp_data.          │   │
                       │       │              │ _dim_item_master    │   │
                       │       │              └─────────────────────┘   │
                       └───────┼─────────────────────────┬──────────────┘
                               │ S2S VPN                 │
                  ┌────────────┼────────────┬────────────┼────────────┐
                  ▼            ▼            ▼            ▼            ▼
            ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐
            │ UAE      │ │ KSA      │ │ Kuwait   │ │ Oman     │ │ … 3 more │
            │ on-prem  │ │ on-prem  │ │ on-prem  │ │ on-prem  │ │ countries│
            │ MSSQL    │ │ MSSQL    │ │ MSSQL    │ │ MSSQL    │ │          │
            │          │ │          │ │          │ │          │ │          │
            │ bfldata  │ │ bfldata  │ │ bfldata  │ │ bfldata  │ │ …        │
            │ usa      │ │ usa      │ │ usa      │ │ usa      │ │          │
            │ AIWMS    │ │ AIWMS    │ │ AIWMS    │ │ AIWMS    │ │          │
            │ (was lpm)│ │ (was lpm)│ │ (was lpm)│ │ (was lpm)│ │          │
            │ hodata   │ │ hodata   │ │ hodata   │ │ hodata   │ │          │
            │ datarep… │ │ datarep… │ │ datarep… │ │ datarep… │ │          │
            └──────────┘ └──────────┘ └──────────┘ └──────────┘ └──────────┘
```

**Key flows:**

- **Auth**: Browser → App Service → Entra ID (single tenant `bflgroup.ae`). User identity = UPN like `sheeja@bflgroup.ae`.
- **Admin / Manual Building reads**: App Service → Azure SQL. No VPN hop. Fast.
- **Manual Building scan-time PCR writes**: App Service → Azure SQL (cached PCR). Background sync flushes to country's MSSQL.
- **Checkout writes** (UPCBoxHead/Det/Photochecking): App Service → Azure SQL. Background sync (default 30 min) to country's on-prem MSSQL.
- **Building completion**: forced sync barrier — verifies all Azure SQL checkout data is in on-prem MSSQL before allowing completion flag to be set.
- **Container Process** (legacy): runs on country's on-prem MSSQL. Writes denormalized PCR locally. Background sync pushes PCR to Azure SQL.
- **Item-master fallback** (item not in any container's PCR): App Service → Google BigQuery directly. Cached.

---

## 4. Database topology

### 4.1 Azure SQL Database — `AIWMS`

Single Azure SQL Database. All AIWMS-owned tables. Organized via SQL Server schemas:

| Schema | Purpose | Examples |
|---|---|---|
| `dbo` (default) | App-owned core | `AiwmsUser`, `AiwmsRole`, `AiwmsUserRole`, `AiwmsWHMaster`, `AiwmsAuditLog`, `AppConfig` |
| `staging` | In-progress Manual Building session state | `AiwmsOpenBox`, `AiwmsOpenBoxItem`, `AiwmsOpenBoxScan`, `AiwmsBoxSequence`, `AiwmsContainerPhotoCheck` |
| `mirror` | Read-replica of on-prem `AIWMS.*` synced from each country | `PhotoCheckingResultLPM` (denormalized + Country column), `BuildingCompletion` |
| `outbox` | Writes that go to Azure SQL first, sync to on-prem later | `UPCBoxHeadLPM`, `UPCBoxDetLPM`, `PhotocheckingLPM` |
| `ref` | Slowly-changing reference data synced from on-prem | `ContReceipt`, `KNBBoxes`, `BlueToteIDMaster` |

Every row in `mirror.*`, `outbox.*`, and `ref.*` carries a `Country` column (e.g., `'UAE'`, `'KSA'`) so we can multi-tenant in a single Azure SQL database.

### 4.2 On-prem per-country MSSQL — renamed `AIWMS` database

Each country's on-prem MSSQL has its `lpm` database renamed to `AIWMS` (see Section 9 for the migration mechanics + breaking-change impact).

After rename, the `AIWMS.dbo.*` namespace on each on-prem holds:

- **Legacy contents of `lpm.dbo.*`** — unchanged tables, just under a different DB name. Container Process writes these. Other on-prem apps read them (after they update their queries).
- **Sync-target tables for Azure-originated writes** — `UPCBoxHeadLPM`, `UPCBoxDetLPM`, `PhotocheckingLPM`, `BuildingCompletion` get fresh data pushed in by the sync workers.

The legacy `bfldata`, `usa`, `hodata`, `datareporting` databases on-prem are **unchanged** — they continue to be owned by their original systems.

### 4.3 Table ownership matrix

Authoritative source per table.

| Table / data | Owner | Lives on | Synced to | Sync direction | Sync interval |
|---|---|---|---|---|---|
| `AiwmsUser`, `AiwmsRole`, `AiwmsUserRole` | AIWMS | Azure SQL | — | — | — |
| `AiwmsAuditLog` | AIWMS | Azure SQL | — | — | — |
| `AiwmsWHMaster`, `AppConfig` | AIWMS | Azure SQL | — | — | — |
| `AiwmsOpenBox`, `AiwmsOpenBoxItem`, `AiwmsOpenBoxScan`, `AiwmsBoxSequence`, `AiwmsContainerPhotoCheck` | AIWMS | Azure SQL | — | — | — |
| `PhotoCheckingResultLPM` (denormalized) | **On-prem MSSQL** (Container Process writes) | per-country `AIWMS` (canonical) + Azure SQL `mirror` (read replica) | MSSQL → Azure SQL | one-way push | ~5 min (or SQL Server CDC) |
| `UPCBoxHeadLPM`, `UPCBoxDetLPM`, `PhotocheckingLPM` | **Azure SQL** (Manual Building writes) | Azure SQL `outbox` (canonical) + per-country on-prem `AIWMS` (replica) | Azure SQL → MSSQL | one-way push | 30 min default + forced at building completion |
| `BuildingCompletion` | **Azure SQL** | Azure SQL (canonical) + per-country on-prem `AIWMS` (replica) | Azure SQL → MSSQL | one-way push | Bundled with checkout sync |
| `ContReceipt`, `KNBBoxes` | **On-prem MSSQL** (legacy systems) | per-country (canonical) + Azure SQL `ref` (read cache) | MSSQL → Azure SQL | one-way push | ~5 min (bundled with PCR sync) |
| `BlueToteIDMaster` | **On-prem MSSQL** | per-country (canonical) + Azure SQL `ref` (read cache) | MSSQL → Azure SQL | one-time bulk + incremental | nightly bulk + on-demand for new IDs |
| Item master (was `usa.dbo.UPCbarcodes`) | **Google BigQuery** `temp_data._dim_item_master` | BigQuery | — | — | Live query (cached in app) |
| `usa.dbo.USAOrgFile`, `hodata.dbo.itemgroup`, `datareporting.dbo.upc_subclass`, `datareporting.dbo.SubclassMaster` | On-prem (other teams own) | per-country (canonical) | — | — | Eliminated as separate sources — **their useful columns are joined into the denormalized PCR at Container Process time** |

### 4.4 Denormalized PCR — the key consolidation

`PhotoCheckingResultLPM` is the heart of the system. Container Process produces it. Manual Building reads it constantly. Today, Manual Building has to join PCR against 4 other tables (`USAOrgFile`, `itemgroup`, `upc_subclass`, `SubclassMaster`) to display item details and resolve allocation. This means cross-DB queries on every scan.

After denormalization, **PCR carries all the columns Manual Building needs**, so Manual Building only queries PCR. The other 4 tables stop being read directly by AIWMS.

**Container Process** (on-prem, Riju's PR #1) does the join once per container, when populating PCR. The joined values land as columns on PCR rows:

| Source table | Source column | New PCR column |
|---|---|---|
| `usa.dbo.USAOrgFile` | `itemname` | `ItemName` |
| | `size` | `Size` |
| | `color` | `Color` |
| | `vendor` | `Brand` |
| | `season` | `Season` |
| | `gender` | `Gender` |
| | `hscode` | `HSCode` |
| | `lpm` | `LPM` |
| | `groupcode` | `GroupCode` |
| | `orgqty` | `OrgQty` |
| `hodata.dbo.itemgroup` | `Description` | `GroupDescription` |
| `datareporting.dbo.upc_subclass` | `MH4ID` | `MH4ID` |
| `datareporting.dbo.SubclassMaster` | `Division` | `Division` |
| | `Department` | `Department` |
| | `Class` | `Class` |
| | `Family` | `Family` |
| | `Subclass` | `Subclass` |

Plus the existing PCR columns: `IdNO`, `Contno`, `Itemcode`, `OraPoNO`, `LPMDT`, `Result`, `ResultType`, `QtyIssue`, `Style`, `BoxNo`, `qty`.

PCR is then synced to Azure SQL `mirror.PhotoCheckingResultLPM` with a `Country` column added. Manual Building's `BuildingService.GetItemDetailsAsync` becomes a single Azure SQL query — no cross-DB joins, no VPN hops.

**PCR IdNO collision**: each country's MSSQL has its own IDENTITY sequence. When 7 streams merge into one Azure SQL database, IdNO values collide. Azure SQL uses **composite primary key `(Country, IdNO)`** to preserve on-prem IdNO meaning while keeping rows globally unique:

```sql
CREATE TABLE mirror.PhotoCheckingResultLPM (
    Country   VARCHAR(8)   NOT NULL,
    IdNO      BIGINT       NOT NULL,
    Contno    VARCHAR(50)  NOT NULL,
    -- … denormalized columns …
    CONSTRAINT PK_mirror_PCR PRIMARY KEY (Country, IdNO)
);
CREATE INDEX IX_mirror_PCR_Cont_Item ON mirror.PhotoCheckingResultLPM (Country, Contno, Itemcode);
```

---

## 5. Synchronization mechanics

### 5.1 Sync workers — `IHostedService` inside `Aiwms.Web`

For v1, sync is implemented as background workers hosted inside the App Service:

```
Aiwms.Web
└── Aiwms.Data.Sync
    ├── PcrInboundSyncWorker         // on-prem MSSQL → Azure SQL, ~5 min
    ├── RefDataInboundSyncWorker     // on-prem MSSQL → Azure SQL (contreceipt, KNBBoxes), ~5 min
    ├── LpmOutboxSyncWorker          // Azure SQL → on-prem MSSQL (UPCBoxHead/Det/Photochecking, BuildingCompletion), ~30 min
    └── BlueToteSyncWorker           // on-prem MSSQL → Azure SQL, ~24h bulk + on-demand
```

Each worker runs on a `PeriodicTimer`. Configuration via `appsettings.json`:

```json
"Sync": {
  "PcrInbound":    { "IntervalSec": 300 },
  "RefInbound":    { "IntervalSec": 300 },
  "LpmOutbound":   { "IntervalSec": 1800 },
  "BlueToteBulk":  { "Cron": "0 2 * * *" }
}
```

Sync intervals are tunable in App Configuration without redeployment.

### 5.2 Outbox pattern for Azure SQL → on-prem MSSQL

Tables `outbox.UPCBoxHeadLPM`, `outbox.UPCBoxDetLPM`, `outbox.PhotocheckingLPM`, plus `dbo.BuildingCompletion`, all carry sync-status columns:

```sql
SyncStatus    CHAR(1)        NOT NULL CONSTRAINT DF_SyncStatus DEFAULT 'P',  -- 'P'=pending, 'S'=synced, 'F'=failed
SyncedAt      DATETIME2(0)   NULL,
SyncAttempts  SMALLINT       NOT NULL CONSTRAINT DF_SyncAttempts DEFAULT 0,
LastError     NVARCHAR(MAX)  NULL
```

Worker logic per country:
1. `SELECT … WHERE SyncStatus='P' AND Country=@country` (chunk by ContNo)
2. Open MSSQL transaction against country's on-prem
3. `INSERT INTO AIWMS.dbo.UPCBoxHeadLPM …` (bulk via `SqlBulkCopy`)
4. Commit MSSQL
5. Update Azure SQL rows: `SET SyncStatus='S', SyncedAt=SYSDATETIME()`
6. Commit Azure SQL
7. On any step 3 failure: rollback MSSQL, increment `SyncAttempts`, record `LastError`, retry next interval (exponential backoff after N failures)

A small admin page `/admin/sync-status` shows pending-row counts per country + recent failures. Alerts fire if any country's pending count exceeds threshold.

**WMS on-prem reading partial data**: because sync runs every 30 min (or whatever interval is tuned), WMS-on-prem sees newly-checked-out boxes within at most that window. Pallet building + racking on-prem can proceed without waiting for full container completion.

### 5.3 Building Completion barrier (the strict consistency point)

When Manual Building marks a container as complete:

```
User clicks "Mark Container Complete"
   │
   ▼
1. Check Azure SQL: count pending sync rows for this container
   SELECT COUNT(*) FROM outbox.UPCBoxHeadLPM
   WHERE Contno=@c AND Country=@cy AND SyncStatus='P'
   (and same for UPCBoxDetLPM, PhotocheckingLPM)
   │
   ▼
2. If any pending → trigger immediate forced sync for this container
   (chunk only this container's rows, run sync inline)
   │
   ▼
3. After forced sync, verify row counts:
     SELECT COUNT FROM outbox.UPCBoxHeadLPM WHERE Contno=@c AND Country=@cy
     ==
     SELECT COUNT FROM AIWMS.dbo.UPCBoxHeadLPM @ on-prem WHERE Contno=@c
   (and same for the other two tables)
   │
   ▼
4. If counts match → INSERT into dbo.BuildingCompletion (Country, Contno, …)
   │  This row will sync to on-prem in the next sync cycle.
   ▼
5. If counts don't match → block completion, show error to user,
   raise ops alert. Manual intervention required.
```

Total user-perceived latency: 2–10 seconds for a typical container. Acceptable for a once-per-container action.

### 5.4 Inbound PCR sync — on-prem MSSQL → Azure SQL

Container Process writes denormalized PCR on-prem. Sync worker every 5 min:

1. Per country: `SELECT * FROM AIWMS.dbo.PhotoCheckingResultLPM WHERE Modified > @lastWatermark`
2. MERGE into Azure SQL `mirror.PhotoCheckingResultLPM` (composite PK `Country, IdNO`)
3. Update watermark in Azure SQL

Requires a `Modified` (or `LastChangedTS`) column on on-prem PCR. **TODO: check if it exists; if not, add it as part of Riju's Container Process PR.**

Alternative if SQL Server CDC is enabled: subscribe to change events instead of polling. Lower latency, more infrastructure. Use CDC if `Modified` column can't easily be added.

### 5.5 Reverse-direction reads — Manual Building scan-time PCR writes

When a user scans an item:
- Read PCR from Azure SQL `mirror` (fast)
- Increment `QtyIssue` — write to Azure SQL `mirror` (fast)
- **The on-prem PCR is still the canonical source.** Azure SQL's increment is a local optimistic write that gets reconciled by the next PCR sync cycle.

This is a subtle design choice — for tier-1 allocation (the most common path), we trust Azure SQL's `QtyIssue` count. If a scan happens just before a sync cycle and the on-prem PCR didn't yet have the increment, the next sync reconciles.

**Edge case**: same item scanned simultaneously by users in different countries → impossible because containers are country-scoped. Same item scanned simultaneously by two users in the same country, both incrementing Azure SQL's PCR `QtyIssue` → handled by the existing `WITH (UPDLOCK, ROWLOCK)` SERIALIZABLE pattern in `BuildingService.ResolveAllocationAsync`. The T-SQL pattern carries over identically to Azure SQL.

---

## 6. Authentication & identity

### 6.1 Microsoft Entra ID OIDC SSO

- Single tenant: `bflgroup.ae`
- App registration created in Azure Portal (Phase 0 prerequisite)
- Sign-in flow: redirect to Entra → user logs in with `riju@bflgroup.ae` → redirect back with token → cookie session
- AIWMS reads `upn` claim from token → looks up `AiwmsUser` table on Azure SQL
- Replaces today's Windows `BFLDomain\sheeja` identity

### 6.2 Database migration for existing users

One-time SQL run against on-prem AIWMS before the cutover (then re-run against Azure SQL after migration):

```sql
UPDATE AiwmsUser SET Username = 'sheeja@bflgroup.ae' WHERE Username = 'BFLDomain\sheeja';
-- (and so on for each existing user)
```

After migration, all `AiwmsUser.Username` values are UPNs.

### 6.3 No more "domain-joined PC" requirement

Today users need their PC joined to the BFL domain (or VPN to a domain controller) for Negotiate auth to work. With OIDC, any browser on any network can log in — auth happens in the browser via Entra ID.

---

## 7. Multi-country routing

### 7.1 Connection-string layout in Azure App Configuration

```
ConnectionStrings:Aiwms              → "Server=<sql>.database.windows.net;Database=AIWMS;Authentication=Active Directory Default;…"
ConnectionStrings:OnPrem:UAE         → "Server=192.168.10.72;Database=AIWMS;User Id=…;Password=@KeyVault(…)"
ConnectionStrings:OnPrem:KSA         → "Server=10.20.30.40;Database=AIWMS;User Id=…;Password=@KeyVault(…)"
ConnectionStrings:OnPrem:KUW         → "Server=…"
ConnectionStrings:OnPrem:OMN         → "Server=…"
ConnectionStrings:OnPrem:QAT         → "Server=…"
ConnectionStrings:OnPrem:BAH         → "Server=…"
ConnectionStrings:OnPrem:EGY         → "Server=…"
```

Passwords stored in **Azure Key Vault**, referenced from App Config via the `@KeyVault(…)` syntax. App Service uses **Managed Identity** to read Key Vault — no secrets in code, no secrets in git.

For Azure SQL itself, prefer **Managed Identity** authentication (no password at all — App Service identity is granted DB access via `CREATE USER … FROM EXTERNAL PROVIDER`).

### 7.2 Country resolution at request time

```csharp
// New service
public interface ICountryConnectionResolver
{
    string GetOnPremConnectionString(string country);
}
```

Behavior:
- Reads connection strings at startup from `IConfiguration`
- `Aiwms.Web` injects `ICurrentUser` and `ICountryConnectionResolver`
- Sync workers iterate over all 7 countries
- Manual Building / Container Process queries resolve by `Me.Country` (or container-specific country if different)

### 7.3 Country column on shared tables

Every Azure SQL table that mirrors per-country data has a `Country` column. Composite keys use `(Country, …)`. Indexes always include `Country` as leading column for efficient per-country filters.

`AiwmsUser.Country` (existing) is the source of country for the logged-in user. Containers in `mirror.PhotoCheckingResultLPM` carry `Country` from the sync source.

### 7.4 Container number uniqueness

**Decision pending**: are container numbers globally unique across all 7 countries (e.g., AE-prefix for UAE, SA-prefix for KSA, etc.), or can `AEINT6078` exist in both UAE and KSA?

If globally unique: simpler — `Contno` alone is enough in many queries.
If not globally unique: every container query must include `Country` predicate.

This doc assumes **not globally unique** as the safer default. All multi-tenant tables use `(Country, …)` composite keys. Confirm or correct (see Section 13 #2).

---

## 8. Item-master lookup — Google BigQuery

When a scanned item has no PCR row in the current container:
1. Cache lookup (in-memory, see below)
2. Cache miss → call BigQuery `temp_data._dim_item_master` via Google.Cloud.BigQuery.V2 client
3. Query: `SELECT * FROM temp_data._dim_item_master WHERE UPPER(upc) = UPPER(@itemcode) OR UPPER(style) = UPPER(@style) LIMIT 100`
4. Return enriched item attributes (item_desc, style, brand, hierarchy)
5. Display as "item exists in master but not in container" — SHOP fallback applies

### 8.1 Caching

```
Positive result (item exists)    → cache 24h    (items rarely vanish from master)
Negative result (not found)      → cache 30s    (might be added via Item Encoding mid-session)
```

Cache key: `itemcode` (upper-cased). Storage: `IMemoryCache` (per-instance, App Service scale-out OK because cache is non-authoritative).

### 8.2 Auth & secrets

- GCP service account JSON key
- Required roles: `BigQuery Data Viewer`, `BigQuery Job User` on the project
- Key stored in Azure Key Vault, mounted at App Service start via `GOOGLE_APPLICATION_CREDENTIALS` env var pointing at a temp file
- For local dev: same env var pointing at a JSON file on the developer's machine (gitignored)

### 8.3 Reference doc

A separate `docs/integrations/bigquery-itemmaster.md` will capture the exact endpoint, request shape, response parsing, and column dictionary. See Section 14 for status.

---

## 9. Naming change: `lpm` → `AIWMS` (HARD RENAME — breaking change)

### 9.1 What's happening

Each country's on-prem MSSQL has its **`lpm` database renamed to `AIWMS`** during the migration:

```sql
USE master;
ALTER DATABASE lpm SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
ALTER DATABASE lpm MODIFY NAME = AIWMS;
ALTER DATABASE AIWMS SET MULTI_USER;
```

After this, every reference to `lpm.dbo.…` on that server stops working. Apps must use `AIWMS.dbo.…`.

### 9.2 Why this is risky

Any on-prem app whose SQL contains `FROM lpm.dbo.PhotoCheckingResultLPM` (or `lpm.dbo.UPCBoxHeadLPM`, `lpm.dbo.UPCBoxDetLPM`, `lpm.dbo.PhotocheckingLPM`, `lpm.dbo.BuildingCompletion`, or any other `lpm.dbo.*` table) **stops working the moment the rename runs**, until that app is updated.

Likely consumers to check (Section 13 #7 — needs your input):

- **Container Process** (Riju's PR #1) — directly reads/writes `lpm.dbo.PhotoCheckingResultLPM`. **Must** be updated.
- **Store Distribution** — likely reads pack-out tables. Owner must update.
- **SSRS / Power BI reports** — any report querying `lpm.*`. Each needs editing.
- **ETL feeding finance / dashboards** — any pipeline pulling from `lpm.*`.
- **VB6 / Access legacy apps** — older custom apps may have hardcoded `lpm.dbo.*`.
- **Stored procedures in `usa.dbo.*` or `online.dbo.*`** that reference `lpm.dbo.*` cross-DB.
- **DBA scripts** — maintenance jobs, backup scripts referencing `lpm`.

### 9.3 Pre-rename inventory (mandatory)

Before any country's `lpm` is renamed, run the inventory query against each on-prem MSSQL:

```sql
-- Find every stored procedure / view / function referencing lpm.*
SELECT DISTINCT
    DB_NAME() AS database_name,
    OBJECT_SCHEMA_NAME(referencing_id) AS schema_name,
    OBJECT_NAME(referencing_id) AS object_name,
    o.type_desc
FROM sys.sql_expression_dependencies d
JOIN sys.objects o ON o.object_id = d.referencing_id
WHERE d.referenced_database_name = 'lpm';

-- Find linked servers / sync jobs referencing lpm
SELECT name, data_source FROM sys.servers WHERE is_linked = 1;
```

Plus a list of every **external app** that connects to this MSSQL — each app's source code or query log needs grepping for `lpm.dbo.` or `lpm..`.

### 9.4 Coordinated migration plan

Per country, in a maintenance window:

1. **Stop all consumer apps** that read `lpm.*` (you've inventoried them in 9.3)
2. **Run the `ALTER DATABASE lpm MODIFY NAME = AIWMS`**
3. **Update each consumer app's queries** from `lpm.dbo.*` to `AIWMS.dbo.*` and redeploy
4. **Smoke-test each app** before opening to users
5. **Open AIWMS** in that country

Stagger countries — don't do all 7 at once. Pilot with one country, fix issues, then roll out.

### 9.5 Table name suffixes

The current table names carry the `LPM` suffix (`PhotoCheckingResultLPM`, `UPCBoxHeadLPM`, etc.). This doc **keeps the suffix** to preserve 1:1 mapping with sync between Azure SQL and on-prem. Renaming tables themselves is out of scope for this design — can be done later as a separate cleanup.

### 9.6 Code-side updates

In `Aiwms.Data.Lpm.BuildingService.cs` (and any new sync code), every `lpm.dbo.…` reference becomes `AIWMS.dbo.…`. Riju's `Aiwms.Data.ContainerProcess.ContainerProcessingService.cs` (PR #1) — same update required.

In addition, the C# folder `Aiwms.Data.Lpm` should be renamed to `Aiwms.Data.Aiwms` for consistency. (Or kept — debatable; preserves existing namespaces.)

---

## 10. Phased migration plan

Each phase is **independently shippable and reversible**. No phase requires going back to undo a previous phase.

### Phase 0 — Foundation (no DB changes)
- Phase 3 (Entra ID OIDC) auth swap — blocked on Azure app registration
- Phase 4a (Azure App Service + Linux deploy of current shape) — VPN to existing per-country on-prem MSSQL via existing connection string
- **Result**: warehouse users hit Azure-hosted app instead of country EXE. Same data layer.

### Phase 1 — Provision Azure SQL + admin migration
- Provision Azure SQL Database, create `AIWMS` DB with all schemas (`dbo`, `staging`, `mirror`, `outbox`, `ref`)
- Migrate `AiwmsUser`, `AiwmsRole`, `AiwmsUserRole`, `AiwmsWHMaster`, `AiwmsAuditLog`, `AppConfig` to Azure SQL
- App reads/writes these from Azure SQL
- **Result**: admin operations independent of any on-prem MSSQL

### Phase 2 — Manual Building staging to Azure SQL
- Migrate `AiwmsOpenBox`, `AiwmsOpenBoxItem`, `AiwmsOpenBoxScan`, `AiwmsBoxSequence`, `AiwmsContainerPhotoCheck` to Azure SQL `staging` schema
- BuildingService updated to query Azure SQL for staging
- **Result**: scan staging operations no longer hit on-prem

### Phase 3 — Denormalized PCR + read mirror
- Riju's PR #1 (Container Process) updated to populate denormalized PCR columns
- Set up Azure SQL `mirror.PhotoCheckingResultLPM` (composite PK `Country, IdNO`)
- Set up `PcrInboundSyncWorker` — ~5 min interval, per country
- BuildingService refactored: `GetItemDetailsAsync` reads Azure SQL only
- **Fresh start**: no historical PCR backfill — only new containers populated post-Phase-3
- **Result**: scan-time reads eliminated from on-prem MSSQL

### Phase 4 — `lpm` → `AIWMS` rename + outbox writes
- **Pre-rename inventory** (Section 9.3) across all countries
- Coordinate with consumer app owners (Container Process, Store Distribution, BI, others)
- Per country in maintenance windows: rename `lpm` → `AIWMS`, update consumer apps, smoke-test
- Create per-country `AIWMS.dbo.*` schema for the outbox target tables
- Create Azure SQL `outbox.*` tables with sync-status columns
- BuildingService.CheckoutBoxAsync writes to Azure SQL outbox (not on-prem)
- Set up `LpmOutboxSyncWorker` — 30 min default interval
- Implement Building Completion barrier (forced sync + verify)
- **Result**: checkout writes eliminated from on-prem hot path; `lpm` retired on every country

### Phase 5 — Reference data sync
- Set up `RefDataInboundSyncWorker` for `contreceipt`, `KNBBoxes`
- Set up `BlueToteSyncWorker` for `BlueToteIDMaster` (bulk + incremental)
- BuildingService reads these from Azure SQL `ref` schema
- **Result**: container/box validation eliminated from on-prem hot path

### Phase 6 — BigQuery item-master integration
- Add `Google.Cloud.BigQuery.V2` NuGet
- Implement `ItemMasterLookupService` with caching
- BuildingService falls back to BQ for items not in PCR
- **Result**: `usa.dbo.UPCbarcodes` eliminated as AIWMS dependency

### Phase 7 — Per-country routing & multi-tenancy
- Add `ICountryConnectionResolver` service
- Migrate hardcoded connection strings to Azure App Configuration + Key Vault
- Set up 7 on-prem connection strings in App Config
- Onboard country 2, 3, 4, … to the same Azure app
- **Result**: single Azure deployment serves all 7 countries

### Phase 8 — Item Encoding (greenfield)
- New module, Azure SQL-native from day one
- Allows users to create new items that BigQuery doesn't yet know about
- **Result**: closes the "new item" loop in Manual Building

### Future / out of scope
- Store Distribution — stays on-prem (updated to read `AIWMS.dbo.*` post-rename)
- Container Process — stays on-prem (also updated for the rename, plus denormalized PCR columns)
- Other downstream apps reading `lpm.*` — must be updated as part of Phase 4

---

## 11. Failure modes & operational concerns

| Failure | Detection | Recovery |
|---|---|---|
| Azure SQL unreachable | App health probe fails | App Service restarts; if persistent, user sees error page; ops alert |
| One country's on-prem MSSQL unreachable | Sync worker logs failures, pending count grows | Auto-retry; alert when threshold exceeded; manual intervention if VPN broken |
| BigQuery unreachable | Cached responses keep working; new lookups error | App shows "Item lookup temporarily unavailable, try again"; non-blocking for items already in PCR |
| Building Completion barrier fails (sync inconsistency) | User sees error at completion time | Forced sync retry; if persists → ops investigates outbox + on-prem state |
| Azure SQL down during scan-time PCR update | Scan fails with clear error | User retries; no data loss because Azure SQL is canonical for staging |
| Entra ID down | Logged-in sessions continue; new logins fail | Wait for Entra recovery; Microsoft SLA |
| Sync worker process crashes | Outbox rows stay `'P'`, picked up on restart | App Service auto-restarts the host service; no data loss |
| Two users in same country scan same item, both increment PCR | `WITH (UPDLOCK, ROWLOCK)` SERIALIZABLE pattern in `BuildingService.ResolveAllocationAsync` | Existing pattern preserved on Azure SQL; one waits, one proceeds |
| `lpm` → `AIWMS` rename completed but a consumer app wasn't updated | App's queries fail "Invalid object name 'lpm.dbo.…'" | Inventory gap from Section 9.3 — emergency: temporarily create `lpm` as an empty stub DB with synonyms while consumer is fixed |

---

## 12. Cost estimate (rough)

| Component | Tier | Estimated monthly |
|---|---|---|
| Azure App Service Linux | B2 (small dev) → P1V3 (prod) | $50 → $250 |
| Azure SQL Database | General Purpose Gen5 2 vCore (start) → 4 vCore (prod) | $300 → $600 |
| Azure Key Vault | Standard | <$5 |
| Azure App Configuration | Standard | $1.20 |
| S2S VPN | per country, BasicSku | $30 × 7 = $210 |
| Google BigQuery | per-query | $5–20 (query volume dependent) |
| **Total estimated range** | | **$600 → $1,300/month** |

For comparison: 7 on-prem servers + 7 SQL Server licenses + ops overhead is already in the thousands per month. Net savings expected.

**Performance note from past experience** (Section 13 #11): when sizing Azure SQL, start at **General Purpose 2 vCore minimum** (not Basic/S0/S1). For Power BI reporting later, do NOT use DirectQuery against the OLTP DB — add a read replica or Import-mode refresh.

---

## 13. Open questions / decisions made

| # | Question | Default in this doc | Decision by | Status |
|---|---|---|---|---|
| 1 | Azure DB engine — PG or Azure SQL? | **Azure SQL Database** | Sheeja | ✅ Resolved 2026-05-25 |
| 2 | Container numbers globally unique across countries? | No (safer assumption) | Sheeja | Open |
| 3 | Per-country MSSQL — server count confirmed as 7? | 7 (UAE, KSA, KUW, OMN, QAT, BAH, EGY) | Sheeja | Open |
| 4 | Are all 7 country MSSQL servers reachable from Azure via S2S VPN? | S2S VPN | IT | Open |
| 5 | Sync mechanism — `IHostedService` in App or separate Azure Function? | `IHostedService` for v1 | Sheeja + Riju | Open |
| 6 | Acceptable lag for `buildingcompletion` write-back to on-prem? | 30 min default, tunable | Sheeja | ✅ Resolved (tunable, 30 min default) |
| 7 | What other on-prem apps read `lpm.dbo.*`? | Unknown — inventory required pre-rename (Section 9.3) | Sheeja + DBA | **Blocker for Phase 4** |
| 8 | BigQuery project ID and service account setup status | Pending | Sheeja | Open |
| 9 | Entra ID app registration — Tenant ID + Client ID | Pending | Sheeja | **Blocker for Phase 0** |
| 10 | Backfill of historical PCR data to Azure SQL `mirror`? | **No — fresh start** | Sheeja | ✅ Resolved 2026-05-25 |
| 11 | Past Power BI slowness on Azure SQL — mitigation? | Right tier (GP 2 vCore+), proper indexes, no DirectQuery for reports | Sheeja | ✅ Resolved 2026-05-25 |
| 12 | `lpm` → `AIWMS` strategy — rename, alongside, or synonym shell? | **Hard rename (Option A)** | Sheeja | ✅ Resolved 2026-05-25 |
| 13 | Sync interval for PG-→ on-prem (Building Completion + outbox)? | 30 min default, tunable, partial visible during build | Sheeja | ✅ Resolved 2026-05-25 |
| 14 | Single Azure SQL DB with `Country` column, or per-country DBs? | **Single AIWMS DB, Country column** | Sheeja | ✅ Resolved 2026-05-25 |

---

## 14. Related artifacts

- **PR #1** — Riju's `container process` branch — needs updates per Section 4.4 (denormalized PCR columns) and Section 9.6 (rename `lpm.dbo.*` → `AIWMS.dbo.*`)
- **PR #2** — CODEOWNERS Option B (merged 2026-05-25)
- **`db/install_aiwms.sql`** — current on-prem AIWMS DB install script — needs split into:
  - Azure SQL schema setup (per Section 4.1)
  - Per-country on-prem `AIWMS.dbo.*` schema setup (for outbox-target tables)
- **`db/migrate_lpm_to_aiwms.sql`** — TODO — one-time per-country rename script (Section 9.4)
- **`db/qa_check_columns.sql`** — QA script — needs expansion to cover Azure SQL tables + on-prem `AIWMS.dbo.*` after rename
- **`docs/integrations/bigquery-itemmaster.md`** — TODO (separate doc, not started)
- **GitHub Issue: "Phase 0 — Entra OIDC + Azure deploy"** — TODO, after Tenant ID arrives
- **GitHub Issue: "Phase 1 — Admin to Azure SQL"** — TODO
- (etc. — one issue per phase for tracking)

---

## 15. Glossary

| Term | Meaning |
|---|---|
| **PCR** | `PhotoCheckingResultLPM` — the per-container allocation table |
| **Bucket** | Earlier shorthand: a group of tables with similar sync semantics |
| **Outbox** | A table in Azure SQL holding rows pending sync to on-prem |
| **Building Completion barrier** | The forced-sync + verification step at container completion time |
| **Denormalized PCR** | PCR with item attributes and MH4 hierarchy joined in as columns |
| **`mirror` schema** | Azure SQL schema holding read-replica of on-prem `AIWMS` data |
| **`outbox` schema** | Azure SQL schema holding writes that sync to on-prem |
| **`ref` schema** | Azure SQL schema holding cached reference data from on-prem |
| **`staging` schema** | Azure SQL schema holding in-progress Manual Building session state |
| **Country** | One of 7 ISO-ish country codes (`UAE`, `KSA`, `KUW`, `OMN`, `QAT`, `BAH`, `EGY`) |
| **Entra ID** | Microsoft's renamed Azure Active Directory; OIDC provider |
| **UPN** | User Principal Name, e.g. `sheeja@bflgroup.ae` |
| **OIDC** | OpenID Connect — auth protocol used with Entra ID |
| **S2S VPN** | Site-to-Site VPN connecting Azure VNet to each country's on-prem network |
| **CDC** | Change Data Capture — SQL Server feature for low-latency change feeds |
| **Managed Identity** | Azure feature — App Service authenticates to other Azure services (SQL, Key Vault) without storing secrets |
| **Hard rename** | The `lpm` → `AIWMS` migration approach chosen (Option A) — breaking change for `lpm.*` consumers; coordinated with consumer-app updates |
