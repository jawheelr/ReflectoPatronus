#!/usr/bin/env bash
# Checks the gateway from the outside using OpenSSL 3.5 (run in a container, so your OpenSSL version does not matter):
#   - a client WITHOUT a certificate is refused (mTLS)
#   - which key-exchange group was negotiated; PASS for X25519MLKEM768 (post-quantum hybrid)
#   - with a device certificate the request goes through
# Usage: scripts/check-gateway.sh HOST:PORT [DEVICE_NAME]     (DEVICE_NAME = a certificate from issue-device-cert.sh)
# Needs docker (pulls alpine:3.22). Set DOCKER_NETWORK=<network> to test a gateway that is only reachable on that network.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"; CERT_DIR="${CERT_DIR:-$ROOT/certs}"
TARGET="${1:?usage: $0 HOST:PORT [DEVICE_NAME]}"; DEV="${2:-}"; HOST="${TARGET%:*}"
NET=(); [[ -n ${DOCKER_NETWORK:-} ]] && NET=(--network "$DOCKER_NETWORK")
run() { docker run --rm ${NET[@]+"${NET[@]}"} -v "$CERT_DIR:/c:ro" alpine:3.22 sh -c "apk add -q --no-cache openssl >/dev/null 2>&1; $1" 2>&1; }
echo "== OpenSSL in the test container: $(run 'openssl version')"
echo "== 1. no client certificate (must be refused)"
out="$(run "printf 'GET /health/live HTTP/1.0\r\n\r\n' | openssl s_client -connect $TARGET -servername $HOST -tls1_3 -CAfile /c/client-ca.crt -quiet 2>&1 | head -5" || true)"
if echo "$out" | grep -qiE "HTTP/1\.[01] 200"; then echo "  FAIL: served without a client certificate"; exit 1; else echo "  PASS: refused ($(echo "$out" | grep -iE 'alert|error' | head -1 | cut -c1-90))"; fi
echo "== 2. offering only X25519MLKEM768 (forces the post-quantum hybrid)"
CERTARGS=""; [[ -n $DEV ]] && CERTARGS="-cert /c/devices/$DEV.crt -key /c/devices/$DEV.key"
pq="$(run "openssl s_client -connect $TARGET -servername $HOST -tls1_3 -groups X25519MLKEM768 -CAfile /c/client-ca.crt $CERTARGS </dev/null 2>&1 | grep -E 'Negotiated TLS1.3 group|Server Temp Key|Verification|error' | head -4")"
echo "$pq" | sed 's/^/  /'
if echo "$pq" | grep -q "X25519MLKEM768"; then echo "  PASS: post-quantum hybrid key exchange negotiated"; else echo "  FAIL: X25519MLKEM768 was not negotiated"; fi
if [[ -n $DEV ]]; then
  echo "== 3. with certificate '$DEV'"
  run "printf 'GET /health/live HTTP/1.0\r\nHost: $HOST\r\n\r\n' | openssl s_client -connect $TARGET -servername $HOST -tls1_3 -CAfile /c/client-ca.crt $CERTARGS -quiet 2>&1 | grep -m1 -E '^HTTP/' || echo 'no HTTP response'" | sed 's/^/  /'
fi
