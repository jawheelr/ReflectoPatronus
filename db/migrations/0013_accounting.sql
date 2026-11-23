-- 0013: sending invoices, credit memos, payments and customers to accounting software.
-- accounting_setting holds the (all-optional) configuration and a little bookkeeping; accounting_export is the log of what
-- has been sent where, so nothing goes twice. A row exists per (target, kind, ref): 'sent', 'error' (waits for a retry) or 'skipped'.
CREATE TABLE accounting_setting (
    key text PRIMARY KEY,
    value jsonb NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE accounting_export (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    target text NOT NULL,                       -- qbd (QuickBooks Desktop via the Web Connector), iif, sage50
    kind text NOT NULL CHECK (kind IN ('customer', 'invoice', 'credit', 'payment')),
    ref_id integer NOT NULL,                    -- customer.id / document.id / payment.id
    status text NOT NULL CHECK (status IN ('sent', 'error', 'skipped')),
    external_id text,                           -- QuickBooks TxnID, or the customer name used there
    void_sent boolean NOT NULL DEFAULT false,   -- a void after sending has been passed on as well
    note text,
    attempts integer NOT NULL DEFAULT 0,
    sent_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (target, kind, ref_id)
);
CREATE INDEX accounting_export_status ON accounting_export (target, status);
