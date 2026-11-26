-- 0015: API clients (OAuth 2.0 client credentials). A client is a service account: an app_user that cannot sign in (no password,
-- is_service = true) and gets its permissions from the groups it belongs to, exactly like a person. The client secret is shown
-- once and only its SHA-256 is stored (it is 256 random bits, so a fast hash is enough). Access tokens are short-lived and opaque.

ALTER TABLE app_user ADD COLUMN is_service boolean NOT NULL DEFAULT false;

CREATE TABLE api_client (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL DEFAULT now(),
    created_by smallint REFERENCES app_user(id) ON DELETE SET NULL,
    client_id text NOT NULL UNIQUE,
    secret_hash bytea NOT NULL,
    secret_rotated_at timestamptz NOT NULL DEFAULT now(),
    user_id smallint NOT NULL UNIQUE REFERENCES app_user(id) ON DELETE CASCADE,
    name text NOT NULL,
    note text,
    expires_at timestamptz,                 -- the secret stops working after this
    last_used_at timestamptz
);

CREATE TABLE api_token (
    access_hash bytea PRIMARY KEY,
    client_id uuid NOT NULL REFERENCES api_client(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    scope text                              -- space-separated permissions this token was narrowed to; null = all of the client's
);
CREATE INDEX api_token_client ON api_token (client_id);
CREATE INDEX api_token_expiry ON api_token (expires_at);
