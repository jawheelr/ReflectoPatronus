-- 0004: payments, AR ledger, attachments, audit, import support.

CREATE TABLE payment (                         -- GQPAY (28k rows)
    paid_at timestamptz NOT NULL,
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customer(id),
    amount numeric(14,2) NOT NULL,
    created_by smallint REFERENCES app_user(id),
    type char(1) NOT NULL,                     -- GQ Pay_Type: D / P (meaning unconfirmed)
    category text REFERENCES pay_category(code),
    description text
);
CREATE INDEX payment_customer ON payment (customer_id, paid_at DESC);

-- GQAR (70k rows) stored debit and credit columns, but never both on one row (verified), so one SIGNED amount
-- (debit positive, credit negative) is lossless and halves the columns. The polymorphic ar_parent_id/ar_parent_src
-- pair is replaced by real foreign keys.
--   purpose 1 = invoice debit, 4/5 = payment credit applied, 3 = paired transfer between two invoices.
CREATE TABLE ar_entry (
    created_at timestamptz NOT NULL DEFAULT now(),
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customer(id),
    document_id integer REFERENCES document(id),
    payment_id integer REFERENCES payment(id),             -- GQ parent_src = 2
    related_document_id integer REFERENCES document(id),   -- purpose 3: the counterpart invoice of a transfer
    amount numeric(14,2) NOT NULL,
    created_by smallint REFERENCES app_user(id),
    purpose smallint NOT NULL CHECK (purpose IN (1, 3, 4, 5))
);
CREATE INDEX ar_entry_customer ON ar_entry (customer_id) INCLUDE (amount);
CREATE INDEX ar_entry_document ON ar_entry (document_id) INCLUDE (amount);
CREATE INDEX ar_entry_payment ON ar_entry (payment_id) WHERE payment_id IS NOT NULL;

CREATE VIEW customer_balance AS               -- replaces GQ7 CustomerBalance
SELECT customer_id, sum(amount) AS balance FROM ar_entry GROUP BY customer_id;

CREATE VIEW receivable AS                     -- replaces GQ7 Receivables
SELECT customer_id, document_id,
       sum(amount) AS balance,
       sum(amount) FILTER (WHERE amount > 0)  AS debits,
       -sum(amount) FILTER (WHERE amount < 0) AS credits,
       max(created_at) AS last_activity_at
FROM ar_entry GROUP BY customer_id, document_id;

-- Attachments: 98% of the GQ7 database (9.8 GB of 10 GB) was photos in one varbinary column.
-- Split metadata from bytes, and store each distinct file once (content-addressed by SHA-256).
-- STORAGE EXTERNAL: JPEG/PDF/PNG are already compressed; skip pglz attempts (saves CPU, keeps fast partial reads).
CREATE TABLE attachment_blob (
    sha256 bytea PRIMARY KEY CHECK (octet_length(sha256) = 32),
    size_bytes integer NOT NULL,
    data bytea NOT NULL
);
ALTER TABLE attachment_blob ALTER COLUMN data SET STORAGE EXTERNAL;

CREATE TABLE attachment (
    created_at timestamptz NOT NULL DEFAULT now(),
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id integer NOT NULL REFERENCES document(id) ON DELETE CASCADE,
    sha256 bytea NOT NULL REFERENCES attachment_blob(sha256),
    file_name text NOT NULL,
    content_type text
);
CREATE INDEX attachment_document ON attachment (document_id);
CREATE INDEX attachment_sha ON attachment (sha256);

-- GQUserLog (265k rows): who touched/deleted which row. Kept for history; not used by the app.
CREATE TABLE legacy_audit_log (
    at timestamptz NOT NULL,
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    row_id integer NOT NULL,
    user_id smallint REFERENCES app_user(id),
    is_delete boolean NOT NULL DEFAULT false,
    table_name text NOT NULL
);
CREATE INDEX legacy_audit_log_row ON legacy_audit_log (table_name, row_id);

-- Single-row/unmodelled GQ7 configuration kept verbatim (QuickBooks mapping, enterprise row, dot-matrix printers).
CREATE TABLE legacy_config (
    key text PRIMARY KEY,
    value jsonb NOT NULL
);

-- Rows the importer could not attach to a parent (GQ7 had no foreign keys; deleted parents left orphans).
-- Kept as JSON for audit and so nothing is silently lost; never read by the app.
CREATE TABLE import_quarantine (
    imported_at timestamptz NOT NULL DEFAULT now(),
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_id integer,
    source_table text NOT NULL,
    reason text NOT NULL,
    row_data jsonb NOT NULL
);
CREATE INDEX import_quarantine_table ON import_quarantine (source_table, reason);
