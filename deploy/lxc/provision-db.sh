#!/usr/bin/env bash
# Run INSIDE the Debian 12 LXC container as root. Produces the same database setup as the Docker `db` service:
# PostgreSQL 17, TLS>=1.3, client-cert + password auth, admin + restricted app role, migrations applied.
# Reads /root/rp.env (POSTGRES_DB, POSTGRES_USER, POSTGRES_PASSWORD, APP_DB_USER, APP_DB_PASSWORD, [LISTEN_ADDRESSES]).
# Needs /root/pki-in/{ca.crt,server.crt,server.key}.  Safe to re-run.
set -euo pipefail
B=/opt/reflectopatronus/bundle
set -a; source /root/rp.env; set +a
: "${POSTGRES_DB:?}" "${POSTGRES_USER:?}" "${POSTGRES_PASSWORD:?}" "${APP_DB_USER:?}" "${APP_DB_PASSWORD:?}"
PGV=17
export DEBIAN_FRONTEND=noninteractive

apt-get update -qq
apt-get install -y -qq ca-certificates curl gnupg sudo
if [[ ! -f /etc/apt/sources.list.d/pgdg.list ]]; then   # Debian 12 ships PostgreSQL 15; we need 17 from PGDG
  install -d /usr/share/keyrings
  curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc | gpg --dearmor -o /usr/share/keyrings/pgdg.gpg
  echo "deb [signed-by=/usr/share/keyrings/pgdg.gpg] https://apt.postgresql.org/pub/repos/apt bookworm-pgdg main" > /etc/apt/sources.list.d/pgdg.list
  apt-get update -qq
fi
# If a separate data volume/bind mount is attached (pct-create.sh --data-mount), refuse to continue unless it is writable
# by this container's root; PostgreSQL's cluster is then created ON it (/var/lib/postgresql).
if findmnt -n /var/lib/postgresql >/dev/null 2>&1; then
  touch /var/lib/postgresql/.rp-write-test 2>/dev/null && rm -f /var/lib/postgresql/.rp-write-test \
    || { echo "ERROR: /var/lib/postgresql is a mount but not writable. On the host: chown 100000:100000 <dir>" >&2; exit 1; }
  echo "Persistent data mount detected: $(findmnt -n -o SOURCE,FSTYPE /var/lib/postgresql)"
fi
apt-get install -y -qq "postgresql-$PGV"
systemctl enable --now "postgresql@$PGV-main"

# --- Bootstrap roles/db using Debian's default (peer / loopback) auth, BEFORE our strict pg_hba is installed. ---
# Already bootstrapped (re-run after hardening)? The admin can log in over the local socket with its password.
if PGPASSWORD="$POSTGRES_PASSWORD" psql -X -h /var/run/postgresql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "SELECT 1" >/dev/null 2>&1; then
  echo "roles/database already bootstrapped; skipping"
else
  sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='$POSTGRES_USER'" | grep -q 1 || \
    sudo -u postgres psql -v ON_ERROR_STOP=1 -c "CREATE ROLE \"$POSTGRES_USER\" SUPERUSER LOGIN PASSWORD '$POSTGRES_PASSWORD'"
  sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='$POSTGRES_DB'" | grep -q 1 || \
    sudo -u postgres createdb -O "$POSTGRES_USER" "$POSTGRES_DB"
  # Same script the Docker image runs from /docker-entrypoint-initdb.d, executed as the admin role so that
  # ALTER DEFAULT PRIVILEGES applies to tables the admin (migrations) creates.
  PGHOST=127.0.0.1 PGPASSWORD="$POSTGRES_PASSWORD" bash "$B/docker/postgres/init/10-create-app-role.sh"
fi

# --- Apply the shared hardened config (identical files to the Docker image). ---
install -d -m 0755 /etc/reflectopatronus
install -m 0644 "$B/deploy/common/pg_hba.conf" /etc/reflectopatronus/pg_hba.conf
CONFD="/etc/postgresql/$PGV/main/conf.d"
install -d "$CONFD"
install -m 0644 "$B/deploy/common/rp-postgresql.conf" "$CONFD/10-reflectopatronus.conf"
# Debian's own postgresql.conf sets hba_file / ssl_* itself and includes conf.d BEFORE those lines, so its values would
# override ours. Make our fragment win: drop Debian's hba_file line and re-include conf.d as the LAST line of the file.
MAIN="/etc/postgresql/$PGV/main/postgresql.conf"
sed -i -e "s|^[[:space:]]*hba_file[[:space:]]*=|#hba_file =|" -e "/^[[:space:]]*include_dir[[:space:]]*=/d" "$MAIN"
echo "include_dir = 'conf.d'" >> "$MAIN"
[[ -n "${LISTEN_ADDRESSES:-}" ]] && sed -i "s|^listen_addresses = .*|listen_addresses = '$LISTEN_ADDRESSES'|" "$CONFD/10-reflectopatronus.conf"
bash "$B/deploy/common/install-pki.sh" /root/pki-in /etc/reflectopatronus/pki
systemctl restart "postgresql@$PGV-main"

# --- Schema ---
bash "$B/scripts/migrate.sh" local
echo "DB container ready. Verify: sudo -u postgres psql -c 'SHOW ssl_min_protocol_version'"
