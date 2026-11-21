#!/usr/bin/env bash
# Apply db/migrations/*.sql in order, once each, recording them in schema_migrations.
# Works identically for Docker and LXC: only HOW psql is reached differs.
#
#   scripts/migrate.sh docker        # runs psql inside the compose `db` service (local socket, admin role)
#   scripts/migrate.sh local         # runs psql here / inside an LXC container, via the local socket as the admin role
#   scripts/migrate.sh remote        # runs psql over the network; needs PGHOST/PGUSER/PGPASSWORD/PGDATABASE (+ PGSSL*)
#
# Migrations must be run by the admin (owner) role, never the restricted app role.
set -euo pipefail

MODE="${1:-}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MIG_DIR="${MIGRATIONS_DIR:-$ROOT/db/migrations}"

if [[ -f "$ROOT/.env" && -z "${POSTGRES_USER:-}" ]]; then set -a; source "$ROOT/.env"; set +a; fi
PGDB="${POSTGRES_DB:-reflectopatronus}"
PGADMIN="${POSTGRES_USER:-reflectopatronus_admin}"

case "$MODE" in
  docker) psql_run() { docker compose --project-directory "$ROOT" exec -T -e PGPASSWORD="${POSTGRES_PASSWORD:?POSTGRES_PASSWORD required (set in .env)}" db psql -X -q -v ON_ERROR_STOP=1 -U "$PGADMIN" -d "$PGDB" "$@"; } ;;
  local)  psql_run() { PGPASSWORD="${POSTGRES_PASSWORD:?POSTGRES_PASSWORD required}" psql -X -q -v ON_ERROR_STOP=1 -h /var/run/postgresql -U "$PGADMIN" -d "$PGDB" "$@"; } ;;
  remote) psql_run() { psql -X -q -v ON_ERROR_STOP=1 "$@"; } ;;
  *) echo "usage: $0 {docker|local|remote}" >&2; exit 2 ;;
esac

psql_run -c "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())"

shopt -s nullglob
applied=0
for f in "$MIG_DIR"/*.sql; do
  v="$(basename "$f" .sql)"
  done_already="$(psql_run -tA -c "SELECT 1 FROM schema_migrations WHERE version = '$v'")"
  [[ -n "$done_already" ]] && { echo "skip   $v"; continue; }
  echo "apply  $v"
  # One transaction per migration: it either fully applies and is recorded, or leaves nothing behind.
  { echo "BEGIN;"; cat "$f"; echo "INSERT INTO schema_migrations (version) VALUES ('$v');"; echo "COMMIT;"; } | psql_run -f -
  applied=$((applied + 1))
done
# The runtime role must not be able to edit migration history.
[[ -n "${APP_DB_USER:-}" ]] && psql_run -c "REVOKE ALL ON schema_migrations FROM \"$APP_DB_USER\""
echo "done: $applied migration(s) applied"
