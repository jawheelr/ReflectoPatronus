-- 0016: supplier stock availability. A supplier can be polled for what it has in stock, either through an API (JSON or CSV)
-- or by reading a web page (a pattern picks the quantity out of the page). This is separate from the cost feed in 0007:
-- it never changes prices or our own counts; it only records what the supplier says, and when.

ALTER TABLE supplier
    ADD COLUMN avail_mode text NOT NULL DEFAULT 'none' CHECK (avail_mode IN ('none', 'api', 'scrape')),
    ADD COLUMN avail_url text,                  -- https only; "{sku}" is replaced by each item's SKU (one request per item), otherwise one request returns a list
    ADD COLUMN avail_auth_header text,          -- e.g. 'Bearer abc'. Write-only: never returned by the API
    ADD COLUMN avail_format text NOT NULL DEFAULT 'json' CHECK (avail_format IN ('json', 'csv', 'html')),
    ADD COLUMN avail_map jsonb NOT NULL DEFAULT '{}',
    ADD COLUMN avail_refresh_hours integer CHECK (avail_refresh_hours BETWEEN 1 AND 720),
    ADD COLUMN avail_last_at timestamptz,
    ADD COLUMN avail_status text,               -- ok | partial | error
    ADD COLUMN avail_message text;

CREATE TABLE supplier_stock (
    supplier_item_id integer PRIMARY KEY REFERENCES supplier_item(id) ON DELETE CASCADE,
    qty numeric(14,3),                          -- null when the supplier only says in/out of stock
    status text NOT NULL CHECK (status IN ('in_stock', 'out', 'unknown')),
    note text,
    checked_at timestamptz NOT NULL DEFAULT now()
);
