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
