-- 0007: supplier cost feeds -> reviewed price changes.
-- Sell price (part_price.price1) = supplier unit cost x link factor x (1 + category markup%), rounded to cents.
-- Categories with markup_pct NULL are NOT managed: automation never touches their prices.
-- Automatic runs only create a PENDING batch; nothing goes live until an admin approves it.
-- Existing quotes/invoices store their own prices and are never affected.

ALTER TABLE app_user ADD COLUMN is_admin boolean NOT NULL DEFAULT false;
UPDATE app_user SET is_admin = true WHERE code = 'ADMIN';

ALTER TABLE price_category ADD COLUMN markup_pct numeric(7,2) CHECK (markup_pct IS NULL OR markup_pct > -100);

CREATE TABLE supplier (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now(),
    name text NOT NULL,
    active boolean NOT NULL DEFAULT true,
    source_type text NOT NULL DEFAULT 'manual' CHECK (source_type IN ('manual', 'upload', 'feed')),
    feed_url text,                              -- https only; fetched by the server on the schedule below
    feed_auth_header text,                      -- e.g. 'Bearer abc'. Write-only: never returned by the API
    feed_format text NOT NULL DEFAULT 'csv' CHECK (feed_format IN ('csv', 'json')),
    column_map jsonb NOT NULL DEFAULT '{"sku":"sku","cost":"cost","description":"description"}',
    refresh_hours integer CHECK (refresh_hours IS NULL OR refresh_hours BETWEEN 1 AND 8760),
    last_run_at timestamptz,
    last_status text,                           -- ok | error
    last_message text
);
CREATE UNIQUE INDEX supplier_name_ci ON supplier (lower(name));

CREATE TABLE supplier_item (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    supplier_id integer NOT NULL REFERENCES supplier(id) ON DELETE CASCADE,
    sku text NOT NULL,
    description text,
    unit_cost numeric(14,4) NOT NULL CHECK (unit_cost >= 0),
    cost_updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (supplier_id, sku)
);

CREATE TABLE supplier_cost_history (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    supplier_item_id integer NOT NULL REFERENCES supplier_item(id) ON DELETE CASCADE,
    unit_cost numeric(14,4) NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT now(),
    source text NOT NULL                         -- feed | upload | manual | adjust
);
CREATE INDEX supplier_cost_history_item ON supplier_cost_history (supplier_item_id, recorded_at DESC);

-- Which supplier item prices a glass/part thickness. factor converts supplier unit to our price unit
-- (e.g. cost per 60x96 sheet -> per sq ft is 1/40).
CREATE TABLE part_cost_source (
    part_thickness_id integer PRIMARY KEY REFERENCES part_thickness(id) ON DELETE CASCADE,
    supplier_item_id integer NOT NULL REFERENCES supplier_item(id) ON DELETE CASCADE,
    factor numeric(14,6) NOT NULL DEFAULT 1 CHECK (factor > 0)
);

CREATE TABLE price_change_batch (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now(),
    supplier_id integer REFERENCES supplier(id) ON DELETE SET NULL,
    source text NOT NULL,                        -- feed | upload | manual | adjust | markup
    status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'applied', 'rejected', 'superseded')),
    decided_by smallint REFERENCES app_user(id),
    decided_at timestamptz
);
CREATE INDEX price_change_batch_status ON price_change_batch (status, created_at DESC);

CREATE TABLE price_change (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    batch_id integer NOT NULL REFERENCES price_change_batch(id) ON DELETE CASCADE,
    part_price_id integer NOT NULL REFERENCES part_price(id) ON DELETE CASCADE,
    old_price numeric(14,2),
    new_price numeric(14,2) NOT NULL,
    cost numeric(14,4) NOT NULL,
    markup_pct numeric(7,2) NOT NULL
);
CREATE INDEX price_change_batch_idx ON price_change (batch_id);
