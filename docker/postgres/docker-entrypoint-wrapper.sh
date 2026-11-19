#!/usr/bin/env bash
set -euo pipefail

# PostgreSQL requires its private key to be owned by postgres and mode 0600.
install -d -o postgres -g postgres -m 0700 /var/lib/postgresql/certs
install -o postgres -g postgres -m 0644 /run/input-certs/ca.crt /var/lib/postgresql/certs/ca.crt
install -o postgres -g postgres -m 0644 /run/input-certs/server.crt /var/lib/postgresql/certs/server.crt
install -o postgres -g postgres -m 0600 /run/input-certs/server.key /var/lib/postgresql/certs/server.key
exec /usr/local/bin/docker-entrypoint.sh "$@"
