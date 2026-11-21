#!/usr/bin/env bash
# Run the legacy system -> PostgreSQL importer in a container attached to the compose `database` network (Postgres is not
# published to the host) and to the network of the restored SQL Server container.
#   scripts/import-legacy.sh --dry-run            # full run, rolled back; writes out/import-report.json
#   scripts/import-legacy.sh --reset              # real import (wipes previously imported data first)
# Extra args are passed to the importer. Needs: stack up, ./scripts/migrate.sh docker applied, a SQL Server
# container (MSSQL_CONTAINER, default legacy-mssql) with the restored `legacydata` and MSSQL_PASSWORD set.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
set -a; source "$ROOT/.env"; set +a
MSSQL_CONTAINER="${MSSQL_CONTAINER:-legacy-mssql}"
: "${MSSQL_PASSWORD:?set MSSQL_PASSWORD (sa password of $MSSQL_CONTAINER)}"
MSSQL_IP="$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}' "$MSSQL_CONTAINER" | awk '{print $1}')"
[[ -f "$ROOT/certs/admin-client.crt" ]] || { echo "run ./scripts/generate-dev-certs.sh first (creates admin-client cert)" >&2; exit 1; }
docker build -q -t rp-importer "$ROOT/tools/importer" >/dev/null
mkdir -p "$ROOT/out"
cid="$(docker create --network reflectopatronus_database \
  -e PGHOST=db -e PGPORT=5432 -e PGUSER="$POSTGRES_USER" -e PGPASSWORD="$POSTGRES_PASSWORD" -e PGDATABASE="$POSTGRES_DB" \
  -e MSSQL_HOST="$MSSQL_IP" -e MSSQL_PASSWORD ${IMPORT_TZ:+-e IMPORT_TZ} \
  -v "$ROOT/certs:/certs:ro" -v "$ROOT/out:/out" rp-importer "$@")"
docker network connect bridge "$cid" 2>/dev/null || true
trap 'docker rm -f "$cid" >/dev/null 2>&1 || true' EXIT
docker start -a "$cid"
