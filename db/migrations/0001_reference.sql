-- 0001: reference / lookup data.
-- Conventions for the whole schema (see docs/DB-DESIGN.md):
--  * Primary keys are `integer GENERATED ALWAYS AS IDENTITY`. The legacy system ids are PRESERVED by the
--    importer (INSERT ... OVERRIDING SYSTEM VALUE, then setval), so printed document/AR references stay valid.
--  * Columns are ordered widest-alignment first (timestamptz, integer, smallint, bool, then variable-length)
--    to avoid alignment padding. Do not reorder casually.
--  * Money is numeric(14,2); never float. Dates that are pure dates are `date`; real instants are timestamptz.
--  * Codes that the legacy system stored as varchar are `text` + FK to a small lookup table where the set is closed.

CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Immutable helper replicating the legacy system dbo.InvDocNum(prefix, num, suffix): prefix + 7-digit zero-padded number + suffix.
CREATE FUNCTION doc_number_text(prefix text, num integer, suffix text)
RETURNS text LANGUAGE sql IMMUTABLE PARALLEL SAFE AS
$$ SELECT coalesce(prefix, '') || lpad(num::text, 7, '0') || coalesce(suffix, '') $$;

-- The legacy system dbo.ThickStr: 'All' when 0/0, otherwise e.g. '1/4'.
CREATE FUNCTION thickness_text(num integer, den integer)
RETURNS text LANGUAGE sql IMMUTABLE PARALLEL SAFE AS
$$ SELECT CASE WHEN coalesce(num, 0) = 0 AND coalesce(den, 0) = 0 THEN 'All' ELSE num::text || '/' || den::text END $$;

CREATE TABLE doc_type (
    code char(1) PRIMARY KEY,
    name text NOT NULL
);
INSERT INTO doc_type (code, name) VALUES
    ('Q', 'Quote'), ('W', 'Work order'), ('I', 'Invoice'), ('C', 'Credit memo'),
    ('R', 'Unclassified (legacy type R, 2 rows in the legacy system export; meaning unconfirmed)');

CREATE TABLE app_user (
    id smallint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code text NOT NULL UNIQUE,                 -- The legacy system User_ID / Create_UserID value
    name text,
    user_type char(1),                         -- legacy USER.User_Type, meaning unconfirmed
    user_group text,
    company_id integer,
    no_dispatch boolean NOT NULL DEFAULT false,
    active boolean NOT NULL DEFAULT true,      -- false for codes seen only in audit columns
    password_hash text                         -- NEVER imported from the legacy system; set via the new auth flow
);

CREATE TABLE user_permission (
    user_id smallint NOT NULL REFERENCES app_user(id) ON DELETE CASCADE,
    function_id text NOT NULL,
    can_add boolean NOT NULL DEFAULT false,
    can_change boolean NOT NULL DEFAULT false,
    can_delete boolean NOT NULL DEFAULT false,
    can_read boolean NOT NULL DEFAULT false,
    PRIMARY KEY (user_id, function_id)
);

CREATE TABLE company (
    id integer PRIMARY KEY,                    -- legacy id preserved; no identity (one row per location)
    code text NOT NULL,
    name text NOT NULL,
    address1 text, address2 text, city text, state char(2), zip text,
    phone text, fax text, email text, fed_id text,
    tax_group text,                            -- FK added in 0002 once tax_group exists
    default_terms_days integer,
    finance_charge_pct numeric(7,4),
    grace_days integer,
    default_price_category text,
    invoice_note text,
    disclaimer text,
    use_disclaimer boolean NOT NULL DEFAULT false,
    logo bytea,
    flat_options jsonb NOT NULL DEFAULT '{}',  -- Cmpy_F_* flat-glass behaviour flags (rounding, loss, print options)
    legacy_settings jsonb NOT NULL DEFAULT '{}'-- every other legacy CMPY column, minus secrets (SMTP/Qix passwords)
);

CREATE TABLE tax_code (
    code text PRIMARY KEY,
    description text,
    percent numeric(7,4) NOT NULL,
    taxes_labor boolean NOT NULL DEFAULT false,
    non_calc boolean NOT NULL DEFAULT false
);

CREATE TABLE tax_group (
    code text PRIMARY KEY,
    description text
);
CREATE TABLE tax_group_code (                  -- replaces legacy TXGP.TxGp_TCode1..5
    tax_group text NOT NULL REFERENCES tax_group(code) ON DELETE CASCADE,
    position smallint NOT NULL CHECK (position BETWEEN 1 AND 5),
    tax_code text NOT NULL REFERENCES tax_code(code),
    PRIMARY KEY (tax_group, position)
);
ALTER TABLE company ADD FOREIGN KEY (tax_group) REFERENCES tax_group(code);

CREATE TABLE price_category (code text PRIMARY KEY, description text);
CREATE TABLE customer_category (code text PRIMARY KEY, description text);
CREATE TABLE pay_category (code text PRIMARY KEY, description text, undeposited boolean NOT NULL DEFAULT false);

CREATE TABLE unit_of_measure (
    id smallint PRIMARY KEY,
    name text NOT NULL, short_name text, plural_name text
);

CREATE TABLE staff (                            -- legacy SLIN: salespeople / installers
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    company_id integer,
    rate numeric(14,2),
    is_salesman boolean NOT NULL DEFAULT false,
    is_installer boolean NOT NULL DEFAULT false,
    is_region boolean NOT NULL DEFAULT false,
    deleted boolean NOT NULL DEFAULT false,
    code text NOT NULL UNIQUE,
    name text,
    phone text,
    hours text,
    lunch text
);

CREATE TABLE carrier (                          -- legacy CARR: insurance carriers (auto-glass legacy; kept, 214 rows)
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    number text NOT NULL UNIQUE,
    name text NOT NULL,
    safelite_number text
);

CREATE TABLE document_status (                  -- legacy Status
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name text NOT NULL,
    is_system boolean NOT NULL DEFAULT false,
    obsolete boolean NOT NULL DEFAULT false
);

CREATE TABLE number_sequence (                  -- legacy Seq: per-company document counters
    company_id integer NOT NULL,
    seq_type integer NOT NULL,                  -- legacy type codes 0..7; meaning to be confirmed against the legacy system UI
    next_value integer NOT NULL DEFAULT 0,
    PRIMARY KEY (company_id, seq_type)
);

CREATE TABLE zip_code (                         -- ZipData: 80k rows; only the columns the app uses
    zip char(5) NOT NULL,
    state char(2) NOT NULL,
    city text NOT NULL,
    latitude real,
    longitude real,
    county text,
    area_code char(3),
    time_zone char(2),
    zip_type char(1),
    PRIMARY KEY (zip, state, city)
);
