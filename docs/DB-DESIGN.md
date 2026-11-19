# PostgreSQL schema design (modelled on GlassQuote 7)

Source analysis: [GQ7-SCHEMA.md](GQ7-SCHEMA.md). Migrations: [db/migrations/](../db/migrations/), applied by `scripts/migrate.sh`.

## Principles
1. **Keep every business fact, drop every dead column.** GQ7 carried auto-glass, EDI, claims, Qix, AGRSS and inventory columns. In this database all of them are 100% empty or constant. The importer must *assert* a dropped column is empty before discarding it, and fail loudly if not.
2. **Preserve legacy ids as primary keys** (`INSERT ... OVERRIDING SYSTEM VALUE`, then `setval`). Printed documents, AR references and `Lin_Parent` links stay valid, and there is no `legacy_id` column or second index to pay for. (This replaces the earlier plan's `legacy_id` columns; fine for a one-time, single-source migration.)
3. **Real foreign keys, real types.** GQ7 had zero FKs. Orphans (5.8k lines, 1.5k tax rows, 2.5k AR rows, 1k payments, 75 attachments) go to `import_quarantine` as JSON instead of being loaded or silently dropped.
4. **Derive, don't store, totals.** See below.

## Where the efficiency comes from
| Change | Effect |
|---|---|
| Dead columns removed | `GQINV` 106 -> 47 cols, `GQLIN` 45 -> 23, `GQCUST` 77 -> 29 (+ phone table), `GQAR` 12 -> 9, `GQPAY` 12 -> 8 |
| Right-sized types: `text` not `varchar(500)`, `smallint` for thickness/seq/user, `date` for pure dates, `boolean`, `numeric(14,2)` | smaller rows, no padding |
| Columns ordered by alignment (8-byte, 4-byte, 2-byte, bool, varlena) | no alignment padding |
| Debit/credit pair -> one signed `amount` (never both non-zero: verified on 70k rows); polymorphic `parent_id/parent_src` -> real FKs | smaller, joinable ledger |
| 5 fixed phone/ext column pairs -> `customer_phone` (4 slots were ~empty) | pay only for numbers that exist |
| `GQNote` side table (1 note per owner) -> inline nullable text columns | one fewer join, NULL is free |
| Repeated `Create_UserID varchar(10)` x all tables -> `smallint` FK to `app_user` | per-row savings, referential integrity |
| Covering indexes `INCLUDE (amount, taxable, source)` on lines, `INCLUDE (amount)` on ledger | totals/balances are index-only scans |
| BRIN on `document.doc_date`, partial indexes on sparse columns (PO, claim, source doc) | tiny indexes |
| `pg_trgm` GIN on customer name | indexed `ILIKE '%smi%'` search (verified usable) |
| Attachments: metadata split from bytes; bytes content-addressed by SHA-256; `STORAGE EXTERNAL` | duplicates stored once; no wasted compression on JPEGs; metadata scans never drag in photos |

**Measured (synthetic data at real GQ7 row counts, rolled back):** `document_line` = 124 bytes/row heap, 50 MB with indexes for 278k rows, versus GQ7's `GQLIN` at 213 MB. A customer's 50 most recent documents with totals runs in ~0.2 ms. These are estimates (synthetic text is shorter than real); re-measure after the real import. The 9.8 GB of attachments is incompressible JPEG, so expect roughly that size minus any exact duplicates.

## Totals: a correction to GQ7's own views
GQ7's `InvoiceTotals` view uses `Lin_A_Kind <> 'LAA'`, which is NULL (unknown) for every non-labor line, so it silently drops them from the subtotal; flat-glass totals were evidently computed in application code. Verified against the data instead: **document total = SUM(line amount) + SUM(tax amount)**, equal to the AR debit on **30,635 of 30,641** invoices. The 6 mismatches are the first thing the importer's reconciliation report should list. `document_totals` implements this rule. Tax *amounts* are stored (GQ7 rounding rules are not reproducible from percent: 99.3% of rows match simple rounding).

## Decisions the data forced
- `Inv_Doc_Type` is `I` invoice, `Q` quote, `W` work order, `C` credit, `R` (2 rows, unclassified). One `document` table, as in GQ7. Document number is unique per (company, type, prefix, number, suffix): verified no duplicates.
- Line source `USR/G/P/LAA` -> enum `custom/glass/part/labor`. `Lin_A_Kind` was redundant with it. 66% of lines are zero-amount `custom` text lines.
- `Lin_Parent` references a sibling line's `seq` in the same document (verified 228/228), so it is `parent_seq`.
- Documents keep a **customer snapshot** (10k differ from the current customer) and `customer_id` is NULL where the customer was deleted (931 docs).
- `bill_to_customer_id` is NULL when it equals the customer (true for all but 3 documents).
- AR `purpose=3` rows are paired transfers between two invoices; `related_document_id` holds the counterpart.
- Line part codes are snapshots (40 codes are not in the parts table), not FKs.
- `part_price` FK to `part_thickness`: 2,214 of 3,228 GQ7 price rows point at a missing thickness row. The importer dry run shows the 1,014 that load are exactly 338 thicknesses x 3 price categories, i.e. pricing is complete and the rest are stale leftovers (kept in quarantine).

## Not imported, on purpose
`GQUSER.User_Password` (set new credentials), SMTP/Qix passwords and the Kust token, `Cust_CreditCard` (empty; never store card numbers), dot-matrix printer config (`GQPrinter`), `GQUserLog` is kept only as `legacy_audit_log`. Unmodelled single-row config (QuickBooks mapping, enterprise row) goes to `legacy_config` JSON.

## Open questions (need the shop or GQ7 UI)
- Meaning of `Cust_Type` A/B/D, `Pay_Type` D/P, `GQSeq.Seq_Type` 0-7, doc type `R`.
- Is `GQAR` purpose 4 vs 5 (payment credit kinds) distinguishable in the old UI?
- Quote -> work order -> invoice conversion: `Inv_Original_Inv_Num` looks like the link; confirm.
- Printing/forms (invoice layout, statements) are not in the database: they must be rebuilt from paper copies.

## Hardening still to do
The init script grants the app role `CREATE` on `public`; revoke it once migrations are the only DDL path. Row-level concerns (users, permissions) are modelled but enforcement is application-side and not built yet.
