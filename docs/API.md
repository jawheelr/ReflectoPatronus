# API (v1)

Base path `/api/v1`. The machine-readable contract is [docs/openapi/v1.json](openapi/v1.json); the TypeScript (web/desktop) and Swift (iOS/iPadOS) clients are generated from it. Re-export it after changing endpoints: `curl localhost:8080/openapi/v1.json` (served only when `ASPNETCORE_ENVIRONMENT=Development` or `OpenApi__Enabled=true`).

## Authentication (per device)
- `POST /auth/login` `{userCode, password, deviceName, platform}` (platform: `web|desktop|ios|ipados|other`) returns an access token (15 min) and refresh token (30 days) bound to **one device session**.
- Send `Authorization: Bearer <accessToken>`. Tokens are opaque 256-bit values; only their SHA-256 is stored.
- `POST /auth/refresh` `{refreshToken}` rotates both tokens; the old refresh token stops working immediately.
- `POST /auth/logout` ends the current session. `GET /me/sessions` lists your signed-in devices; `DELETE /me/sessions/{id}` revokes one (a lost laptop or iPad) without affecting the others.
- Every endpoint requires sign-in except `auth/login`, `auth/refresh` and `/health/*`. 5 failed logins lock the account for 15 minutes; failures for unknown users, bad passwords and locked accounts are indistinguishable.
- **First run:** set `BOOTSTRAP_ADMIN_USER` and `BOOTSTRAP_ADMIN_PASSWORD` (12+ chars) in `.env`. They create that user only if *no* user has a password yet (imported GQ7 users have none by design). Remove them afterwards.

## Time zones
`GET /me` returns the effective `timeZone` (user setting, else company default). `PUT /me/settings {"timeZone": "America/New_York"}` sets it (`null` clears it); names are validated against the server's tz database; `GET /timezones` lists valid names for a picker. Instants are sent as UTC and rendered by the client in this zone; plain dates are never shifted.

## Customers
- `GET /customers?q=&limit=&offset=`: `q` digits-only = account-number/phone prefix, otherwise name fragment (trigram-indexed) or account-number prefix; `%` and `_` are matched literally. Max `limit` 100; `hasMore` replaces a slow total count.
- `GET /customers/{id}`: full record, phones, balance (sum of the AR ledger).

## Testing
`scripts/smoke-api.sh` runs 28 end-to-end checks against a running stack (auth, rotation, revocation, time zones, customer search, injection and wildcard handling).
