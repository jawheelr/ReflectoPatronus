#!/usr/bin/env bash
# Install PostgreSQL server certs with the ownership/modes PostgreSQL insists on.
# Shared by the Docker entrypoint and the LXC provisioning script so both behave identically.
#   install-pki.sh <source-dir> [dest-dir]     source-dir contains ca.crt, server.crt, server.key
set -euo pipefail
SRC="${1:?source dir required}"
DEST="${2:-/etc/reflectopatronus/pki}"
install -d -o postgres -g postgres -m 0750 "$(dirname "$DEST")" "$DEST"
install -o postgres -g postgres -m 0644 "$SRC/ca.crt"     "$DEST/ca.crt"
install -o postgres -g postgres -m 0644 "$SRC/server.crt" "$DEST/server.crt"
install -o postgres -g postgres -m 0600 "$SRC/server.key" "$DEST/server.key"
