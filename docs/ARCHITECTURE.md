# Architecture decisions (initial)

## System shape: central server, API-only clients
One central server (Docker or Proxmox LXC, see DEPLOYMENT.md) runs PostgreSQL and the API. **Every client talks to the API; none talks to PostgreSQL** (unlike GQ7, whose desktop clients connected straight to SQL Server). Logins, permissions and the totals rules live in the API.

| Client | Technology | Notes |
|---|---|---|
| Web | React + TypeScript | Browser access; also the UI the desktop app wraps |
| Desktop app (installed on each computer) | Same React UI in a Tauri shell | Thin client of the same API; one UI codebase for web and desktop. Electron only if a Tauri limitation forces it |
| iPhone / iPad | Native SwiftUI, one app with an adaptive iPad layout | Offline field work: local store plus a conflict-aware sync queue |

Decisions that follow from this:
- **API first.** All clients depend on one versioned REST contract published as OpenAPI (`/api/v1`). The TypeScript and Swift clients are generated from it. No client is built before the API slice it needs exists.
- **Per-device sign-in.** Each user signs in on each device. Short-lived access tokens plus refresh tokens that are bound to a device record, so a lost laptop or iPad can be revoked without affecting anyone else. User authentication is separate from the API's own database mTLS identity. Passwords are never imported from GQ7.
- **Time zones.** Instants are stored as `timestamptz` and sent as UTC. Each client renders them in the user's chosen zone: a per-user setting held on the server (default: the device's zone) with a company default. Plain dates (document dates) are never shifted. The importer's `--tz` is unrelated: it only states what zone GQ7 recorded its naive timestamps in.
- Only the API and explicitly authorized migration/backup workers access PostgreSQL.
- PostgreSQL's port remains private (internal Docker network / private Proxmox bridge with firewall rules).

## Database security
- PostgreSQL requires TLS 1.3 or later.
- TCP database connections require a trusted client certificate (`clientcert=verify-full`) and password authentication.
- Client certificate CN is aligned with the API database username in the development configuration.
- API readiness remains closed until the deployed connection is proven to use the approved PQC key-exchange group.

## PQC implementation gate
PostgreSQL's `pg_stat_ssl` does not expose the negotiated key-exchange group. TLS 1.3 and a valid client certificate alone do not prove PQC. The stock `postgres:17-bookworm` image is not assumed to support ML-KEM. A production candidate must pin a compatible PostgreSQL/OpenSSL build, configure strict hybrid-group enforcement, verify .NET/Npgsql client support, and include a handshake-level test that proves no classical-only fallback is possible. The development certificate signatures are classical ECDSA; PQC key exchange and PQC certificate signatures are separate requirements.

## Portable backup direction
The planned `.rpb` archive should include:
- versioned manifest and schema version
- canonical entity exports in JSON Lines or similarly portable encoding
- attachment payloads
- SHA-256 checksums
- export counts and validation results
- explicit mapping/version metadata

Do not include database passwords, TLS private keys, or CA private keys in portable data backups.

## Legacy migration
Treat the legacy database as read-only during discovery. Preserve source IDs and relationships, map fields explicitly, report unknown fields, and verify financial totals before cutover.
