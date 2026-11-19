# ReflectoPatronus

Replacement for GlassQuote 7 (flat-glass quoting/invoicing): PostgreSQL 17 + ASP.NET Core API (React and iOS clients planned), runnable on **Docker** or **Proxmox LXC** from the same config.

| Doc | What |
|---|---|
| [docs/GQ7-SCHEMA.md](docs/GQ7-SCHEMA.md) | What is inside the GlassQuote 7 database (structure only) |
| [docs/DB-DESIGN.md](docs/DB-DESIGN.md) | The PostgreSQL model, what changed from GQ7 and why, open questions |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Docker and LXC setup, converting between them, what is tested |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/STARTER-README.md](docs/STARTER-README.md) | Original scaffold notes (TLS/mTLS/PQC gate) |

Quick start (Docker): `./scripts/generate-dev-certs.sh`, copy `.env.example` to `.env` and set passwords, `docker compose --env-file .env up -d --build`, `./scripts/migrate.sh docker`.

`legacy/` holds the GlassQuote backup and is git-ignored. Never commit it.
