# AIWMS — AI Warehouse Management System

On-prem warehouse-management web app for BFL. Built with **ASP.NET Core 9 + Blazor Server** with **MSSQL** backend.

## Status

- LPM Manual Building workflow implemented and runs locally on `BFLITH` (192.168.10.4)
- Backend MSSQL on `192.168.10.72` — multi-DB (AIWMS owned + bfldata, usa, lpm, hodata, datareporting read/write)
- Hosting target: **Azure App Service (Linux)** at `https://bflwms.bflgroup.ae`
- Auth target: **Microsoft Entra ID OIDC SSO**

## Solution layout

```
src/
  Aiwms.Core/    — entities, validators, role constants
  Aiwms.Data/    — EF Core DbContext, audit interceptor, BuildingService (Dapper for cross-DB)
  Aiwms.Web/     — Blazor Server pages, auth, layout
db/
  install_aiwms.sql           — creates AIWMS DB + tables + indexes (idempotent)
  qa_check_columns.sql        — verifies column references against actual schema
  migrate_pcr_idno_identity.sql — converts lpm.PCR.IdNO to IDENTITY (one-time)
```

## Local development

```powershell
cd src\Aiwms.Web
dotnet watch run
```

Then browse to `http://localhost:5217`. First run goes to `/setup` to configure DB connection (encrypted via ASP.NET Core Data Protection at `App_Data/connection.protected`).

## Production

- App: Azure App Service Linux (B1+), Entra ID single-tenant OIDC
- DB: on-prem MSSQL `192.168.10.72`, reached via **Azure Site-to-Site VPN**
- DNS: `bflwms.bflgroup.ae` A-record → App Service IP
- TLS: App Service managed certificate

## Roles

- `Admin` — full access (Users, WH Master, Audit, Settings)
- `WHManager`, `WHSupervisor`, `WHAssociate` — warehouse operations

## Audit

All AIWMS-owned table changes captured in `AIWMS.dbo.AiwmsAuditLog` via EF Core `SaveChangesInterceptor`. Plus explicit step logs (`Building.Container`, `Building.Box`, `Building.Item`, `Building.CheckIn`, `Building.Checkout`, `Building.Clear`).
