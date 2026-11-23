# Accounting: QuickBooks Desktop, IIF and Sage 50 / Peachtree

Everything is under **Utilities → Accounting (QuickBooks, Sage 50)…** (permission `accounting.manage`, held by Administrators by default).
Every setting is optional; anything left blank uses a default name.

## What is sent
Invoices, credit memos and payments, plus the customer each belongs to (sent once, before its first document). Voiding an invoice, credit
memo or payment that was already sent to QuickBooks sends a void on the next run. Nothing dated before the **start date** is sent; if it
is left empty it is the day sending is switched on, so the legacy history is never pushed by accident. Each thing is sent once; the log is
`accounting_export` (one row per target, kind and id: `sent`, `error` or `skipped`). Errors wait until someone presses *Send again*.
Documents are not re-sent when edited after they were sent.

## QuickBooks Desktop, automatically (Web Connector)
QuickBooks Desktop cannot be called from a server. Instead the **QuickBooks Web Connector** (installed with QuickBooks) polls
`POST /qbwc` on this server every N minutes (SOAP, QBXML) and ReflectoPatronus answers with one request at a time:

1. once: the income account, the vendor for sales tax, the Glass / Parts / Labor / Other items and the sales tax items (and groups for documents with more than one tax code); anything that already exists is left alone;
2. customers (`CustomerAdd`), then `InvoiceAdd` / `CreditMemoAdd`, then `ReceivePaymentAdd` applied to the QuickBooks transaction ids returned for the invoices, then `TxnVoid`s.

Set-up (once): in the window, give the address people use to reach ReflectoPatronus, download the `.qwc` file, load it into the Web Connector,
type the password shown in the window, tick the application and set Auto-Run.

* The Web Connector accepts **https** addresses only (or `localhost`). Put ReflectoPatronus behind the HTTPS gateway (see DEPLOYMENT.md).
* `/qbwc` is reachable without a console sign-in, like the update status page, and is protected by its own user name and password (constant-time compare, one-second delay on failure, regenerate any time). With `Gateway:Mode=require` it needs the gateway like everything else.
* The sales tax feature must be switched on in QuickBooks if you charge tax. Documents with tax use a sales tax item per tax code (or a group named after the tax group); QuickBooks recomputes tax itself, so a difference of more than one cent from the amount here is recorded as a note on the item (the item is still marked sent).
* Credit memos are stored negative here and sent positive. Zero-amount note lines are sent as zero-amount lines on the "Other" item.
* Customer names are `Last First (account number)` by default so two customers with the same name never merge; *Name only* and *Account number only* are options. QuickBooks names are at most 41 characters and cannot contain a colon (it is replaced by a dash).
* Try it on a **copy of the company file** first.

## Files
| File | For | How it is brought in |
|---|---|---|
| IIF | QuickBooks Desktop | File → Utilities → Import → IIF Files. Uses account names (A/R, income, sales tax payable, undeposited funds), so those must exist. |
| Sage 50 customers / invoices (and credit memos) / receipts | Sage 50 (Peachtree) | File → Select Import/Export. The files are CSV with a header row named after Sage's fields; map the columns once and Sage remembers the layout. Import customers first. |

Choose a date range; by default only what was not exported to that program before is included (*Include what was exported before* overrides this, *Remember what I export* can be switched off for a trial run). Sage 50 has no interface a server can push to, so it stays file based. Voids are not carried in files.

## API
All under `/api/v1/accounting` and need `accounting.manage`: `GET /` (settings, defaults, counts, last contact), `PUT /config`, `POST /qbwc/password`,
`GET /qbwc.qwc?url=`, `GET /queue?status=pending|error|sent|skipped`, `POST /retry {kind?, refId?}`, `POST /skip {kind, refId}`,
`GET /export/{iif|sage50-invoices|sage50-receipts|sage50-customers}?from=&to=&includeSent=&mark=`.

## Tested / not tested
Tested with a scripted stand-in for the Web Connector (authentication, request order, error and retry, voids, start date, applied payments, repeat runs send nothing) and by checking that every IIF transaction balances. **Not tested against a real QuickBooks, the real Web Connector or Sage 50**; QBXML element order follows Intuit's spec but QuickBooks is the final judge, so use a company file copy first.
