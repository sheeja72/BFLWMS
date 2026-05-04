<!-- Short description of WHAT changed and WHY -->

## What

-

## Why

-

## How to test

-

## Checklist

- [ ] Builds clean locally (`dotnet build`)
- [ ] No new warnings introduced
- [ ] If touching SQL — also updated `db/qa_check_columns.sql` if any column reference changed
- [ ] If touching Razor — verified the page renders without runtime errors
- [ ] If touching auth / Program.cs — local smoke test still works
- [ ] No secrets, connection strings, or `.protected` files committed
- [ ] At least 1 approving review
