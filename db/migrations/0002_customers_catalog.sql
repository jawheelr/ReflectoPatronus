-- 0002: customers and the flat-glass parts / pricing catalog.

CREATE TABLE customer (
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    company_id integer,
    credit_limit numeric(14,2),
    last_transaction_on date,                  -- GQ Cust_TransDate
    created_by smallint REFERENCES app_user(id),
    updated_by smallint REFERENCES app_user(id),
    type char(1) NOT NULL,                     -- GQ Cust_Type: A / B / D (meaning unconfirmed; preserved as-is)
    tax_exempt boolean NOT NULL DEFAULT false,
    print_statement boolean NOT NULL DEFAULT false,
    permanent boolean NOT NULL DEFAULT false,
    no_prices boolean NOT NULL DEFAULT false,
    number text NOT NULL UNIQUE,               -- Cust_Number: the human-visible account number
    last_name text NOT NULL DEFAULT '',
    first_name text,
    address1 text, address2 text, city text, state char(2), zip text,
    email text,
    contact_name text,
    tax_id text,
    tax_group text REFERENCES tax_group(code),
    price_category text REFERENCES price_category(code),
    category text REFERENCES customer_category(code),
    note_printed text,                         -- was GQNote via Cust_NotId (inlined: one note per owner)
    note_internal text
);
-- Customer search is by name fragment and phone; trigram index makes ILIKE '%smi%' index-assisted.
CREATE INDEX customer_name_trgm ON customer
    USING gin ((lower(last_name || ' ' || coalesce(first_name, ''))) gin_trgm_ops);
CREATE INDEX customer_zip ON customer (zip);

-- GQCUST had five fixed phone/ext column pairs, 4 of them nearly empty. Normalised: only real numbers cost space.
CREATE TABLE customer_phone (
    customer_id integer NOT NULL REFERENCES customer(id) ON DELETE CASCADE,
    slot smallint NOT NULL CHECK (slot BETWEEN 1 AND 5),   -- GQ position preserved (may carry meaning)
    number text NOT NULL,
    ext text,
    PRIMARY KEY (customer_id, slot)
);
CREATE INDEX customer_phone_number ON customer_phone (number);

CREATE TABLE part (                            -- FGPart (207 rows)
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_by smallint REFERENCES app_user(id),
    updated_by smallint REFERENCES app_user(id),
    uom_id smallint REFERENCES unit_of_measure(id),
    labor_uom_id smallint REFERENCES unit_of_measure(id),
    source char(1) NOT NULL,                   -- Fgp_Source (G glass / P part, matches line source)
    code text NOT NULL,
    description text,
    note_printed text, note_internal text
);
CREATE UNIQUE INDEX part_code_ci ON part (upper(code));

CREATE TABLE part_thickness (                  -- FGPartThick (342 rows)
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_id integer NOT NULL REFERENCES part(id) ON DELETE CASCADE,
    thick_num smallint NOT NULL,
    thick_den smallint NOT NULL,
    markup_category text,
    UNIQUE (part_id, thick_num, thick_den)
);

-- FGPricing (3,228 rows). NOTE: in the GQ7 export 2,202 rows reference a part_thickness id that does not exist.
-- The importer cannot enforce an FK for them: orphans go to import_quarantine, and this FK covers the rest.
CREATE TABLE part_price (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    part_thickness_id integer NOT NULL REFERENCES part_thickness(id) ON DELETE CASCADE,
    price_category text REFERENCES price_category(code),
    price1 numeric(14,2), price2 numeric(14,2), price3 numeric(14,2), price4 numeric(14,2),
    min_price numeric(14,2),
    labor_price numeric(14,2),
    min_labor numeric(14,2),
    taxable boolean NOT NULL DEFAULT true,
    UNIQUE NULLS NOT DISTINCT (part_thickness_id, price_category)
);

CREATE TABLE customer_part_price (             -- FGCUSPRC (48 rows): per-customer price overrides
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customer(id) ON DELETE CASCADE,
    part_id integer NOT NULL REFERENCES part(id) ON DELETE CASCADE,
    thick_num smallint NOT NULL,
    thick_den smallint NOT NULL,
    price_category text REFERENCES price_category(code),
    price numeric(14,2) NOT NULL
);
CREATE INDEX customer_part_price_cust ON customer_part_price (customer_id);

CREATE TABLE glass_loss (                      -- FGLoss: waste allowance per thickness, in fractional inches
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    thick_num smallint NOT NULL, thick_den smallint NOT NULL,
    loss_int smallint NOT NULL, loss_num smallint NOT NULL, loss_den smallint NOT NULL,
    UNIQUE (thick_num, thick_den)
);
