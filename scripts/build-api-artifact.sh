#!/usr/bin/env bash
# Build the API into dist/reflectopatronus-api.tar.gz WITHOUT a local .NET SDK (uses the SDK image).
# The tarball is what the LXC API container runs; it is framework-dependent (needs ASP.NET Core 10 runtime).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[[ -f "$ROOT/src/ReflectoPatronus.Api/wwwroot/index.html" ]] || "$ROOT/scripts/build-web.sh"   # bundle the web console
mkdir -p "$ROOT/dist"
docker run --rm \
  -v "$ROOT/src/ReflectoPatronus.Api:/src:ro" -v "$ROOT/dist:/out" \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c 'cp -r /src /work && cd /work && rm -rf bin obj && dotnet publish -c Release -o /work/publish && tar -C /work/publish -czf /out/reflectopatronus-api.tar.gz .'
echo "built $ROOT/dist/reflectopatronus-api.tar.gz"
