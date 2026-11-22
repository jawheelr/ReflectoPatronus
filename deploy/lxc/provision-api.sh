#!/usr/bin/env bash
# Run INSIDE the Debian 12 LXC container as root. Installs the ASP.NET Core 10 runtime, the published API
# (/root/reflectopatronus-api.tar.gz from scripts/build-api-artifact.sh) and a hardened systemd unit.
# Reads /root/rp.env: APP_DB_USER, APP_DB_PASSWORD, POSTGRES_DB, DB_IP (address of the DB container),
# optional REQUIRE_PQC_VERIFICATION, BOOTSTRAP_ADMIN_USER (default ADMIN) + BOOTSTRAP_ADMIN_PASSWORD (12+ chars; creates the
# first admin on first start, then remove it from rp.env and api.env).  Needs /root/pki-in/{ca.crt,api-client.crt,api-client.key}.  Safe to re-run.
set -euo pipefail
set -a; source /root/rp.env; set +a
: "${APP_DB_USER:?}" "${APP_DB_PASSWORD:?}" "${POSTGRES_DB:?}" "${DB_IP:?}"
export DEBIAN_FRONTEND=noninteractive

# The downloadable LXC template ships everything preinstalled (marker file); the plain Debian route installs here.
PREINSTALLED=false; [[ -f /opt/reflectopatronus/.preinstalled ]] && PREINSTALLED=true
$PREINSTALLED || { apt-get update -qq && apt-get install -y -qq ca-certificates curl; }
if [[ ! -x /usr/share/dotnet/dotnet ]]; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --runtime aspnetcore --channel 10.0 --install-dir /usr/share/dotnet
  ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
fi

id rpapi >/dev/null 2>&1 || useradd --system --home /opt/reflectopatronus --shell /usr/sbin/nologin rpapi
install -d -o rpapi -g rpapi /opt/reflectopatronus/api
ARTIFACT=/root/reflectopatronus-api.tar.gz; [[ -f $ARTIFACT ]] || ARTIFACT=/opt/reflectopatronus/api-dist.tar.gz   # template ships one
tar -C /opt/reflectopatronus/api -xzf "$ARTIFACT"
chown -R rpapi:rpapi /opt/reflectopatronus/api

# The convention `Host=db` keeps the Docker-issued server certificate valid here too.
grep -qE '[[:space:]]db$' /etc/hosts || echo "$DB_IP db" >> /etc/hosts

install -d -m 0750 -o root -g rpapi /etc/reflectopatronus /etc/reflectopatronus/pki
install -m 0644 -o root -g rpapi /root/pki-in/ca.crt /etc/reflectopatronus/pki/ca.crt
install -m 0644 -o root -g rpapi /root/pki-in/api-client.crt /etc/reflectopatronus/pki/api-client.crt
install -m 0600 -o rpapi -g rpapi /root/pki-in/api-client.key /etc/reflectopatronus/pki/api-client.key   # stricter than Docker dev

# With the mTLS gateway enabled (GATEWAY_SHARED_SECRET in rp.env) the API only listens locally and requires the gateway.
API_BIND=0.0.0.0; GW_ON=false
if [[ -n ${GATEWAY_SHARED_SECRET:-} ]]; then API_BIND=127.0.0.1; GW_ON=true; fi
cat > /etc/reflectopatronus/api.env <<ENV
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://$API_BIND:8080
Security__RequirePqcVerification=${REQUIRE_PQC_VERIFICATION:-false}
${GATEWAY_SHARED_SECRET:+Gateway__Mode=require
Gateway__SharedSecret=$GATEWAY_SHARED_SECRET}
${BOOTSTRAP_ADMIN_PASSWORD:+Bootstrap__AdminUser=${BOOTSTRAP_ADMIN_USER:-ADMIN}
Bootstrap__AdminPassword=$BOOTSTRAP_ADMIN_PASSWORD}
Database__ConnectionString=Host=db;Port=5432;Database=$POSTGRES_DB;Username=$APP_DB_USER;Password=$APP_DB_PASSWORD;SSL Mode=VerifyFull;Root Certificate=/etc/reflectopatronus/pki/ca.crt;SSL Certificate=/etc/reflectopatronus/pki/api-client.crt;SSL Key=/etc/reflectopatronus/pki/api-client.key;Timeout=8;Command Timeout=15;GSS Encryption Mode=Disable;
ENV
chown root:rpapi /etc/reflectopatronus/api.env; chmod 0640 /etc/reflectopatronus/api.env

mkdir -p /var/lib/reflectopatronus/transfer; chown rpapi:rpapi /var/lib/reflectopatronus/transfer; chmod 0750 /var/lib/reflectopatronus/transfer
cat > /etc/systemd/system/reflectopatronus-api.service <<'UNIT'
[Unit]
Description=ReflectoPatronus API
After=network-online.target
Wants=network-online.target

[Service]
User=rpapi
Group=rpapi
WorkingDirectory=/opt/reflectopatronus/api
EnvironmentFile=/etc/reflectopatronus/api.env
ExecStart=/usr/local/bin/dotnet /opt/reflectopatronus/api/ReflectoPatronus.Api.dll
Restart=on-failure
RestartSec=3
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ProtectKernelTunables=true
ProtectControlGroups=true
RestrictSUIDSGID=true
ReadWritePaths=/var/lib/reflectopatronus/transfer

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now reflectopatronus-api
sleep 4
curl -fsS http://127.0.0.1:8080/health/live && echo && echo "API up. Readiness: curl -i http://127.0.0.1:8080/health/ready"
