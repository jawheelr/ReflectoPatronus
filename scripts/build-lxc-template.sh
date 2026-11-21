#!/usr/bin/env bash
# Build the Proxmox LXC template locally (needs Docker; zstd optional). Output: dist/reflectopatronus-<version>-$ARCH.tar.zst
# (or .tar.gz without zstd). Copy it to /var/lib/vz/template/cache/ on the Proxmox host. See docs/DEPLOYMENT.md.
#   scripts/build-lxc-template.sh [version]      (default: git describe, else "dev")
# (Proxmox x86 hosts need amd64; the default is linux/amd64 even on Apple silicon, built under emulation.)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VER="${1:-$(git -C "$ROOT" describe --tags --always 2>/dev/null || echo dev)}"; VER="${VER#v}"
PLATFORM="${RP_TEMPLATE_PLATFORM:-linux/amd64}"
ARCH="${PLATFORM#linux/}"
mkdir -p "$ROOT/dist"
docker build --platform "$PLATFORM" -f "$ROOT/deploy/lxc/template/Dockerfile" --build-arg "RP_VERSION=$VER" -t "rp-lxc-template:$VER" "$ROOT"
CID="$(docker create --platform "$PLATFORM" "rp-lxc-template:$VER" /bin/true)"
trap 'docker rm -f "$CID" >/dev/null 2>&1 || true' EXIT
if command -v zstd >/dev/null; then OUT="$ROOT/dist/reflectopatronus-$VER-$ARCH.tar.zst"; docker export "$CID" | zstd -19 -T0 -q -o "$OUT" -f
else OUT="$ROOT/dist/reflectopatronus-$VER-$ARCH.tar.gz"; docker export "$CID" | gzip -9 > "$OUT"; fi
( cd "$ROOT/dist" && shasum -a 256 "$(basename "$OUT")" > "$(basename "$OUT").sha256" )
echo "built $OUT"; ls -lh "$OUT"
