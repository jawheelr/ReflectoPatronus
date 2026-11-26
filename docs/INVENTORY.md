# Inventory and ordering

Utilities > Inventory & Ordering. Needs the permission `inventory.manage` (Administrators have it).

**It is off until you switch it on, and you can switch it off again at any time** (the checkbox at the bottom of the window).
While off, the program ignores stock completely: invoices use up nothing, the Stock button on paperwork is hidden, and every
`/inventory` call except the switch answers 409. Turning it off keeps all counts and orders.

## How stock is counted
- Per **supplier item** (what you import under Supplier Pricing), in the supplier's own unit (a sheet, a box, each).
- Stock on hand = the sum of the history of receipts, uses, counts and adjustments (`inventory_move`). Nothing else holds a balance.
- Glass and parts are used up automatically only when the part and thickness are **linked to a supplier item** (Supplier Pricing, the same link that prices them).
  A line needs `qty x area-or-1 x link factor` of that item (the factor converts our price unit to the supplier's, for example 1/40 for a 60x96 sheet).
  The billed area is used, so the waste allowance you already charge for is counted too. Labor, custom lines and unlinked parts never touch stock.

## Suppliers and their items
The **Suppliers & items** tab (needs only `inventory.manage`, not the Supplier Pricing permission) is where you add suppliers (name, order email, phone, notes),
add or update single items (SKU, description, cost, unit), and **import items from a file**: a CSV (comma, tab or semicolon) with a header row, or pasted rows.
Columns are matched by their headings and can be changed in the dialog; only SKU is required. Items are matched by SKU (case-insensitive) and updated; a blank cost keeps the old cost.
Optional columns Unit, On hand, Reorder at and Order multiple fill in the inventory settings when "also start counting these" is ticked (items already counted keep their counts).
Cost changes use the normal supplier pricing path: they are kept in the cost history and may propose selling-price changes under Supplier Pricing > Pending changes for you to approve.
Feeds and scheduled price updates are still set up in Supplier Pricing.

## Work orders
- An **open work order** sets stock aside ("committed", shown in the stock list). Nothing is removed yet.
- When the work order is turned into an invoice (or a quote straight into one), its stock leaves, once. Editing or crediting the invoice afterwards does not change stock; use a count or adjustment.
- On a quote or work order, the **Stock** button shows what the job needs against stock on hand, everything set aside, and what is on order.

## Needs estimate
For every tracked item we hold the larger of the **reorder point** or: committed by open work orders + open quotes from the last 60 days times the share of
quotes that turned into jobs over the past year (50% when there are fewer than 10 quotes to judge by) + average daily use over 90 days times
(supplier lead days + days of cover). The shortfall after on-hand and already-ordered stock is rounded up to the item's order multiple.

## Purchase orders
Drafts come from the needs list (one per supplier; items already on an open order are not suggested twice) or by hand.
Draft orders can be edited; **Email order** opens your email program with the order filled in (the supplier's order email is kept on the Suppliers tab),
**Print** gives a printable page, **Download CSV** gives a file for suppliers that take uploads. **Mark as sent** only records that you sent it;
the program does not send email itself. Receiving (whole or partial) adds to stock. Closing a part-received order short stops the rest counting as on order.

## Not covered
Offcuts/remnants are not tracked, there is no per-location stock, and nothing is pushed to supplier systems.

## Using it from other software (API)

Everything in the Inventory window is also a JSON API under `/api/v1/inventory`, so a purchasing system or script can add suppliers, import supplier items and read stock and needs.

1. Make a group with just **Inventory, stock counts and purchase orders** (File → Groups), then a client in that group (File → API Clients (OAuth)). Copy the client ID and secret when they are shown.
2. Get a one-hour token (see `docs/API.md`, "API clients"):

```
TOKEN=$(curl -s https://HOST/api/v1/auth/token -d grant_type=client_credentials \
  -d client_id=rpc_… -d client_secret=rps_… | jq -r .access_token)
```

3. Call the API with `Authorization: Bearer $TOKEN`:

```
# add a supplier
curl -s -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"name":"Acme Glass","email":"orders@acme.example"}' https://HOST/api/v1/inventory/suppliers

# import or update that supplier's items (matched by SKU); "track" also starts counting them
curl -s -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"track":true,"rows":[{"sku":"CL-18","description":"Clear 1/8","cost":52.5,"unit":"sheet","onHand":4,"reorderPoint":2,"orderMultiple":1}]}' \
  https://HOST/api/v1/inventory/suppliers/1/items

# what is on hand, and what to order
curl -s -H "Authorization: Bearer $TOKEN" https://HOST/api/v1/inventory/items
curl -s -H "Authorization: Bearer $TOKEN" 'https://HOST/api/v1/inventory/needs?coverDays=14'
```

Inventory must be switched on (otherwise these routes answer 409). The routes are listed in `src/ReflectoPatronus.Api/Inventory/InventoryEndpoints.cs`; the machine-readable contract is at `/openapi/v1.json` in Development.

