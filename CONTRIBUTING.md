# Contributing to AIWMS

Thanks for picking this up. Here's how the team works on this repo.

## Get the code running

```powershell
git clone https://github.com/sheeja72/BFLWMS.git
cd BFLWMS\src\Aiwms.Web
dotnet watch run
```

Then browse to `http://localhost:5217`. First run sends you to `/setup` — enter the on-prem MSSQL (`192.168.10.72`, AIWMS DB) credentials. Stored encrypted via ASP.NET Core Data Protection at `App_Data/connection.protected` (already in `.gitignore`).

You need:
- .NET 9 SDK ([download](https://dotnet.microsoft.com/download))
- Network access to `192.168.10.72:1433` (LAN VPN if remote)
- A row in `AIWMS.dbo.AiwmsUser` for your Windows username with at least one role assigned. Ask an existing Admin to add you via the `/admin/users` page.

## Branching & pull-request flow

`main` is protected. **No direct commits or force pushes** — everything goes through a PR.

```powershell
git checkout main
git pull
git checkout -b feature/<short-kebab-name>
# ...code...
git add .
git commit -m "Short imperative summary"
git push -u origin feature/<short-kebab-name>
```

Then on github.com:
- Open a Pull Request → base `main`
- Fill in the PR template
- Tag a teammate for review
- After ≥1 approval and CI green → squash merge → delete branch

### Branch naming
- `feature/<thing>` — new functionality
- `fix/<thing>` — bug fixes
- `chore/<thing>` — refactors, deps, tooling
- `docs/<thing>` — documentation only

### Commit messages
- One-line summary in the imperative present tense (50 chars or less ideal)
- Optional blank line + body for context
- Reference issues with `#<num>`
- Examples:
  - `Fix box-grid focus loop on resume`
  - `Add WHMaster country filter to user form`
  - `Refactor BuildingService allocation tiers`

## Code style

- `.editorconfig` enforces formatting — your IDE picks it up automatically
- File-scoped namespaces, 4-space indent, CRLF
- Prefer `var` when the type is obvious
- Add XML doc comments on public APIs
- Use `DbOpContext.Set(op, sql)` before any cross-DB Dapper call so SQL errors surface the failing operation in the page error panel

## What lives where

```
src/
  Aiwms.Core/    Entities + role constants + validators (no deps on EF/Web)
  Aiwms.Data/    EF Core DbContext, audit interceptor, BuildingService (Dapper)
  Aiwms.Web/     Blazor Server pages, layout, auth, CSS
db/
  install_aiwms.sql                 Idempotent schema setup
  qa_check_columns.sql              Verify column refs against live schema
  migrate_pcr_idno_identity.sql     One-time migration
.github/
  workflows/ci.yml                  Builds on every push/PR
```

## When you change SQL

If you reference a new column in a cross-DB query (anything outside `AIWMS.dbo.*`), **also add it to `db/qa_check_columns.sql`** so we can verify it exists before deploying anywhere new. The script is run pre-deploy; missing columns show as `MISSING` in the output.

## Secrets — never commit

The `.gitignore` already blocks: `secrets.json`, `appsettings.Production.json`, `App_Data/`, `.env`. If you accidentally commit a credential, **rotate it immediately** (assume it leaked) and tell the team to force-clean history.

## Questions

Open an Issue or message on the team chat. PR > issue > silent rewrite.
