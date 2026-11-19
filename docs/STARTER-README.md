# ReflectoPatronus starter — PostgreSQL edition

Early development scaffold for a Docker-hosted business application with a shared API, a portable-backup direction, and a native SwiftUI iOS client planned.

## What changed

- PostgreSQL 17 replaces MySQL.
- ASP.NET Core API uses Npgsql.
- PostgreSQL TCP access requires TLS and a trusted client certificate (`clientcert=verify-full`) as well as password authentication.
- The database is on an internal Docker network and its port is not published to the host.
- The security endpoint reports the negotiated TLS version and whether PostgreSQL saw the client certificate.

## Critical PQC note

**This scaffold does not yet claim PQC compliance.** PostgreSQL's `pg_stat_ssl` exposes TLS version, cipher and client-certificate information, but not the negotiated TLS key-exchange group. The API therefore deliberately keeps `/health/ready` at HTTP 503 until a separate, trustworthy PQC verification mechanism is implemented and integrated. Do not change that behavior to “green” based only on TLS 1.3.

PQC must be validated end-to-end across the PostgreSQL server's OpenSSL build/configuration, the .NET runtime/Npgsql TLS implementation, and the actual handshake. The stock `postgres:17-bookworm` image is a conventional starting point for PostgreSQL and **must not be assumed to support or enforce ML-KEM**. A later deployment phase should build/pin an image with a compatible OpenSSL 3.5+ stack and enforce the approved hybrid group on both sides, then prove it with handshake-level tests. If the database driver/runtime cannot enforce the policy, select a compatible client stack rather than silently downgrading.

mTLS and PQC are different controls: client-certificate validation authenticates the client; PQC key exchange protects the handshake against future quantum-capable attackers. The development certificates use ECDSA and are not post-quantum signatures.

## Prerequisites

- Docker Engine and Docker Compose plugin
- OpenSSL on the host
- Internet access for the first image build/pull

## Quick start

1. Generate local development certificates:

   ```bash
   chmod +x scripts/generate-dev-certs.sh
   ./scripts/generate-dev-certs.sh
   ```

2. Create `.env` from the template and replace both passwords with different long random values:

   ```bash
   cp .env.example .env
   ```

3. Start the stack from the directory containing `docker-compose.yml`:

   ```bash
   docker compose --env-file .env up --build
   ```

4. Check the endpoints:

   ```bash
   curl http://localhost:8080/health/live
   curl -i http://localhost:8080/health/ready
   curl -i http://localhost:8080/api/v1/security/status
   ```

`/health/live` should return 200. `/health/ready` intentionally returns 503 until PQC key exchange can be independently verified. That is the current security gate, not a database connection failure by itself.

## Reverse proxy

The API listens on port `8080` inside the container and is bound to `127.0.0.1:8080` on the host for local testing. In production, put it behind your reverse proxy and publish only the proxy's public ports. Configure forwarded-header trust carefully. The database port is not published to the host.

## Certificate files

The script creates a development CA, PostgreSQL server certificate, API client certificate, and API HTTPS certificate under `certs/`. Private keys are excluded from source control. These certificates are for local development only. Use a managed internal CA or certificate lifecycle service in production, with unique client identities, rotation/revocation procedures, and protected secret mounts.

## Next implementation milestones

1. Build and pin a PostgreSQL/OpenSSL image that supports the required hybrid PQC group; validate enforcement with handshake-level tests.
2. Verify Npgsql/.NET can negotiate the approved group and add a trustworthy mechanism to expose the negotiated group to the readiness gate.
3. Add schema migrations and a versioned canonical domain model.
4. Implement portable `.rpb` backup/restore with manifest, entity exports, attachments, checksums, dry-run validation, and restore tests.
5. Add legacy SQL Server/MySQL import adapters after inspecting the real schema and a sanitized export.
6. Add customer, parts/pricing, quote, invoice, and work-order modules.
7. Build the native iOS app in a separate Xcode project using the API's OpenAPI contract.

## Important

This is an initial code scaffold and has not been built or integration-tested in this environment. Validate it in a disposable environment before using real data.
