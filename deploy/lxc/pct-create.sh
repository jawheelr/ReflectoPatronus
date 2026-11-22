#!/usr/bin/env bash
# Run ON THE PROXMOX HOST (as root). Creates an UNPRIVILEGED Debian 12 LXC container, pushes this repo's deploy bundle
# (and optionally certs / the API artifact) into it. It does NOT provision: you then run the provision script inside.
#
#   pct-create.sh --role db  --ctid 210 --name rp-db  --ip 10.10.10.10/24 --gw 10.10.10.1 [--bridge vmbr1] [--certs DIR]
#   --data-mount rootfs | volume:STORAGE:SIZE_GB | bind:/host/path   where the DB's persistent data lives (db role only).
#       rootfs  data inside the container's own disk (default when not interactive)
#       volume  a separate Proxmox volume on STORAGE (e.g. volume:local-zfs:100) mounted at /var/lib/postgresql
#       bind    an existing host directory / mount (e.g. bind:/mnt/ssd/rp-data), mounted at /var/lib/postgresql
#   When --data-mount is omitted and you run this from a terminal, you are PROMPTED for it.
#   --template local:vztmpl/reflectopatronus-X.Y.Z-amd64.tar.zst   use the downloadable ReflectoPatronus template (everything preinstalled)
#       instead of stock Debian 12 + installing at provision time; then --artifact is optional.
#   pct-create.sh --role api --ctid 211 --name rp-api --ip 10.10.10.11/24 --gw 10.10.10.1 [--bridge vmbr1] [--certs DIR] [--artifact FILE]
#
# Recommended topology (mirrors the compose file): TWO containers on a private bridge/VLAN. Only the API container is
# reachable from the LAN (via your reverse proxy); the DB container only accepts 5432 from the API container.
set -euo pipefail

ROLE="" CTID="" NAME="" IP="dhcp" GW="" BRIDGE="vmbr0" STORAGE="local-lvm" TPL_STORAGE="local"
MEM=2048 CORES=2 DISK=16 CERTS="" ARTIFACT="" DATA_MOUNT="" TEMPLATE=""
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --role) ROLE="$2"; shift 2;; --ctid) CTID="$2"; shift 2;; --name) NAME="$2"; shift 2;;
    --ip) IP="$2"; shift 2;; --gw) GW="$2"; shift 2;; --bridge) BRIDGE="$2"; shift 2;;
    --storage) STORAGE="$2"; shift 2;; --memory) MEM="$2"; shift 2;; --cores) CORES="$2"; shift 2;;
    --disk) DISK="$2"; shift 2;; --certs) CERTS="$2"; shift 2;; --artifact) ARTIFACT="$2"; shift 2;; --data-mount) DATA_MOUNT="$2"; shift 2;; --template) TEMPLATE="$2"; shift 2;;
    *) echo "unknown option $1" >&2; exit 2;;
  esac
done
[[ "$ROLE" == db || "$ROLE" == api ]] || { echo "--role db|api required" >&2; exit 2; }
[[ -n "$CTID" && -n "$NAME" ]] || { echo "--ctid and --name required" >&2; exit 2; }
command -v pct >/dev/null || { echo "pct not found: run this on the Proxmox host" >&2; exit 1; }

# --- Persistent data location (DB role). The mount is part of the container config, so it must be decided now. ---
MP_ARGS=()
DATA_DIR=/var/lib/postgresql
if [[ "$ROLE" == db ]]; then
  if [[ -z "$DATA_MOUNT" && -t 0 ]]; then
    echo "Where should the database's persistent data be stored?"
    echo "  1) Inside the container's disk (simplest)"
    echo "  2) A separate Proxmox volume on a storage you choose"
    echo "  3) A host directory / mounted volume (bind mount), e.g. /mnt/ssd/rp-data"
    read -rp "Choice [1]: " ch
    case "${ch:-1}" in
      2) read -rp "Storage name (see 'pvesm status') [$STORAGE]: " st; read -rp "Size in GB [50]: " sz
         DATA_MOUNT="volume:${st:-$STORAGE}:${sz:-50}";;
      3) read -rp "Absolute host path: " hp; DATA_MOUNT="bind:$hp";;
      *) DATA_MOUNT=rootfs;;
    esac
  fi
  case "${DATA_MOUNT:-rootfs}" in
    rootfs) ;;
    volume:*) IFS=: read -r _ vst vsz <<<"$DATA_MOUNT"
      [[ -n "$vst" && "$vsz" =~ ^[0-9]+$ ]] || { echo "use volume:STORAGE:SIZE_GB" >&2; exit 2; }
      pvesm status | awk 'NR>1{print $1}' | grep -qx "$vst" || { echo "storage '$vst' not found (pvesm status)" >&2; exit 2; }
      MP_ARGS=(--mp0 "$vst:$vsz,mp=$DATA_DIR,backup=1");;
    bind:*) HP="${DATA_MOUNT#bind:}"
      [[ "$HP" == /* && "$HP" != "/" ]] || { echo "bind path must be absolute and not /" >&2; exit 2; }
      mkdir -p "$HP"
      # Unprivileged containers map container root to host uid 100000. The directory must be owned by that id.
      if [[ -n "$(ls -A "$HP" 2>/dev/null)" ]]; then
        echo "NOTE: $HP is not empty; existing PostgreSQL data there will be re-used. Make sure it is PostgreSQL 17 data."
      fi
      chown 100000:100000 "$HP"; chmod 0755 "$HP"
      MP_ARGS=(--mp0 "$HP,mp=$DATA_DIR");;
    *) echo "--data-mount must be rootfs, volume:STORAGE:SIZE_GB or bind:/host/path" >&2; exit 2;;
  esac
elif [[ -n "$DATA_MOUNT" ]]; then
  echo "NOTE: --data-mount applies to the db role only (the API container is stateless); ignored." >&2
fi

if [[ -n "$TEMPLATE" ]]; then TPL_REF="$TEMPLATE"
else
  pveam update >/dev/null
  TPL="$(pveam available --section system | awk '/debian-12-standard/ {print $2}' | sort -V | tail -1)"
  pveam list "$TPL_STORAGE" | grep -q "$TPL" || pveam download "$TPL_STORAGE" "$TPL"
  TPL_REF="$TPL_STORAGE:vztmpl/$TPL"
fi

NET="name=eth0,bridge=$BRIDGE,ip=$IP,firewall=1"; [[ -n "$GW" ]] && NET="$NET,gw=$GW"
pct create "$CTID" "$TPL_REF" \
  --hostname "$NAME" --ostype debian --unprivileged 1 --features nesting=0 \
  --cores "$CORES" --memory "$MEM" --swap 512 --rootfs "$STORAGE:$DISK" \
  --net0 "$NET" --onboot 1 "${MP_ARGS[@]}"
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
${MP_ARGS[*]:+Persistent data: ${DATA_MOUNT} -> $DATA_DIR (kept on 'pct destroy' only for bind mounts; volumes are deleted with the container).}
Next, run the interactive setup (it asks for passwords, addresses and certificates, and can generate them):
  pct enter $CTID    then:  bash /opt/reflectopatronus/bundle/deploy/lxc/rp-setup.sh $ROLE
(Unattended instead: write /root/rp.env, see docs/DEPLOYMENT.md, and run provision-$ROLE.sh.)
MSG
