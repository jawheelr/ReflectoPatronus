#!/usr/bin/env bash
# First-time setup for the Docker deployment. Run from the repository (needs docker, openssl, curl):
#   ./scripts/setup.sh [--yes] [--no-start] [--ghcr]
# It asks for what a new install needs (and generates the database passwords for you), writes .env (mode 0600),
# creates the development certificates, starts the stack, applies the migrations and creates the FIRST ADMINISTRATOR
# with the login name and password YOU choose. There is no built-in default login. Once the login is proven to work,
# the password is removed from .env and the API container is recreated without it.
#   --yes        never prompt; use values from the environment / existing .env (admin user + password are then required:
#                export BOOTSTRAP_ADMIN_USER=... BOOTSTRAP_ADMIN_PASSWORD=...)
#   --no-start   only write .env and the certificates
#   --ghcr       run the published images from ghcr.io (docker-compose.ghcr.yml) instead of building locally
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"; cd "$ROOT"
ENVF="$ROOT/.env"; ASSUME_YES=false START=true GHCR=false
for a in "$@"; do case "$a" in --yes|-y) ASSUME_YES=true;; --no-start) START=false;; --ghcr) GHCR=true;;
  -h|--help) sed -n 2,13p "${BASH_SOURCE[0]}"; exit 0;; *) echo "unknown argument: $a" >&2; exit 2;; esac; done
INTERACTIVE=true; { $ASSUME_YES || [[ ! -t 0 ]]; } && INTERACTIVE=false
command -v openssl >/dev/null || { echo "openssl is required" >&2; exit 1; }
source "$ROOT/deploy/common/setup-lib.sh"

# Existing .env: its values are reused (not asked again) and unknown extra lines are kept.
OLD_EXTRA=""
if [[ -f $ENVF ]]; then
  echo "Found $ENVF: reusing the values in it."
  set -a; source "$ENVF"; set +a
  OLD_EXTRA="$(grep -vE '^(POSTGRES_DB|POSTGRES_USER|POSTGRES_PASSWORD|APP_DB_USER|APP_DB_PASSWORD|ASPNETCORE_ENVIRONMENT|REQUIRE_PQC_VERIFICATION|BOOTSTRAP_ADMIN_USER|BOOTSTRAP_ADMIN_PASSWORD|RP_GATEWAY|GATEWAY_[A-Z_]+|COMPOSE_PROFILES)=|^#|^$' "$ENVF" || true)"
fi

echo "== ReflectoPatronus first-time setup (Docker) =="
ask POSTGRES_DB       "Database name"                           reflectopatronus name
ask POSTGRES_USER     "Database admin role (owns the schema)"   reflectopatronus_admin name
ask POSTGRES_PASSWORD "Database admin password"                 "" newsecret
ask APP_DB_USER       "Application role (restricted, DML only)" reflectopatronus_app name
ask APP_DB_PASSWORD   "Application role password"               "" newsecret
ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"; REQUIRE_PQC_VERIFICATION="${REQUIRE_PQC_VERIFICATION:-false}"
ask_admin   # the installer chooses the first administrator; no default login exists

# Remote access: a gateway container that requires a device certificate (mTLS) and offers post-quantum key exchange.
# It listens on its own port, so 80/443 stay free for other things (e.g. a Synology reverse proxy / Let's Encrypt).
ask_yn RP_GATEWAY "Enable the remote-access gateway (client-certificate mTLS + post-quantum TLS)?" n
if [[ $RP_GATEWAY == y ]]; then
  ask GATEWAY_HOSTNAMES "Public host name(s) or IP(s) clients will connect to (comma separated)" "" text
  ask GATEWAY_PORT "Public TCP port for the gateway (not 80/443)" 8443 text
  [[ $GATEWAY_PORT =~ ^[0-9]+$ && $GATEWAY_PORT -ge 1024 && $GATEWAY_PORT -le 65535 ]] || die "GATEWAY_PORT must be 1024-65535"
  GATEWAY_SHARED_SECRET="${GATEWAY_SHARED_SECRET:-$(gen 40)}"; GATEWAY_MODE=require; COMPOSE_PROFILES=gateway
fi

write_env() {
  ( umask 077
    {
      printf '%s=%s\n' POSTGRES_DB "$POSTGRES_DB" POSTGRES_USER "$POSTGRES_USER" POSTGRES_PASSWORD "$POSTGRES_PASSWORD" \
        APP_DB_USER "$APP_DB_USER" APP_DB_PASSWORD "$APP_DB_PASSWORD" ASPNETCORE_ENVIRONMENT "$ASPNETCORE_ENVIRONMENT" \
        REQUIRE_PQC_VERIFICATION "$REQUIRE_PQC_VERIFICATION"
      echo "# First-run administrator. The password is removed automatically once the login has been verified."
      printf '%s=%s\n' BOOTSTRAP_ADMIN_USER "$BOOTSTRAP_ADMIN_USER" BOOTSTRAP_ADMIN_PASSWORD "${1-$BOOTSTRAP_ADMIN_PASSWORD}"
      if [[ $RP_GATEWAY == y ]]; then
        echo "# Remote-access gateway (mTLS). Mode 'require' makes the API refuse anything that did not come through it."
        printf '%s=%s\n' RP_GATEWAY y GATEWAY_HOSTNAMES "$GATEWAY_HOSTNAMES" GATEWAY_PORT "$GATEWAY_PORT" \
          GATEWAY_SHARED_SECRET "$GATEWAY_SHARED_SECRET" GATEWAY_MODE require COMPOSE_PROFILES gateway
      fi
      if [[ -n $OLD_EXTRA ]]; then printf '%s\n' "$OLD_EXTRA"; fi
    } > "$ENVF.tmp" ); mv "$ENVF.tmp" "$ENVF"
}
write_env
echo "Wrote $ENVF (mode 0600)."
if ((${#GENERATED[@]})); then echo "Generated values (stored in .env; record them in your password manager):"; printf '  %s\n' "${GENERATED[@]}"; fi

POSTGRES_USER="$POSTGRES_USER" "$ROOT/scripts/generate-dev-certs.sh" >/dev/null
[[ $RP_GATEWAY == y ]] && "$ROOT/scripts/gateway-certs.sh" "$GATEWAY_HOSTNAMES"
echo "Certificates are in $ROOT/certs (development CA; use your own CA for production)."
$START || { echo "--no-start: done. Start with: docker compose --env-file .env up -d --build"; exit 0; }

export GIT_SHA="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"   # lets the console tell when a newer commit exists
DC=(docker compose --env-file "$ENVF"); $GHCR && DC=(docker compose -f docker-compose.yml -f docker-compose.ghcr.yml --env-file "$ENVF")
$GHCR && "${DC[@]}" pull || "${DC[@]}" build
"${DC[@]}" up -d db
"$ROOT/scripts/migrate.sh" docker
"${DC[@]}" up -d api
[[ $RP_GATEWAY == y ]] && "${DC[@]}" up -d gateway
echo "Waiting for the administrator login..."
if wait_for_login http://127.0.0.1:8080 "$BOOTSTRAP_ADMIN_USER" "$BOOTSTRAP_ADMIN_PASSWORD" 90; then
  write_env ""                      # drop the password from .env ...
  "${DC[@]}" up -d api              # ... and recreate the container so it is gone from its environment too
  echo
  if [[ $RP_GATEWAY == y ]]; then
    echo "Ready. The API now only answers through the gateway: https://${GATEWAY_HOSTNAMES%%,*}:$GATEWAY_PORT (device certificate required)."
    echo "  1. Router: forward TCP $GATEWAY_PORT to this machine. Leave 80/443 where they are (e.g. on the Synology)."
    echo "  2. Issue a certificate for each device:  ./scripts/issue-device-cert.sh my-ipad   (import the .p12 on that device)"
    echo "  3. Verify mTLS + post-quantum:           ./scripts/check-gateway.sh ${GATEWAY_HOSTNAMES%%,*}:$GATEWAY_PORT my-ipad"
    echo "  Log in as '$BOOTSTRAP_ADMIN_USER' with the password you chose. Back up certs/client-ca.key somewhere safe."
    if $INTERACTIVE; then read -rp "Name of the first device to issue a certificate for (Enter to skip): " dn
      if [[ -n ${dn:-} ]]; then "$ROOT/scripts/issue-device-cert.sh" "$dn"; fi; fi
  else
    echo "Ready: open http://localhost:8080 and log in as '$BOOTSTRAP_ADMIN_USER' with the password you chose."
  fi
else
  echo "WARNING: could not log in as $BOOTSTRAP_ADMIN_USER yet. Check: docker compose logs api" >&2; exit 1
fi
