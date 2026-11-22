-- 0009: payments can be voided (the ledger is append-only: a void posts reversing entries rather than deleting history).
ALTER TABLE payment ADD COLUMN voided boolean NOT NULL DEFAULT false;
ALTER TABLE payment ADD COLUMN voided_at timestamptz;
ALTER TABLE payment ADD COLUMN voided_by smallint REFERENCES app_user(id);
