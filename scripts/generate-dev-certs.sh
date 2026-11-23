#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CERT_DIR="$ROOT_DIR/certs"
mkdir -p "$CERT_DIR"
chmod 700 "$CERT_DIR"

# Idempotent: creates the CA once, then only the certificates that are still missing (never overwrites).
if [[ ! -e "$CERT_DIR/ca.key" ]]; then
  # Development-only ECDSA certificates. PQC key exchange is a separate TLS capability
  # and is NOT provided merely by using these classical certificate signatures.
  openssl ecparam -name prime256v1 -genkey -noout -out "$CERT_DIR/ca.key"
  openssl req -x509 -new -nodes -key "$CERT_DIR/ca.key" -sha256 -days 3650 \
    -subj "/CN=ReflectoPatronus Development CA" -out "$CERT_DIR/ca.crt"
fi

make_cert() {
  local name="$1" cn="$2" ext="$3" san="$4"
  [[ -e "$CERT_DIR/$name.crt" ]] && { echo "keeping existing $name.crt"; return; }
  openssl ecparam -name prime256v1 -genkey -noout -out "$CERT_DIR/$name.key"
  openssl req -new -key "$CERT_DIR/$name.key" -subj "/CN=$cn" -out "$CERT_DIR/$name.csr"
  cat > "$CERT_DIR/$name.ext" <<EXT
basicConstraints=CA:FALSE
keyUsage=digitalSignature,keyEncipherment
extendedKeyUsage=$ext
subjectAltName=$san
EXT
  openssl x509 -req -in "$CERT_DIR/$name.csr" -CA "$CERT_DIR/ca.crt" -CAkey "$CERT_DIR/ca.key" \
    -CAcreateserial -out "$CERT_DIR/$name.crt" -days 825 -sha256 -extfile "$CERT_DIR/$name.ext"
  rm -f "$CERT_DIR/$name.csr" "$CERT_DIR/$name.ext"
}

# DB_EXTRA_SANS="DNS:rp-db.lan,IP:10.0.0.5" adds names for LXC/Proxmox setups that do not use the hostname `db`.
make_cert "postgres-server" "db" "serverAuth" "DNS:db,DNS:localhost,IP:127.0.0.1${DB_EXTRA_SANS:+,$DB_EXTRA_SANS}"
make_cert "api-client" "reflectopatronus_app" "clientAuth" "DNS:reflectopatronus-api"
# Admin client cert: used ONLY by the one-off the legacy system importer / DBA tooling (CN must equal the admin DB role for verify-full).
make_cert "admin-client" "${POSTGRES_USER:-reflectopatronus_admin}" "clientAuth" "DNS:reflectopatronus-admin"
make_cert "api-https" "localhost" "serverAuth" "DNS:localhost,IP:127.0.0.1"

chmod 600 "$CERT_DIR"/ca.key "$CERT_DIR"/postgres-server.key "$CERT_DIR"/admin-client.key
# Development-only: the API container runs as a non-root UID and needs to read this bind-mounted key.
# Do not use this permission pattern for production; use a secret mount with controlled ownership instead.
chmod 644 "$CERT_DIR"/api-client.key
chmod 644 "$CERT_DIR"/*.crt

echo "Development certificates created in $CERT_DIR"
echo "Do not use these certificates in production. PostgreSQL will copy its server key and set required ownership/mode at container startup."
