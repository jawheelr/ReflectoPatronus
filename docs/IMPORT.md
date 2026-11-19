# Importing GlassQuote 7

`tools/importer/import_gq7.py` copies a restored GQ7 (`gqdata`) SQL Server database into PostgreSQL. GQ7 is only ever read.

## Run
```bash
# 1. restore legacy/Copy.bak into a SQL Server container (see docs/GQ7-SCHEMA.md), schema applied to Postgres:
./scripts/generate-dev-certs.sh          # creates the admin-client cert the importer uses (mTLS)
./scripts/migrate.sh docker
export MSSQL_PASSWORD=...                # sa password of the SQL Server container (MSSQL_CONTAINER, default gq7-mssql)

./scripts/import-gq7.sh --dry-run        # full run incl. reconciliation, then ROLLBACK (~1 min, skips attachments)
./scripts/import-gq7.sh --reset          # real import; --reset wipes previously imported data first
```
Options: `--skip-attachments`, `--skip-audit-log`, `--tz ZONE` or env `IMPORT_TZ=ZONE` (IANA zone GQ7 recorded its naive timestamps in; default America/Chicago, **confirm with the shop**; it only affects timestamps, not plain dates). The report is written to `out/import-report.json` (git-ignored). Exit code 1 means an unexplained discrepancy.

## What it guarantees
- **Everything is accounted for:** for every source table `source = loaded + quarantined + skipped`, or the run fails. Quarantined rows keep their full content as JSON in `import_quarantine`.
- **Money reconciles:** sum of line amounts, AR net and payments each equal source totals (loaded + quarantined). Among loaded documents, computed total (lines + tax) must equal the AR invoice debit.
- **Nothing real is silently dropped:** a dropped column that varies is preserved per row in `legacy_extra`; broken links (e.g. a customer number that no longer exists) are recorded there too. Constant/empty dropped columns are only counted.
- **Secrets are not imported:** `GQUSER.User_Password`, SMTP/Qix passwords, Kust token, `Cust_CreditCard`. The report states how many source rows had values.
- **Legacy ids are kept** as primary keys; sequences are advanced afterwards.
- Lookup codes used by data but missing from their reference table get a labelled placeholder row (listed in the report).
- Attachments commit in batches, are resumable (re-run without `--reset`), and are stored once per distinct SHA-256.

## First dry run on the real data (2026-11-19, rolled back, 51 s)
| Check | Result |
|---|---|
| Line sum / AR net / payments vs source | all match |
| Document total vs AR invoice debit | 30,385 of 30,385 match |
| Orphans quarantined | 5,801 lines, 1,497 tax rows, 2,636 AR rows + 1,083 payments (deleted customers), 27 revisions, 2 customer prices, 4 thicknesses, 1 status |
| Pricing | 1,014 loaded = 338 thicknesses x 3 price categories (complete); 2,214 stale rows from deleted thicknesses quarantined |
| Documents without a customer | 931 (kept, with the old customer number in `legacy_extra`) |
| Skipped | 200 zero-value AR rows |
| Placeholder codes | tax groups/codes `9.25`, `9.75`, `EXEMPT`; customer category `CASH` (documents reference codes that were never defined) |
| `Inv_Cust_BillorShip` | only `B` occurs, so `addr_is_ship` is always false |
