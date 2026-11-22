-- 0011: small server-side state that is not business data (for example when the weekly update check last ran).
CREATE TABLE system_state (
    key text PRIMARY KEY,
    value jsonb NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);
