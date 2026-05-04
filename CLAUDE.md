# AIWMS — Claude project context

This file is auto-loaded by Claude Code when run from the repo root. It captures
project knowledge that isn't obvious from the codebase alone.

## What this is

On-prem warehouse-management web app for **BFL** (BFL Group, UAE).
- ASP.NET Core 9 + Blazor Server + MudBlazor (custom CSS, NOT Material default)
- MSSQL backend on **on-prem server `192.168.10.72`** (Mixed Mode auth)
- Currently dev-hosted on **BFLITH (192.168.10.4)**; targeting **Azure App Service Linux** at `https://bflwms.bflgroup.ae` with **Entra ID OIDC SSO**
- Repo: https://github.com/sheeja72/BFLWMS

## Solution layout

```
src/
  Aiwms.Core/    Entities, role constants, validators (no EF/Web deps)
  Aiwms.Data/    EF Core DbContext, audit interceptor, BuildingService (Dapper for cross-DB)
  Aiwms.Web/     Blazor Server pages, auth, layout, CSS
db/
  install_aiwms.sql              Idempotent — creates AIWMS DB + tables + indexes
  qa_check_columns.sql           Verify column refs against actual schemas (run pre-deploy)
  migrate_pcr_idno_identity.sql  One-time: convert lpm.PCR.IdNO → IDENTITY
.github/
  workflows/ci.yml               Builds on every push & PR to main
  CODEOWNERS                     Auto-request reviewers on /db, /Auth, Program.cs, /.github
  pull_request_template.md       PR checklist
```

## Databases — what we read vs write

**AIWMS-owned (we manage)**:
- `AIWMS.dbo.AiwmsUser`, `AiwmsRole`, `AiwmsUserRole`
- `AIWMS.dbo.AiwmsAuditLog` (auto via SaveChangesInterceptor)
- `AIWMS.dbo.AiwmsWHMaster`
- `AIWMS.dbo.AiwmsBoxSequence` — concurrency-safe box # mint
- `AIWMS.dbo.AiwmsContainerPhotoCheck` — one-time photo qty match cache per container
- `AIWMS.dbo.AiwmsOpenBox`, `AiwmsOpenBoxItem` — staging during build session
- `AIWMS.dbo.AiwmsOpenBoxScan` — per-scan PCR effect log (lets `Clear Box` reverse PCR changes)
- `AIWMS.dbo.AppConfig` — encrypted connection settings via Data Protection

**Existing DBs we WRITE to** (must NOT change schema):
- `lpm.dbo.PhotoCheckingResultLPM` — UPDATE QtyIssue / INSERT new rows / UPDATE BoxNo (the hot path)
- `lpm.dbo.UPCBoxHeadLPM`, `UPCBoxDetLPM`, `PhotocheckingLPM` — written at Check-Out

**Existing DBs we READ only** (must NOT change schema):
- `bfldata.dbo.buildingcompletion`, `contreceipt`, `BlueToteIDMaster`, `DataSettings`
- `usa.dbo.OpenUSACont`, `KNBBoxes`, `USAOrgFile`, `UPCbarcodes`
- `hodata.dbo.itemgroup`
- `datareporting.dbo.upc_subclass`, `SubclassMaster`

## Confirmed column names that aren't obvious

User confirmed these in Apr 2026 — do NOT change:

`lpm.dbo.PhotoCheckingResultLPM`:
- `IdNO` (uppercase, was migrated to IDENTITY)
- `Contno`, `Itemcode`, `OraPoNO`, `LPMDT` (uppercase!), `Result`, `ResultType`
- `QtyIssue` (NOT `QtyIssued`), `Style`, `BoxNo`, `qty` (lowercase, used in SUM only)

`usa.dbo.USAOrgFile`:
- PO column is **`OraPONo`** — NOT `ponumber`, `PONumber`, or `OraPo`
- Other columns lowercase: `contno`, `itemcode`, `itemname`, `style`, `size`, `color`, `vendor` (=Brand), `season`, `gender`, `hscode`, `lpm`, `groupcode`, `orgqty`

`hodata.dbo.itemgroup`:
- `groupcode`, **`Description`** (NOT `grpname` despite original spec)

`lpm.dbo.UPCBoxHeadLPM` (full mapping per user spreadsheet):
- Has NO `Contno` column. PK is `BoxNo`.
- Hardcodes: `NewPallet='Y'`, `Closed='N'`, `Remarks='from AIWMS'`
- `Userid` is **INT** in some envs — may need numeric mapping (TBD with user)

`lpm.dbo.UPCBoxDetLPM`: `BoxNo, Itemcode, Qty, QtyIssued=0, SrNo, Status='', UPC=Itemcode, imgfile=Contno`

`lpm.dbo.PhotocheckingLPM` (one row per scan event): `ContNo` (capital N!), `TrnDate, Time1, UPC=Itemcode, PhotoSize=size, Result, CheckedBy, CmpName=reverse-DNS of client IP, BoxSize=size, Style, Color, GroupCode, Warehouse=user.Warehouse, RRP=0, Logistics_BoxNo=supplier physical box, Season, ToteID, RoboStatus='N'`, blanks for the rest.

If you reference any of these in new code, **also add the column to `db/qa_check_columns.sql`** so missing columns surface before a deploy goes wrong.

## Hard-won runtime gotchas (DO NOT REINTRODUCE)

1. **`@rendermode RenderMode.InteractiveServer` is required per-page** for `@onclick` to work in .NET 9 Blazor Web Apps — static SSR is the default. Pages with form actions need it: Setup, LpmManualBuilding, Users, WHMaster, Audit.

2. **Negotiate auth + Kestrel + interactive Blazor prerender hangs**. We use HTTP.sys via `builder.WebHost.UseHttpSys(...)` for on-prem. **For Azure deployment** this changes to Entra ID OIDC via `Microsoft.Identity.Web` (Phase 3, in progress).

3. **`AuditSaveChangesInterceptor` MUST be `Singleton` with `IServiceScopeFactory`** — was previously Scoped + ICurrentUser ctor injection, caused DI deadlock when DbContext factory options lambda resolved the interceptor during claims transformation. Pattern in `Aiwms.Data/Auditing/AuditSaveChangesInterceptor.cs`.

4. **`AiwmsClaimsTransformer` DB lookup wrapped in `CancellationTokenSource(5s)` + try/catch** so DB outages don't hang the request pipeline. Don't remove the timeout.

5. **Don't `baseId.Clone()` a `WindowsIdentity`** in claims transformer — it loses `.Name` (which is SID-derived, not a claim). Add claims to the existing identity in place.

6. **Server-side Blazor `@bind`** defaults to `change` event (fires on blur). Use `@bind:event="oninput"` for any input where the user might press Enter without losing focus first — otherwise the bound variable lags one keystroke.

7. **SQL Error 4060** symptom: `/setup` configures fine, but auth-gated routes return 403 because claims transformer's DB lookup silently fails. Fix: ensure target SQL login is mapped as user in `AIWMS` database with `db_owner`.

## Concurrency design

- **Box-number minting**: `UPDLOCK,HOLDLOCK` on `AiwmsBoxSequence` inside the Stage transaction → no duplicate `AEINT6078-0001` even with N parallel users on same container.
- **Tote uniqueness**: filtered `UNIQUE(ToteID) WHERE ToteID IS NOT NULL` on `AiwmsOpenBox` blocks two boxes sharing a non-null tote, but allows multiple in-progress boxes with no tote yet.
- **PCR allocation tier-1 row reservation**: `WITH (UPDLOCK, ROWLOCK)` on tier-1 SELECT inside a SERIALIZABLE transaction prevents two users from claiming the same `QtyIssue=0` row.

## Clear Box semantics

`Clear` reverses every PCR effect for the box and deletes staging rows.
- `AiwmsOpenBoxScan` records each scan event's PCR action (`U` = QtyIssue increment, `I` = new row insert).
- On Clear: walk events newest-first, decrement `QtyIssue` (clamped at 0) for `U`, DELETE the inserted PCR row for `I`.
- Then DELETE staging children + the AiwmsOpenBox row.
- All in a SERIALIZABLE transaction. Same-user check enforced (only the box owner can clear).

## Roles

- `Admin` — everything (Users, WH Master, Audit, Settings)
- `WHManager`, `WHSupervisor`, `WHAssociate` — warehouse ops only
- Constants in `Aiwms.Core/Roles.cs`

## Dev workflow

```powershell
cd src\Aiwms.Web
dotnet watch run            # auto-rebuilds on file save
# http://localhost:5217 — first run goes to /setup
```

After cloning, **first-run config**:
1. Visit `/setup` → enter SQL connection (`192.168.10.72`, AIWMS DB, the AIWMS login)
2. Have an existing Admin add your user via `/admin/users` (with Country + Warehouse)
3. Hard-refresh `/`

For **schema changes**: re-run `db/install_aiwms.sql` (idempotent). For new column references: also update `db/qa_check_columns.sql`.

## Branching / PR flow

- `main` is protected — no direct pushes
- Branch off: `feature/<thing>`, `fix/<thing>`, `chore/<thing>`, `docs/<thing>`
- PR with checklist filled in (template auto-loads)
- CI must pass + ≥1 reviewer approval → squash-merge → branch deleted

## Phase status (May 2026)

- ✅ Local dev on BFLITH with Windows Auth (HTTP.sys + Negotiate)
- ✅ LPM Manual Building workflow end-to-end (4-tier alloc + per-row Check-In/Out/Clear)
- ✅ GitHub repo + CI + branch protection + team collab files
- ⏳ Phase 3 — Entra ID OIDC swap (waiting on user's Tenant ID + Client ID from Azure portal)
- ⏳ Phase 4 — Azure App Service Linux deployment + Hybrid Connection / S2S VPN to on-prem MSSQL (waiting on IT)
- ⏳ Item Encoding (`/encoding`) and LPM Production (`/production/lpm`) — stubs awaiting scope

## When in doubt

- Read this file. Then read `CONTRIBUTING.md`. Then `BuildingService.cs` (the heart of the app).
- For column-name uncertainty in cross-DB queries, **never guess** — run `db/qa_check_columns.sql` and check live schema.
- Wrap every cross-DB Dapper call site with `DbOpContext.Set("description", sql)` so SQL errors surface the failing operation in the page error panel.
