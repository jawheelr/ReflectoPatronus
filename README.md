# ReflectoPatronus

**A modern glass shop software.**  
Developer: Jarred Wheeler

Replacement for the legacy system (flat-glass quoting/invoicing): PostgreSQL 17 + ASP.NET Core API (React and iOS clients planned), runnable on **Docker** or **Proxmox LXC** from the same config.

| Doc | What |
|---|---|
| [docs/LEGACY-SCHEMA.md](docs/LEGACY-SCHEMA.md) | What is inside the legacy system database (structure only) |
| [docs/DB-DESIGN.md](docs/DB-DESIGN.md) | The PostgreSQL model, what changed from the legacy system and why, open questions |
| [docs/API.md](docs/API.md) | REST API: per-device auth, time zones, customers; OpenAPI contract |
| [docs/IMPORT.md](docs/IMPORT.md) | Running the legacy system importer, guarantees, first dry-run results |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Docker and LXC setup, converting between them, what is tested |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/STARTER-README.md](docs/STARTER-README.md) | Original scaffold notes (TLS/mTLS/PQC gate) |

Quick start (Docker): `./scripts/generate-dev-certs.sh`, copy `.env.example` to `.env` and set passwords, `docker compose --env-file .env up -d --build`, `./scripts/migrate.sh docker`.

`legacy/` holds the legacy system backup and is git-ignored. Never commit it.
