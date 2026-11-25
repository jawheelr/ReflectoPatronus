-- 0014: inventory and purchasing. Everything here is inert until an administrator switches inventory on (system_state key 'inventory').
-- Stock is counted per supplier item (the same items prices are linked to, see part_cost_source) in the supplier's own unit
-- (a sheet, a box, each). Stock on hand is the sum of inventory_move; nothing else holds a running balance.
-- Work orders reserve nothing in the table: "committed" is worked out live from open work orders. Stock leaves when an invoice is made.

ALTER TABLE supplier ADD COLUMN email text, ADD COLUMN phone text, ADD COLUMN order_notes text;

CREATE TABLE inventory_item (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now(),
    supplier_item_id integer NOT NULL UNIQUE REFERENCES supplier_item(id) ON DELETE CASCADE,
    unit text NOT NULL DEFAULT 'each',
    reorder_point numeric(14,3) NOT NULL DEFAULT 0 CHECK (reorder_point >= 0),
    order_multiple numeric(14,3) NOT NULL DEFAULT 1 CHECK (order_multiple > 0),   -- orders are rounded up to a multiple of this (a bundle, a crate)
    lead_days integer NOT NULL DEFAULT 7 CHECK (lead_days BETWEEN 0 AND 365),
    location text,
    active boolean NOT NULL DEFAULT true
);

CREATE TABLE purchase_order (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now(),
    created_by smallint REFERENCES app_user(id),
    supplier_id integer NOT NULL REFERENCES supplier(id),
    status text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'sent', 'partial', 'received', 'cancelled')),
    sent_at timestamptz,
    expected_date date,
    note text
);
CREATE INDEX purchase_order_status ON purchase_order (status, created_at DESC);

CREATE TABLE purchase_order_line (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    po_id integer NOT NULL REFERENCES purchase_order(id) ON DELETE CASCADE,
    item_id integer NOT NULL REFERENCES inventory_item(id),
    qty numeric(14,3) NOT NULL CHECK (qty > 0),
    unit_cost numeric(14,4) NOT NULL DEFAULT 0,
    received_qty numeric(14,3) NOT NULL DEFAULT 0 CHECK (received_qty >= 0)
);
CREATE INDEX purchase_order_line_po ON purchase_order_line (po_id);

CREATE TABLE inventory_move (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    item_id integer NOT NULL REFERENCES inventory_item(id) ON DELETE CASCADE,
    at timestamptz NOT NULL DEFAULT now(),
    qty numeric(14,3) NOT NULL,                       -- + in, - out
    kind text NOT NULL CHECK (kind IN ('receive', 'use', 'adjust', 'count')),
    document_id integer REFERENCES document(id) ON DELETE SET NULL,
    po_id integer REFERENCES purchase_order(id) ON DELETE SET NULL,
    note text,
    user_id smallint REFERENCES app_user(id)
);
CREATE INDEX inventory_move_item ON inventory_move (item_id, at DESC);
CREATE UNIQUE INDEX inventory_use_once ON inventory_move (item_id, document_id) WHERE kind = 'use';   -- an invoice takes stock out once
