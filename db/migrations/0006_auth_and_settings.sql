-- 0006: per-device sessions (opaque revocable tokens), login lockout, time-zone settings.
-- Auth is deliberately separate from the API's database mTLS identity (docs/ARCHITECTURE.md).

-- IANA zone name. NULL on a user = fall back to the company default. Validated by the API against pg_timezone_names.
ALTER TABLE app_user ADD COLUMN time_zone text;
ALTER TABLE company  ADD COLUMN time_zone text NOT NULL DEFAULT 'America/Chicago';

ALTER TABLE app_user ADD COLUMN failed_logins smallint NOT NULL DEFAULT 0;
ALTER TABLE app_user ADD COLUMN locked_until timestamptz;

-- One row per signed-in device. Tokens are random 256-bit values; only their SHA-256 is stored, so a database leak
-- does not leak usable tokens. Revoking a lost laptop/iPad = set revoked_at on its row; other devices are unaffected.
CREATE TABLE device_session (
    created_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    access_expires_at timestamptz NOT NULL,
    refresh_expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id smallint NOT NULL REFERENCES app_user(id) ON DELETE CASCADE,
    platform text NOT NULL CHECK (platform IN ('web', 'desktop', 'ios', 'ipados', 'other')),
    device_name text NOT NULL,
    access_hash bytea NOT NULL UNIQUE,
    refresh_hash bytea NOT NULL UNIQUE
);
CREATE INDEX device_session_user ON device_session (user_id) WHERE revoked_at IS NULL;
