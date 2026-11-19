#!/usr/bin/env bash
# Run ON THE PROXMOX HOST (as root). Creates an UNPRIVILEGED Debian 12 LXC container, pushes this repo's deploy bundle
# (and optionally certs / the API artifact) into it. It does NOT provision: you then run the provision script inside.
#
#   pct-create.sh --role db  --ctid 210 --name rp-db  --ip 10.10.10.10/24 --gw 10.10.10.1 [--bridge vmbr1] [--certs DIR]
#   pct-create.sh --role api --ctid 211 --name rp-api --ip 10.10.10.11/24 --gw 10.10.10.1 [--bridge vmbr1] [--certs DIR] [--artifact FILE]
#
# Recommended topology (mirrors the compose file): TWO containers on a private bridge/VLAN. Only the API container is
# reachable from the LAN (via your reverse proxy); the DB container only accepts 5432 from the API container.
set -euo pipefail

ROLE="" CTID="" NAME="" IP="dhcp" GW="" BRIDGE="vmbr0" STORAGE="local-lvm" TPL_STORAGE="local"
MEM=2048 CORES=2 DISK=16 CERTS="" ARTIFACT=""
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --role) ROLE="$2"; shift 2;; --ctid) CTID="$2"; shift 2;; --name) NAME="$2"; shift 2;;
    --ip) IP="$2"; shift 2;; --gw) GW="$2"; shift 2;; --bridge) BRIDGE="$2"; shift 2;;
    --storage) STORAGE="$2"; shift 2;; --memory) MEM="$2"; shift 2;; --cores) CORES="$2"; shift 2;;
    --disk) DISK="$2"; shift 2;; --certs) CERTS="$2"; shift 2;; --artifact) ARTIFACT="$2"; shift 2;;
    *) echo "unknown option $1" >&2; exit 2;;
  esac
done
[[ "$ROLE" == db || "$ROLE" == api ]] || { echo "--role db|api required" >&2; exit 2; }
[[ -n "$CTID" && -n "$NAME" ]] || { echo "--ctid and --name required" >&2; exit 2; }
command -v pct >/dev/null || { echo "pct not found: run this on the Proxmox host" >&2; exit 1; }

pveam update >/dev/null
TPL="$(pveam available --section system | awk '/debian-12-standard/ {print $2}' | sort -V | tail -1)"
pveam list "$TPL_STORAGE" | grep -q "$TPL" || pveam download "$TPL_STORAGE" "$TPL"

NET="name=eth0,bridge=$BRIDGE,ip=$IP,firewall=1"; [[ -n "$GW" ]] && NET="$NET,gw=$GW"
pct create "$CTID" "$TPL_STORAGE:vztmpl/$TPL" \
  --hostname "$NAME" --ostype debian --unprivileged 1 --features nesting=0 \
  --cores "$CORES" --memory "$MEM" --swap 512 --rootfs "$STORAGE:$DISK" \
  --net0 "$NET" --onboot 1
pct start "$CTID"
for _ in $(seq 1 30); do pct exec "$CTID" -- getent hosts deb.debian.org >/dev/null 2>&1 && break; sleep 2; done

# Bundle only what provisioning needs (never legacy/, certs/, .env).
BUNDLE="$(mktemp --suffix=.tgz)"
tar -C "$REPO" -czf "$BUNDLE" deploy db scripts docker/postgres/init
pct push "$CTID" "$BUNDLE" /root/rp-bundle.tgz && rm -f "$BUNDLE"
pct exec "$CTID" -- bash -c 'mkdir -p /opt/reflectopatronus/bundle && tar -C /opt/reflectopatronus/bundle -xzf /root/rp-bundle.tgz'

if [[ -n "$CERTS" ]]; then
  pct exec "$CTID" -- install -d -m 0700 /root/pki-in
  if [[ "$ROLE" == db ]]; then
    pct push "$CTID" "$CERTS/ca.crt" /root/pki-in/ca.crt
    pct push "$CTID" "$CERTS/postgres-server.crt" /root/pki-in/server.crt
    pct push "$CTID" "$CERTS/postgres-server.key" /root/pki-in/server.key
  else
    pct push "$CTID" "$CERTS/ca.crt" /root/pki-in/ca.crt
    pct push "$CTID" "$CERTS/api-client.crt" /root/pki-in/api-client.crt
    pct push "$CTID" "$CERTS/api-client.key" /root/pki-in/api-client.key
  fi
fi
[[ -n "$ARTIFACT" ]] && pct push "$CTID" "$ARTIFACT" /root/reflectopatronus-api.tar.gz

cat <<MSG

Container $CTID ($NAME) is up with the bundle in /opt/reflectopatronus/bundle.
Next: create /root/rp.env inside it (see docs/DEPLOYMENT.md), then run:
  pct exec $CTID -- bash /opt/reflectopatronus/bundle/deploy/lxc/provision-$ROLE.sh
MSG
