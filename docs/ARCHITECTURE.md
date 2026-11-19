# Architecture decisions (initial)

## Application boundary
- React web client and native SwiftUI iOS client call the same versioned REST API.
- Only the API and explicitly authorized migration/backup workers access PostgreSQL.
- PostgreSQL's port remains private to the Docker network.

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
