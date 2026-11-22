#!/usr/bin/env bash
# Issues a client certificate for ONE device (iPhone, iPad, Windows PC, a browser) and bundles it as a password-protected
# .p12 to import on that device. Output: $CERT_DIR/devices/<name>.{crt,key,p12}; the p12 password is printed once.
#   scripts/issue-device-cert.sh NAME [--days 365] [--legacy-p12]
#   --legacy-p12  use older 3DES/SHA1 protection of the .p12 file, only if a device refuses the default (AES-256).
# Import: iOS/iPadOS - open the .p12 (AirDrop/Files), install the profile in Settings, enter the password.
#         Windows - double-click the .p12, "Current User", keep "Mark key as exportable" OFF. macOS - Keychain Access.
#         The app (SwiftUI / Tauri) or browser then presents it automatically to the gateway.
# Also prints the certificate's SHA-256 fingerprint: sessions are bound to it.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CERT_DIR="${CERT_DIR:-$ROOT/certs}"
NAME="${1:-}"; shift || true; DAYS=365; LEGACY=false
while [[ $# -gt 0 ]]; do case "$1" in --days) DAYS="$2"; shift 2;; --legacy-p12) LEGACY=true; shift;; *) echo "unknown option $1" >&2; exit 2;; esac; done
[[ $NAME =~ ^[A-Za-z0-9._-]{1,48}$ ]] || { echo "usage: $0 NAME [--days N] [--legacy-p12]   (NAME: letters, digits . _ -)" >&2; exit 2; }
[[ -f $CERT_DIR/client-ca.key && -f $CERT_DIR/client-ca.crt ]] || { echo "run scripts/gateway-certs.sh first" >&2; exit 1; }
D="$CERT_DIR/devices"; mkdir -p "$D"; chmod 700 "$D"
[[ -e $D/$NAME.crt ]] && { echo "device '$NAME' already has a certificate ($D/$NAME.crt); pick another name or delete it" >&2; exit 1; }
openssl ecparam -name prime256v1 -genkey -noout -out "$D/$NAME.key"; chmod 600 "$D/$NAME.key"
openssl req -new -key "$D/$NAME.key" -subj "/CN=$NAME/O=ReflectoPatronus" -out "$D/$NAME.csr"
printf 'basicConstraints=CA:FALSE\nkeyUsage=digitalSignature\nextendedKeyUsage=clientAuth\n' > "$D/$NAME.ext"
openssl x509 -req -in "$D/$NAME.csr" -CA "$CERT_DIR/client-ca.crt" -CAkey "$CERT_DIR/client-ca.key" -CAcreateserial \
  -out "$D/$NAME.crt" -days "$DAYS" -sha256 -extfile "$D/$NAME.ext" 2>/dev/null
rm -f "${D:?}/${NAME:?}.csr" "${D:?}/${NAME:?}.ext"
PW="$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 16)"
OPTS=(); $LEGACY && OPTS=(-legacy)
openssl pkcs12 -export ${OPTS[@]+"${OPTS[@]}"} -inkey "$D/$NAME.key" -in "$D/$NAME.crt" -certfile "$CERT_DIR/client-ca.crt" \
  -name "ReflectoPatronus $NAME" -passout "pass:$PW" -out "$D/$NAME.p12"
chmod 600 "$D/$NAME.p12"
echo "device certificate for '$NAME' (valid $DAYS days): $D/$NAME.p12"
echo "  p12 password (shown once): $PW"
echo "  fingerprint (SHA-256): $(openssl x509 -in "$D/$NAME.crt" -noout -fingerprint -sha256 | sed 's/.*=//; s/://g' | tr 'A-F' 'a-f')"
