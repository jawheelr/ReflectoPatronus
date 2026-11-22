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
Every push to `main` refreshes the rolling `edge` pre-release (`reflectopatronus-edge-amd64.tar.zst`); every release (`v*` tag) attaches `reflectopatronus-<version>-amd64.tar.zst` and a `.sha256` to its GitHub Release. It is a Debian 12 container root filesystem with PostgreSQL 17, the ASP.NET Core 10 runtime, the API + web console and the provisioning scripts already installed, so provisioning needs no internet. One template serves every layout; the setup wizard asks what the container should run: **all** (database and application together in one container, the simplest; the database then listens on 127.0.0.1 only), **db** only, or **api** only (two containers, as described above). (`.github/workflows/lxc-template.yml` builds it; `scripts/build-lxc-template.sh` builds it locally.)

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
Setup starts by itself the first time a container boots from the template: open its console (`pct console 210`, or the Console tab in the web UI), press Enter at the banner, and the wizard takes over `tty1` and waits for your answers. It reappears at every boot until setup completes, and hands the console back to the login prompt when done. To run it by hand instead, use `pct enter 210` and `rp-setup db` (or `rp-setup api`); to turn the automatic start off, `touch /var/lib/reflectopatronus/skip-firstboot`. It asks for everything that is required, and pressing Enter on a password generates a strong random one:

| Container | Asked |
|---|---|
| `all` | everything in the `db` row (except the listen address, fixed to 127.0.0.1) and the `api` row (except the database address), certificates are generated or taken from your directory (`ca`, `server`, `api-client`) |
| `db` | database name, admin role + password, application role + password, listen address (defaults to the container's IP), certificates: generate a private CA + server/API client certs, or point at your own directory |
| `api` | database address, database name, application role + the password set on `db`, first admin user + password, certificates (the `.tgz` the `db` step produced, or your own files) |

Generated passwords are shown once and saved to `/root/rp.env` (mode 0600). The `db` step ends by printing the `pct pull` / `pct push` line that moves `rp-pki-for-api.tgz` to the API container. Answers already in `/root/rp.env` are not asked again, so you can pre-seed the file and run `rp-setup db --yes` / `rp-setup api --yes` for an unattended install; `--no-provision` only collects answers and certificates. Passwords need 12+ characters and none of `; ' " \ $` or spaces. (The older manual route, writing `rp.env` yourself and running `provision-<role>.sh`, still works.)

**Preconfiguring (unattended install):** copy [deploy/lxc/preseed.env.example](../deploy/lxc/preseed.env.example), fill it in (`RP_ROLE=all|db|api`, plus the first administrator's login and password, which you always choose yourself; there is no default login), and put it in the container as `/root/rp-preseed.env` (`pct push <ctid> preseed.env /root/rp-preseed.env --perms 0600`, or `pct-create.sh ... --preseed preseed.env`). The first-boot wizard picks it up on its own, asks only for what is missing, installs, checks that the administrator login works, then deletes the preseed file and removes the first-admin password from `rp.env` and `api.env`. The Docker equivalent is `./scripts/setup.sh --yes` with `BOOTSTRAP_ADMIN_USER` / `BOOTSTRAP_ADMIN_PASSWORD` exported.

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

## Remote access: mTLS + post-quantum gateway (behind a Synology / router)
Clients (web console, Windows/Tauri, iOS) should reach the server through a **gateway** that requires a **device certificate (mTLS)** and offers the **post-quantum hybrid key exchange X25519MLKEM768**. It is Caddy (a static Go binary with its own TLS stack, so it does not depend on the container's OpenSSL), TLS 1.3 only, in front of the API.

**Layout with a Synology reverse proxy.** Keep 80/443 forwarded to the Synology for Let's Encrypt and its other sites. The gateway listens on its **own port** (default `8443`); forward that TCP port on the router **straight to the gateway host**, not through the Synology. The Synology reverse proxy cannot be in this path: DSM regenerates its nginx config, has no client-certificate option, and would terminate TLS itself, so neither mTLS nor post-quantum would reach the client.
```
iPhone / Windows ---TLS1.3 + client cert + X25519MLKEM768---> router :8443 ---> gateway ---HTTP (internal)---> API ---mTLS---> PostgreSQL
                  (80/443 -> Synology: unchanged)
```
**Enable it:** answer **y** to "Enable the remote-access gateway" in `./scripts/setup.sh` (Docker) or in the first-boot wizard / `rp-setup` (LXC; preseed keys `RP_GATEWAY`, `GATEWAY_HOSTNAMES`, `GATEWAY_PORT`, `RP_DEVICE_NAME`). It creates a private **device CA** and the gateway's server certificate, and sets `Gateway__Mode=require` on the API: the API then refuses any request that did not come through the gateway (shared secret) or that lacks a verified certificate, and each login session is **bound to the certificate's fingerprint**, so a stolen token is useless without the device's key. In this mode `http://host:8080` no longer works directly; set `GATEWAY_MODE=off` in `.env` (Docker) to allow LAN access as well.

**Devices.** Issue one certificate per device and import the `.p12` there:
```bash
./scripts/issue-device-cert.sh jarreds-ipad          # Docker: certs/devices/jarreds-ipad.p12 (password shown once)
rp-issue-device jarreds-ipad                          # LXC: /root/rp-gateway-pki/devices/jarreds-ipad.p12; fetch with pct pull
```
iPhone/iPad: open the `.p12` (AirDrop/Files), install the profile in Settings, enter the password. Windows: double-click, "Current User", leave the key non-exportable. Add `--legacy-p12` only if a device refuses the default file format. The SwiftUI / Tauri apps present the identity from the Keychain / certificate store; the web console uses the one installed in the browser.

**Server certificate.** By default it is signed by the device CA, so native apps pin that CA (`certs/client-ca.crt`) and browsers must trust it too. To use the Let's Encrypt certificate from the Synology instead (trusted everywhere, no pinning), export it and put it at `certs/gateway.crt` / `certs/gateway.key` before running setup (the CA is still needed to verify devices). The name must match the host clients use.

**Check it:** `./scripts/check-gateway.sh rp.example.net:8443 jarreds-ipad` (runs OpenSSL 3.5 in a container) confirms that a client without a certificate is refused, that `X25519MLKEM768` is negotiated, and that a certificate gets through.

**Post-quantum support by client:** the server prefers the hybrid when the client offers it, otherwise it falls back to classical X25519 (still TLS 1.3 + mTLS). Apple enables it by default from iOS/macOS 26; current Chromium-based browsers and WebView2 offer it; Windows Schannel support is recent, so check the Microsoft KB for your build. The database link's PQC gate (`Security__RequirePqcVerification`) is separate and still unverified.

**Not built yet:** certificate revocation lists (revoke a device by disabling its session in the API and keeping certificate lifetimes short, `--days`), and a one-time-code enrollment flow (certificates are issued by an administrator with the scripts above). Back up `client-ca.key` offline: it signs every device certificate. **Tested here:** the Caddy gateway against a stand-in backend (no certificate and a certificate from another CA are refused, `X25519MLKEM768` negotiated, spoofed headers overwritten, fingerprint forwarded) and the API's gateway mode and session binding against the real API. **Not tested:** the full `setup.sh` / `rp-setup` run with the gateway on, the LXC gateway service, and real iOS/Windows clients.

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
