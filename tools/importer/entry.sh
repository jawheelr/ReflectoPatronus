#!/bin/sh
# libpq refuses client keys that are group/world readable; bind-mounted keys often are. Copy to a private path.
set -e
if [ -f /certs/admin-client.key ]; then
  cp /certs/admin-client.key /tmp/admin-client.key && chmod 600 /tmp/admin-client.key
  export PGSSLKEY=/tmp/admin-client.key PGSSLCERT=/certs/admin-client.crt PGSSLROOTCERT=/certs/ca.crt
  export PGSSLMODE="${PGSSLMODE:-verify-full}"
fi
exec python -u /app/import_legacy.py "$@"
