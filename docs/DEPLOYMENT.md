# Deployment: Docker and Proxmox LXC

One application, one set of config files, two ways to run it. Everything that matters is shared; only the thin wrapper differs.

## What is shared (single source of truth)
| Concern | File | Docker uses it via | LXC uses it via |
|---|---|---|---|
| Postgres settings (TLS 1.3, cert paths, hba path) | `deploy/common/rp-postgresql.conf` | entrypoint wrapper -> `-c key=value` | copied to `/etc/postgresql/17/main/conf.d/` |
| Auth rules (TLS + client cert + password) | `deploy/common/pg_hba.conf` | `COPY` into image | `install` into `/etc/reflectopatronus/` |
| Cert install (ownership/modes) | `deploy/common/install-pki.sh` | image entrypoint | provision script |
| Roles / privileges | `docker/postgres/init/10-create-app-role.sh` | `/docker-entrypoint-initdb.d` | run by `provision-db.sh` |
| Schema | `db/migrations/*.sql` via `scripts/migrate.sh` | `migrate.sh docker` | `migrate.sh local` (run by provisioning) |
| API config | env vars (`Database__ConnectionString`, ...) | `docker-compose.yml` | `/etc/reflectopatronus/api.env` (`deploy/common/api.env.example`) |

**Conventions that make conversion trivial:** the DB host is always named `db`; certs live at `/etc/reflectopatronus/pki/` on the DB side in both runtimes; PostgreSQL is always 17 with the same settings; the API is always configured by the same environment variable names. So the **same dev certificates work unchanged** in both.

## Docker
```bash
./scripts/generate-dev-certs.sh
cp .env.example .env            # set both passwords to different long random values
docker compose --env-file .env up -d --build
./scripts/migrate.sh docker
curl -i http://localhost:8080/health/ready
```
The `db` service is on an `internal` network and publishes no port. Tested.

## Proxmox LXC
Recommended topology mirrors compose: **two unprivileged Debian 12 containers** on a private bridge/VLAN, `rp-db` (PostgreSQL) and `rp-api`. Put your reverse proxy in front of `rp-api` only; allow 5432 into `rp-db` from `rp-api` only. `pct-create.sh` turns the Proxmox firewall flag on for the CT NIC, but **you must add the rules** (default policy plus an ACCEPT for rp-api -> rp-db:5432); the client-certificate requirement is the second layer, not a substitute. One container holding both also works but loses the isolation.

On the **Proxmox host** (as root), with the repo and `certs/` present:
```bash
# 1. Build the API artifact where Docker exists (any machine), copy dist/reflectopatronus-api.tar.gz to the host
./scripts/build-api-artifact.sh

# 2. Create the containers (pushes the deploy bundle + certs; never pushes legacy/, .env or CA key)
deploy/lxc/pct-create.sh --role db  --ctid 210 --name rp-db  --ip 10.10.10.10/24 --gw 10.10.10.1 --bridge vmbr1 --certs ./certs
deploy/lxc/pct-create.sh --role api --ctid 211 --name rp-api --ip 10.10.10.11/24 --gw 10.10.10.1 --bridge vmbr1 --certs ./certs --artifact dist/reflectopatronus-api.tar.gz

# 2b. Persistent data location (db role). You are PROMPTED when run from a terminal; or pass it explicitly:
#       --data-mount rootfs                      inside the container disk (default)
#       --data-mount volume:local-zfs:100        separate 100 GB Proxmox volume on that storage
#       --data-mount bind:/mnt/ssd/rp-data       an existing host directory or mounted volume
#     Mounted at /var/lib/postgresql. Bind dirs are chowned to 100000:100000 (unprivileged root mapping) and survive
#     'pct destroy', so a rebuilt container can re-use the same data; volumes are deleted with the container.

# 3. Write each CT's env file on the host, push it, then provision
cat > /tmp/rp-db.env <<EOF
POSTGRES_DB=reflectopatronus
POSTGRES_USER=reflectopatronus_admin
POSTGRES_PASSWORD=<long random>
APP_DB_USER=reflectopatronus_app
APP_DB_PASSWORD=<different long random>
LISTEN_ADDRESSES=10.10.10.10
EOF
cat > /tmp/rp-api.env <<EOF
POSTGRES_DB=reflectopatronus
APP_DB_USER=reflectopatronus_app
APP_DB_PASSWORD=<same as above>
DB_IP=10.10.10.10
EOF
pct push 210 /tmp/rp-db.env  /root/rp.env --perms 0600
pct push 211 /tmp/rp-api.env /root/rp.env --perms 0600
shred -u /tmp/rp-db.env /tmp/rp-api.env
pct exec 210 -- bash /opt/reflectopatronus/bundle/deploy/lxc/provision-db.sh
pct exec 211 -- bash /opt/reflectopatronus/bundle/deploy/lxc/provision-api.sh
```
Passwords must not contain `;` or `'` (they go into a connection string / SQL literal). `rp.env` contains secrets: delete it from the CT once provisioned, or keep it `chmod 600`.

### Downloadable LXC template (no repo checkout needed on the host)
Every push to `main` refreshes the rolling `edge` pre-release (`reflectopatronus-edge-amd64.tar.zst`); every release (`v*` tag) attaches `reflectopatronus-<version>-amd64.tar.zst` and a `.sha256` to its GitHub Release. It is a Debian 12 container root filesystem with PostgreSQL 17, the ASP.NET Core 10 runtime, the API + web console and the provisioning scripts already installed, so provisioning needs no internet. One template serves both roles; you pick `db` or `api` when you run `rp-setup`. (`.github/workflows/lxc-template.yml` builds it; `scripts/build-lxc-template.sh` builds it locally.)

On the **Proxmox host**:
```bash
cd /var/lib/vz/template/cache
curl -LO https://github.com/jawheelr/ReflectoPatronus/releases/download/vX.Y.Z/reflectopatronus-X.Y.Z-amd64.tar.zst
curl -LO https://github.com/jawheelr/ReflectoPatronus/releases/download/vX.Y.Z/reflectopatronus-X.Y.Z-amd64.tar.zst.sha256 && sha256sum -c reflectopatronus-X.Y.Z-amd64.tar.zst.sha256

# DB container, with the persistent-data choice (rootfs, a Proxmox volume, or a host bind mount):
pct create 210 local:vztmpl/reflectopatronus-X.Y.Z-amd64.tar.zst --hostname rp-db --unprivileged 1 --cores 2 --memory 2048 \
  --rootfs local-lvm:16 --net0 name=eth0,bridge=vmbr1,ip=10.10.10.10/24,gw=10.10.10.1,firewall=1 --onboot 1 \
  --mp0 local-zfs:100,mp=/var/lib/postgresql,backup=1        # or omit --mp0 to keep data in the rootfs
pct create 211 local:vztmpl/reflectopatronus-X.Y.Z-amd64.tar.zst --hostname rp-api --unprivileged 1 --cores 2 --memory 2048 \
  --rootfs local-lvm:8 --net0 name=eth0,bridge=vmbr1,ip=10.10.10.11/24,gw=10.10.10.1,firewall=1 --onboot 1
pct start 210; pct start 211
```
Then continue exactly as in step 3 above (write `rp-db.env` / `rp-api.env`, plus `BOOTSTRAP_ADMIN_PASSWORD=<12+ chars>` in the API one; `pct push` the env files to `/root/rp.env` and the certs to `/root/pki-in/`), but provision with:
```bash
pct exec 210 -- rp-setup db
pct exec 211 -- rp-setup api
```
`deploy/lxc/pct-create.sh --template local:vztmpl/reflectopatronus-X.Y.Z-amd64.tar.zst ...` does the create/push work for you if you have the repo on the host (including the data-mount prompt). With a bind mount, chown the host directory to `100000:100000` first. The template contains no secrets, certs or data. It is amd64 only. **Not yet tested on a real Proxmox host.**

## Published images (ghcr.io)
`.github/workflows/images.yml` builds `linux/amd64` + `linux/arm64` images on every push to `main` and every `v*` tag, and publishes them to GitHub Container Registry:

| Image | Contents |
|---|---|
| `ghcr.io/jawheelr/reflectopatronus` | API + React console |
| `ghcr.io/jawheelr/reflectopatronus-db` | PostgreSQL 17 with the project's TLS 1.3 / pg_hba config |
| `ghcr.io/jawheelr/reflectopatronus-importer` | legacy SQL Server importer (operations use) |

Tags: `main`, `sha-<short>`, and for releases `X.Y.Z`, `X.Y`, `latest`. Pull requests build but do not push. No secrets, certs or legacy data are in the images; they come in at run time exactly as with a local build. New GHCR packages are **private** until you change their visibility (repo -> Packages), or run `docker login ghcr.io` first.

**Docker, from the registry:**
```bash
docker compose -f docker-compose.yml -f docker-compose.ghcr.yml --env-file .env pull
docker compose -f docker-compose.yml -f docker-compose.ghcr.yml --env-file .env up -d
./scripts/migrate.sh docker
```
Pin a release with `RP_TAG=1.0.0` in `.env`.

**Proxmox VE 9.1+, LXC from an OCI image (not yet tested here):** Proxmox can pull an OCI image into template storage (web UI: storage -> CT Templates -> *Pull from OCI registry*, registry `ghcr.io`; or `pveam`) and create an unprivileged container from it. Pin a tag, never `latest`. Application containers are a tech preview in 9.1, so the Debian-based provisioning above remains the supported LXC route. If you use OCI containers you must supply at run time what compose supplies: the env vars from `docker-compose.yml`, the three cert files (`api` -> `/run/db-certs/`, `db` -> `/run/input-certs/` as `ca.crt`, `server.crt`, `server.key`), a mount for `/var/lib/postgresql/data` on the `db` container, and a network where the API can resolve the host name `db`.

## Converting between them
The data lives only in PostgreSQL, so conversion is a dump/restore of one database; schema and config are identical.
```bash
# Docker -> LXC
docker compose exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db pg_dump -U "$POSTGRES_USER" -Fc "$POSTGRES_DB" > rp.dump
pct push 210 rp.dump /root/rp.dump
pct exec 210 -- bash -c 'PGPASSWORD=... pg_restore -h /var/run/postgresql -U reflectopatronus_admin -d reflectopatronus --clean --if-exists /root/rp.dump'
# LXC -> Docker: same in reverse (pg_dump in the CT, pg_restore into the compose db service).
```
Run `scripts/migrate.sh` afterwards; it skips what is already recorded in `schema_migrations`. Restoring with the *same* admin/app role names avoids ownership surprises. Use the same CA/client certs on both sides, or regenerate and redeploy both.

## What was and was not tested
| | Status |
|---|---|
| Docker stack (build, mTLS, migrations, health, TLS settings) | Run on this machine |
| `provision-db.sh` on Debian 12 + PostgreSQL 17 (PGDG): config precedence, role bootstrap, TLS 1.3, plain-text and no-client-cert rejection, valid client cert accepted, app role cannot edit `schema_migrations`, idempotent re-run | Run in a Debian 12 container with a `systemctl` stand-in |
| `provision-api.sh`: .NET install, `rpapi` user, key mode 0600, service serves `/health/live`, clean 503 when DB unreachable | Run in a Debian 12 container with a `systemd` stand-in |
| `pct-create.sh`, the real systemd unit (sandboxing directives), Proxmox firewall, DB<->API across two real CTs | **Not run: needs a Proxmox host.** Review before relying on them |

Note on the stand-ins: they cannot catch unit-file mistakes or CT networking. Do a first run on a throwaway CT ID.

## Known limits
- PQC key exchange is still unverified (see README); `Security__RequirePqcVerification` stays `false` until it can be.
- The shipped dev certs are for development. Use your internal CA for production, with rotation and revocation.
- Debian 12's stock PostgreSQL is 15; the LXC script deliberately installs 17 from the official PGDG repository (needs outbound HTTPS).
