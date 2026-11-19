-- 0003: documents (quotes / work orders / invoices / credits), lines, tax, installers, revisions.
-- GQ7 kept all four document kinds in one table (GQINV), distinguished by Inv_Doc_Type; so do we.
-- Only flat-glass ('F') documents exist in the export; the Inv_A_* auto-glass, EDI, claims and Qix columns were
-- 100% empty and are intentionally not carried over (importer asserts they are empty before dropping them).

CREATE TYPE line_source AS ENUM ('glass', 'part', 'labor', 'custom');
-- GQ7 Lin_Source: G -> glass, P -> part, LAA -> labor, USR -> custom (user-entered lines, ~66% are zero-amount text).

CREATE TABLE document (
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    printed_at timestamptz,
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    company_id integer NOT NULL DEFAULT 1,
    doc_number integer NOT NULL,
    customer_id integer REFERENCES customer(id),            -- NULL for 931 GQ7 docs whose customer no longer exists
    bill_to_customer_id integer REFERENCES customer(id),    -- NULL = same as customer (differs on only 3 GQ7 docs)
    staff_sales_id integer REFERENCES staff(id),
    doc_date date NOT NULL,
    loss_date date,
    discount_pct numeric(7,4),
    created_by smallint REFERENCES app_user(id),
    updated_by smallint REFERENCES app_user(id),
    doc_type char(1) NOT NULL REFERENCES doc_type(code),
    voided boolean NOT NULL DEFAULT false,
    shop_or_mobile char(1),
    addr_is_ship boolean NOT NULL DEFAULT false,            -- GQ Inv_Cust_BillorShip
    doc_prefix text NOT NULL DEFAULT '',
    doc_suffix text NOT NULL DEFAULT '',
    source_doc_number text,                                  -- Inv_Original_Inv_Num: the quote/WO this came from
    terms text,
    initials text,
    agent_code text,
    po_number text, policy_number text, claim_number text,
    price_category text REFERENCES price_category(code),
    customer_category text REFERENCES customer_category(code),
    tax_group text REFERENCES tax_group(code),
    -- Historic snapshot of the customer as printed on THIS document (differs from current customer in 10k docs).
    cust_last_name text, cust_first_name text,
    cust_address1 text, cust_address2 text, cust_city text, cust_state char(2), cust_zip text,
    cust_phones text[],                                      -- 'number' or 'numberxEXT'
    -- Job-site address (mobile jobs)
    loc_address1 text, loc_address2 text, loc_city text, loc_state char(2), loc_zip text,
    spec_instructions text,
    wo_instructions text,
    note_printed text, note_internal text,
    UNIQUE (company_id, doc_type, doc_prefix, doc_number, doc_suffix)
);
CREATE INDEX document_customer ON document (customer_id, doc_date DESC);
CREATE INDEX document_date ON document USING brin (doc_date);     -- append-mostly by date: tiny index, good for ranges
CREATE INDEX document_po ON document (po_number) WHERE po_number IS NOT NULL;
CREATE INDEX document_claim ON document (claim_number) WHERE claim_number IS NOT NULL;
CREATE INDEX document_source ON document (source_doc_number) WHERE source_doc_number IS NOT NULL;

-- 278k rows in GQ7 at 45 columns (~800 bytes/row on disk); 22 of those columns were always empty/constant.
CREATE TABLE document_line (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id integer NOT NULL REFERENCES document(id) ON DELETE CASCADE,
    qty integer,
    list_price numeric(14,2),
    unit_price numeric(14,2),
    multiplier numeric(8,4) NOT NULL DEFAULT 1,
    amount numeric(14,2) NOT NULL DEFAULT 0,
    uom_price numeric(14,2),
    uom_units numeric(14,4),
    seq smallint NOT NULL,
    parent_seq smallint,                       -- references document_line.seq within the same document (verified in GQ7)
    thick_num smallint, thick_den smallint,
    source line_source NOT NULL,
    taxable boolean NOT NULL DEFAULT false,
    amount_override boolean NOT NULL DEFAULT false,
    list_override boolean NOT NULL DEFAULT false,
    special_price boolean NOT NULL DEFAULT false,
    part_code text,                            -- snapshot, NOT an FK: 40 GQ7 line part codes are not in the parts table
    description text,
    dims text,
    shape text,
    price_category text REFERENCES price_category(code)
);
-- Covering index: totals and line listings never touch the heap.
CREATE INDEX document_line_doc ON document_line (document_id, seq) INCLUDE (amount, taxable, source);

CREATE TABLE document_tax (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id integer NOT NULL REFERENCES document(id) ON DELETE CASCADE,
    tax_code text NOT NULL REFERENCES tax_code(code),
    percent numeric(7,4) NOT NULL,             -- snapshot of the rate at the time
    amount numeric(14,2) NOT NULL,             -- authoritative: GQ7 rounding rules live in app code, not reproducible from percent
    UNIQUE (document_id, tax_code)
);

CREATE TABLE document_installer (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id integer NOT NULL REFERENCES document(id) ON DELETE CASCADE,
    staff_id integer REFERENCES staff(id),
    installer_code text NOT NULL,              -- kept: staff row may be absent
    hours numeric(8,2),
    amount numeric(14,2)
);
CREATE INDEX document_installer_doc ON document_installer (document_id);

CREATE TABLE document_revision (               -- GQREVISE: total snapshot each time a document was revised
    at timestamptz NOT NULL,
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id integer NOT NULL REFERENCES document(id) ON DELETE CASCADE,
    total numeric(14,2),
    created_by smallint REFERENCES app_user(id),
    comment text
);
CREATE INDEX document_revision_doc ON document_revision (document_id, at DESC);

CREATE TABLE document_status_event (           -- GQInvStatus
    at timestamptz,
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id integer NOT NULL REFERENCES document(id) ON DELETE CASCADE,
    status_id integer NOT NULL REFERENCES document_status(id),
    created_by smallint REFERENCES app_user(id),
    note text
);

-- Derived totals. GQ7 stored none. IMPORTANT: GQ7's own InvoiceTotals view is NOT the source of truth for
-- flat glass (its NULL-comparison predicates drop non-labor lines). Verified against the data: total =
-- SUM(line amount) + SUM(tax amount), matching the AR ledger debit on 30,635 of 30,641 invoices.
CREATE VIEW document_totals AS
SELECT d.id AS document_id,
       coalesce(l.lines_total, 0)   AS lines_total,
       coalesce(l.taxable_total, 0) AS taxable_total,
       coalesce(l.labor_total, 0)   AS labor_total,
       coalesce(l.glass_total, 0)   AS glass_total,
       coalesce(l.part_total, 0)    AS part_total,
       coalesce(l.custom_total, 0)  AS custom_total,
       coalesce(t.tax_total, 0)     AS tax_total,
       coalesce(l.lines_total, 0) + coalesce(t.tax_total, 0) AS total
FROM document d
LEFT JOIN LATERAL (
    SELECT sum(amount) AS lines_total,
           sum(amount) FILTER (WHERE taxable)            AS taxable_total,
           sum(amount) FILTER (WHERE source = 'labor')   AS labor_total,
           sum(amount) FILTER (WHERE source = 'glass')   AS glass_total,
           sum(amount) FILTER (WHERE source = 'part')    AS part_total,
           sum(amount) FILTER (WHERE source = 'custom')  AS custom_total
    FROM document_line WHERE document_id = d.id) l ON true
LEFT JOIN LATERAL (
    SELECT sum(amount) AS tax_total FROM document_tax WHERE document_id = d.id) t ON true;
