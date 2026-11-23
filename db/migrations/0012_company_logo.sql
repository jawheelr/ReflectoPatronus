-- The shop's logo (shown on the Company screen, usable as the printed watermark). Small images only; the API enforces 1 MB.
ALTER TABLE company ADD COLUMN IF NOT EXISTS logo bytea, ADD COLUMN IF NOT EXISTS logo_type text;
