#!/usr/bin/env bash
# Build the React console into src/ReflectoPatronus.Api/wwwroot WITHOUT local Node (uses the Node image), so the
# Docker image and the LXC artifact both ship it. Re-run `docker compose up -d --build api` afterwards.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/src/ReflectoPatronus.Api/wwwroot"
rm -rf "$OUT"; mkdir -p "$OUT"
docker run --rm -v "$ROOT/src/web:/src:ro" -v "$OUT:/out" node:24-slim \
  sh -c 'cp -r /src /work && cd /work && rm -rf node_modules dist && npm ci --no-audit --no-fund --ignore-scripts && npm run build && cp -r dist/. /out/ && chmod -R a+rX /out'
echo "built $OUT"
