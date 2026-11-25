# API (v1)

Base path `/api/v1`. The machine-readable contract is [docs/openapi/v1.json](openapi/v1.json); the TypeScript (web/desktop) and Swift (iOS/iPadOS) clients are generated from it. Re-export it after changing endpoints: `curl localhost:8080/openapi/v1.json` (served only when `ASPNETCORE_ENVIRONMENT=Development` or `OpenApi__Enabled=true`).

## Authentication (per device)
- `POST /auth/login` `{userCode, password, deviceName, platform}` (platform: `web|desktop|ios|ipados|other`) returns an access token (15 min) and refresh token (30 days) bound to **one device session**.
- Send `Authorization: Bearer <accessToken>`. Tokens are opaque 256-bit values; only their SHA-256 is stored.
- `POST /auth/refresh` `{refreshToken}` rotates both tokens; the old refresh token stops working immediately.
- `POST /auth/logout` ends the current session. `GET /me/sessions` lists your signed-in devices; `DELETE /me/sessions/{id}` revokes one (a lost laptop or iPad) without affecting the others.
- Every endpoint requires sign-in except `auth/login`, `auth/refresh` and `/health/*`. 5 failed logins lock the account for 15 minutes; failures for unknown users, bad passwords and locked accounts are indistinguishable.
- **First run:** set `BOOTSTRAP_ADMIN_USER` and `BOOTSTRAP_ADMIN_PASSWORD` (12+ chars) in `.env`. They create that user only if *no* user has a password yet (imported the legacy system users have none by design). Remove them afterwards.

## Time zones
`GET /me` returns the effective `timeZone` (user setting, else company default). `PUT /me/settings {"timeZone": "America/New_York"}` sets it (`null` clears it); names are validated against the server's tz database; `GET /timezones` lists valid names for a picker. Instants are sent as UTC and rendered by the client in this zone; plain dates are never shifted.

## Customers
- `GET /customers?q=&limit=&offset=`: `q` digits-only = account-number/phone prefix, otherwise name fragment (trigram-indexed) or account-number prefix; `%` and `_` are matched literally. Max `limit` 100; `hasMore` replaces a slow total count.
- `GET /customers/{id}`: full record, phones, balance (sum of the AR ledger), `createdAt`/`updatedAt`.
- `POST /customers`: create. Required: `number` (the account code, unique, 20 chars max), `type` (one letter) and a last or first name. Phones are `{slot 1-5, number, ext}`. 201 `{id}`; 409 duplicate code; 400 for invalid values or unknown tax group / price category / category.
- `PUT /customers/{id}`: replace the record (same body as create, plus `updatedAt` exactly as read). **Optimistic concurrency:** if the record changed since that `updatedAt`, 409 and nothing is written. `created_by`/`updated_by` record the signed-in user. 204 on success.
- `DELETE /customers/{id}` (administrators only): 204, or 409 when the customer has documents or ledger entries.
- `GET /lookups/customer`: tax groups, price categories, customer categories and the type letters in use, for drop-downs.

## Maintenance tables and tax groups
Small reference tables share one set of endpoints; reading is open to any signed-in user, changing is **administrator only**. Only tables registered in the server can be addressed (`GET /maintenance` describes them: columns, labels, types).
- Tables: `tax-codes`, `customer-categories`, `price-categories`, `pay-categories`, `staff` (salesmen/installers), `statuses`.
- `GET /maintenance/{table}`, `POST /maintenance/{table}`, `PUT /maintenance/{table}/{key}`, `DELETE /maintenance/{table}/{key}`. A code (key) never changes after creation. 409 when a code already exists or the entry is in use; system statuses cannot be deleted.
- Tax groups: `POST /tax-groups`, `PUT /tax-groups/{code}`, `DELETE /tax-groups/{code}` with `{code, description, codes:[{position 1-5, code}]}`; administrators only, 409 if a customer, document or the company uses the group.

## Documents (quotes, work orders, invoices, credits)
- `GET /documents?type=&customerId=&q=&from=&to=&includeVoided=&limit=&offset=`: newest first. `type` is `Q` quote, `W` work order, `I` invoice, `C` credit memo. `q` matches document number (prefix), PO number (prefix) or customer name (fragment, as printed on the document). `from`/`to` are inclusive document dates (`YYYY-MM-DD`). Voided documents are hidden unless `includeVoided=true`. Max `limit` 100; `hasMore` replaces a total count. Totals are computed only for the returned page.
- `GET /documents/{id}`: header, customer snapshot as printed (the current customer record may differ), job site, lines, tax lines, installers, status history, revisions and `totals`.
- **Totals rule:** `total = SUM(line amount) + SUM(tax amount)`; the breakdown (`glass`, `part`, `labor`, `custom`, `taxable`) is by line source. This equals the AR invoice debit on every loaded invoice (see [DB-DESIGN.md](DB-DESIGN.md)); the legacy system's own totals view is not used.
- **Quotes, work orders and invoices are editable; credit memos are fixed once created** (`editable` is true for an unvoided Q, W or I). Detail also returns `balance` (the document's ledger balance) and `paid` (payments and deposits applied).
- `POST /documents/calculate` `{customerId, priceCategory?, taxGroup?, discountPercent?, lines[]}`: prices a draft and returns priced lines, tax lines, subtotal, tax, total and warnings. Nothing is saved. Lines: `{source: glass|part|labor|custom, qty, partId, thickNum, thickDen, widthIn, heightIn, units, priceLevel, rate, unitPrice, amount, description, taxable}`; everything price-related is optional and filled from the catalog.
- `POST /documents` creates a **quote** (customer required; number = next in the latest quote's prefix series; printed customer name/address/phones are copied onto it). `PUT /documents/{id}` replaces header and lines (send `updatedAt` as read; 409 if changed; a revision row is added). `POST /documents/{id}/void` voids any document (409 while payments are applied). Clients never send totals: the server prices every save.
- `GET /catalog/parts?q=&source=G|P`: parts matching a code or description, with their thicknesses, for pickers.
- **Pricing rules** (industry basics, in `Documents/PricingEngine.cs`): glass per sq ft = (width + waste) x (height + waste) / 144 where waste comes from the loss table per thickness; piece price = max(area x rate, part minimum); rate = line `rate` > customer special price > the part's price for the document's category (price level 1-4, first non-empty); parts = qty x units x price; labor/custom = typed amounts; document discount % scales every line; tax per tax code of the tax group on taxable lines (labor only when the code taxes labor, non-calculative codes skipped, exempt customers none); half-away-from-zero rounding to cents.
- The `R` type (2 legacy rows, unclassified) is listed but cannot be filtered with `type=R` meaningfully until its meaning is confirmed.

## Work orders, invoices, credit memos
- `POST /documents/{id}/convert` `{to: "W"|"I"|"C", lineSeqs?, force?}`:
  - `Q -> W` creates a work order; `Q -> I` creates an invoice (counter sale); `W -> I` turns the work order **into** the invoice (same row and number, invoice date = today). Work orders, invoices and credit memos share one number series, as in the legacy data. Converting a quote that already has a live work order or invoice is a 409 unless `force`.
  - `I -> C` creates a credit memo for all lines or the listed `lineSeqs`; amounts and tax are negative (tax scaled to the taxable share credited).
  - Returns `{id, number, type, total}`.
- `POST /documents/{id}/printed` stamps the "Printed" time.
- `GET /company`: the shop's own details for print headers.

## Ledger rules (`Documents/Ledger.cs`)
The ledger is append-only: a signed amount per row (debit positive, credit negative). Corrections add offsetting rows, never edits.
- Invoice: purpose 1 debit = total. Editing an invoice posts only the difference. Credit memo: purpose 1 = -total, then a purpose 3 transfer (+total on the credit memo, -total on the invoice, each naming the other).
- Payment: purpose 4 credit per document applied; deposit (type `D`): purpose 5. Unapplied money is a credit row with no document ("on account").
- Voiding a document posts reversing rows and is refused while payments are applied; voiding a payment reverses its rows and keeps it listed as voided.

## Receivables and payments
- `GET /receivables?q=&credit=&limit=`: customers with a balance due (or, with `credit=true`, a credit), largest first.
- `GET /customers/{id}/account`: balance, on-account amount, open documents (total, paid, owed) and recent payments.
- `GET /payments?customerId=&limit=`; `POST /payments` `{customerId, amount, type: P|D, category?, description?, paidAt?, applications:[{documentId, amount}], autoApply?}`: `autoApply` pays the oldest invoices first. 400 if more is applied than received; 409 if a document is voided or would be overpaid.
- `POST /payments/{id}/void`: administrators only.

## Catalog and pricing
- `GET /catalog/items?q=&source=G|P`, `GET /catalog/items/{id}` (thicknesses, each with prices per price category: levels 1-4, minimum, labor, minimum labor, taxable), `POST/PUT/DELETE` (administrators). A PUT replaces the part, its thicknesses and prices in one transaction (send `updatedAt` as read, 409 if changed); prices are matched by thickness and category and upserted so supplier price-change history is kept. A part that appears on documents cannot be deleted.
- `GET /catalog/uoms`; `GET/PUT /catalog/loss` (waste allowance per thickness; PUT replaces the table).
- `GET/PUT /customers/{id}/prices`: special prices (PUT replaces them; administrators).

## Roles, groups and permissions
Access is role-based: a **group** is a set of permissions, a user is in any number of groups and may do whatever any of them allows (`*` = everything, held only by the built-in Administrators group). Permissions are: `customers.read|write|delete`, `documents.read`, `quotes.write`, `workorders.write`, `invoices.write`, `credits.write`, `documents.void`, `receivables.read`, `payments.write`, `payments.void`, `catalog.write`, `maintenance.write`, `pricing.manage`, `reports.view`, `company.write`, `users.manage`, `transfer.manage`, `utilities.cleanup`. Reference data (tax, categories, price lists, lookups, parts pickers, company details) is readable by every signed-in user. A refused call is `403 {error, needs:[...]}`; changes to a user's groups or a group's permissions apply on the next request.
- `GET /me` includes `permissions` and `groups`. `GET /permissions` (catalog), `GET/POST /groups`, `PUT/DELETE /groups/{id}` (`users.manage`). The Administrators group cannot be changed or deleted, built-in groups cannot be deleted, and a group with members cannot be deleted.
- Users: `POST /users {code, name, password, groupIds[], active}` (at least one group), `PUT /users/{id} {name, active, groupIds[]}`. You cannot remove your own Administrators membership or deactivate yourself, and one active administrator who can sign in must always remain. `app_user.is_admin` is kept as a derived flag.
- Per type checks happen inside the handler: editing a document needs that type's write permission, converting needs the target type's (`W` workorders, `I` invoices, `C` credits).

## Updates
`GET /updates` (current commit, newest commit on the tracked branch, whether you are behind, whether an updater is present and the update is already downloaded, how to update), `POST /updates/check` (also asks the updater to download a newer commit), `POST /updates/apply` `{backup}` (asks the updater to install it; 202 with the progress page URL, 409 if no updater, nothing newer, or one already running), `POST /updates/dismiss` (`users.manage`). Anonymous, because they must survive the restart: `GET /update-status` (progress of the current or last job) and the page `GET /update-progress`. A background service checks GitHub once a week (`Updates__IntervalDays`, `Updates__Repository`, `Updates__Branch`, `Updates__Enabled=false` to disable); only the repository name is sent, with a generic user agent. The running commit comes from `RP_BUILD_SHA` (Docker `GIT_SHA` build arg) or `/opt/reflectopatronus/COMMIT` (LXC).

## Users, company, utilities
- `POST /me/password` `{current, new}` (any user; 12+ characters; signs out other devices).
- `GET/POST /users`, `PUT /users/{id}` `{name, active, isAdmin}`, `POST /users/{id}/password`. You cannot demote or deactivate yourself and one active administrator must remain.
- `PUT /company`. `POST /utilities/delete-quotes` and `/utilities/delete-inactive-customers` with `{before, dryRun}`.
- `POST /customers/{id}/apply-credits`: uses on-account money and over-credited documents against open invoices, oldest first.

## Reports
`GET /reports` lists them (id, group, parameters, columns); `GET /reports/{id}?from=&to=&asOf=&customerId=&type=` returns `{columns, rows}` (max 20,000 rows). Each report is one SQL statement in `Reports/ReportEndpoints.cs`.

## Database transfer (large databases)
`GET /admin/transfer/files`, `PUT /admin/transfer/files/{name}` (stream an upload), `DELETE`, `POST /admin/transfer/export {attachments}` and `POST /admin/transfer/import {file, mode, overwrite, dryRun}` start background jobs (202 + id), `GET /admin/transfer/jobs/{id}` reports progress. `POST /admin/transfer/ticket {file?, attachments}` returns a short-lived URL the browser can download natively (stored files support Range requests). The older `GET /admin/export` and `POST /admin/import` stream through the request and remain for scripts.

## Testing
`scripts/smoke-roles.py` (86 checks) runs every built-in group against a matrix of 35 gated endpoints plus real flows (a Sales user cannot invoice, a Counter user cannot void, custom groups, live permission changes, last-administrator rules).

`scripts/smoke-admin.sh` (80 checks) covers the catalog, special pricing, loss table, users and passwords, company, credits and every report; it removes what it creates.

`scripts/smoke-workflow.sh` (40 checks) walks quote -> work order -> invoice -> payments -> credit memo -> voids against a running stack and removes everything it creates.

`scripts/smoke-api.sh` runs 41 end-to-end checks against a running stack (auth, rotation, revocation, time zones, customer and document search, totals consistency, injection and wildcard handling).

## Supplier pricing (admin only)
`app_user.is_admin` gates everything under `/api/v1/pricing` (403 otherwise; `GET /me` returns `isAdmin`). The bootstrap admin is an admin.

Selling price (`part_price.price1`) = supplier unit cost x link factor x (1 + category `markup_pct`/100), rounded to cents. Categories with no markup are never touched. **Nothing changes live prices except approving a batch**; quotes/invoices keep their own prices.

| Endpoint | Purpose |
|---|---|
| `GET/POST /pricing/suppliers`, `PUT/DELETE /pricing/suppliers/{id}` | Suppliers: source `manual`, `upload` or `feed` (https URL, optional write-only auth header, refresh hours, column mapping, csv/json) |
| `GET /pricing/suppliers/{id}/items` | Supplier items and costs |
| `POST /pricing/suppliers/{id}/costs` | Manual cost rows `{rows:[{sku,description,cost}]}` |
| `POST /pricing/suppliers/{id}/adjust` | `{percent}` applied to all of the supplier's costs |
| `POST /pricing/suppliers/{id}/import` | `{content}` CSV/JSON text, parsed with the supplier's column map |
| `POST /pricing/suppliers/{id}/run` | Fetch the feed now (scheduler does this every `refresh_hours`, checked every 5 min) |
| `GET /pricing/links`, `PUT/DELETE /pricing/links/{thicknessId}`, `GET /pricing/thicknesses?q` | Link a glass/part thickness to a supplier item + factor |
| `GET /pricing/markups`, `PUT /pricing/markups/{code}`, `POST /pricing/recalculate` | Category markups (null = manual) |
| `GET /pricing/batches[?status]`, `GET /pricing/batches/{id}`, `POST .../approve`, `POST .../reject` | Review proposed changes (old vs new) |

Each ingest returns `{added, changed, unchanged, batchId, priceChanges}`. A newer pending batch supersedes an older one for the same supplier. Feed fetching is https-only, no redirects, refuses private/loopback addresses (SSRF guard), 10 MB / 30 s limits.

## Database export / import (admin only)
`GET /api/v1/admin/export[?attachments=false]` streams the whole database as a `.rpb` file (gzip of one JSON row per line, in foreign-key order, ending with a row-count trailer). `POST /api/v1/admin/import?mode=merge|replace&overwrite=true|false&dryRun=true|false` takes that file as the raw request body.

- **merge** adds rows whose ID is not present. With `overwrite=false` existing rows are left alone; with `overwrite=true` they are replaced by the file's version.
- **replace** deletes every table the file carries (children first), then loads the file. A file exported without attachments is refused if the database has attachments.
- **dryRun** does everything and rolls back; the response is the per-table report either way (`inserted`, `updated`, `skipped`, `deleted`).
- All-or-nothing: one transaction; a damaged, truncated or wrong-version file changes nothing. The file's table structure must match the server's (same release, migrations applied).
- Never exported or overwritten: sessions, password hashes, admin flags, lockout state, supplier feed credentials. Users are only added (without a password), never changed or deleted; a file whose user ids mean different people than on this server is refused.
- Records are matched by their primary key (the original IDs are preserved by the importer, so a file from this server or from the legacy import lines up).

## Company logo
`GET /company/logo` (any signed-in user; 404 if none), `PUT /company/logo` with the image as the body and its `Content-Type` (png, jpeg, webp or svg; max 1 MB), `DELETE /company/logo` (`company.write`). `GET /company` returns `hasLogo`. The logo is part of database export/import. The print dialog uses it as the watermark (the company name when there is no logo).

## Inventory
See [INVENTORY.md](INVENTORY.md): `/api/v1/inventory/*` (permission `inventory.manage`; the on/off state and the per-document stock check are open to anyone who can read paperwork).

## Accounting
See [ACCOUNTING.md](ACCOUNTING.md): `/api/v1/accounting/*` (permission `accounting.manage`) and the QuickBooks Web Connector endpoint `POST /qbwc` (own user name and password).
