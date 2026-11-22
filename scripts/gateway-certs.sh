#!/usr/bin/env bash
# Creates the certificates for the remote-access gateway (idempotent; never overwrites). Output in $CERT_DIR (default ./certs):
#   client-ca.crt / client-ca.key   the "device CA": signs one client certificate per device (scripts/issue-device-cert.sh)
#   gateway.crt / gateway.key       the gateway's server certificate, signed by that CA, for the host names you pass
# Usage: scripts/gateway-certs.sh HOSTNAME[,HOSTNAME...]       e.g.  rp.example.net,192.168.1.20
#
# Server certificate alternatives: put your own gateway.crt / gateway.key in $CERT_DIR first (e.g. the Let's Encrypt
# certificate exported from the Synology, trusted by every browser and OS with no pinning). Only client-ca.* is still needed
# so the gateway can verify devices. Keep client-ca.key somewhere safe: whoever holds it can mint device certificates.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CERT_DIR="${CERT_DIR:-$ROOT/certs}"; mkdir -p "$CERT_DIR"; chmod 700 "$CERT_DIR"
HOSTS="${1:-${GATEWAY_HOSTNAMES:-}}"
[[ -n $HOSTS ]] || { echo "usage: $0 HOSTNAME[,HOSTNAME...]  (the public name(s) clients will use)" >&2; exit 2; }
SAN="DNS:localhost,IP:127.0.0.1"
IFS=, read -ra H <<<"$HOSTS"
for h in "${H[@]}"; do [[ $h =~ ^[A-Za-z0-9.-]+$ ]] || { echo "bad host name: $h" >&2; exit 2; }
  if [[ $h =~ ^[0-9.]+$ ]]; then SAN="$SAN,IP:$h"; else SAN="$SAN,DNS:$h"; fi; done

if [[ ! -e $CERT_DIR/client-ca.key ]]; then
  openssl ecparam -name prime256v1 -genkey -noout -out "$CERT_DIR/client-ca.key"
  openssl req -x509 -new -key "$CERT_DIR/client-ca.key" -sha256 -days 3650 -subj "/CN=ReflectoPatronus Device CA" \
    -addext "basicConstraints=critical,CA:TRUE,pathlen:0" -addext "keyUsage=critical,keyCertSign,cRLSign" -out "$CERT_DIR/client-ca.crt"
  chmod 600 "$CERT_DIR/client-ca.key"; echo "created device CA"
fi
if [[ -e $CERT_DIR/gateway.crt ]]; then echo "keeping existing gateway.crt (to change host names, delete gateway.crt/.key and re-run)"
else
  openssl ecparam -name prime256v1 -genkey -noout -out "$CERT_DIR/gateway.key"
  openssl req -new -key "$CERT_DIR/gateway.key" -subj "/CN=${H[0]}" -out "$CERT_DIR/gateway.csr"
  printf 'basicConstraints=CA:FALSE\nkeyUsage=digitalSignature\nextendedKeyUsage=serverAuth\nsubjectAltName=%s\n' "$SAN" > "$CERT_DIR/gateway.ext"
  openssl x509 -req -in "$CERT_DIR/gateway.csr" -CA "$CERT_DIR/client-ca.crt" -CAkey "$CERT_DIR/client-ca.key" -CAcreateserial \
    -out "$CERT_DIR/gateway.crt" -days 825 -sha256 -extfile "$CERT_DIR/gateway.ext" 2>/dev/null
  rm -f "${CERT_DIR:?}/gateway.csr" "${CERT_DIR:?}/gateway.ext"
  chmod 644 "$CERT_DIR/gateway.key"   # the gateway container runs unprivileged; tighten with a secret mount in production
  echo "created gateway certificate for: $SAN"
fi
chmod 644 "$CERT_DIR"/client-ca.crt "$CERT_DIR"/gateway.crt
