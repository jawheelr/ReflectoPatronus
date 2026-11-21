#!/usr/bin/env bash
set -euo pipefail

CONF_DIR=/etc/reflectopatronus/conf.d
/usr/local/bin/rp-install-pki.sh /run/input-certs /etc/reflectopatronus/pki

# Translate the shared `key = value` settings into postgres -c flags (official image has no conf.d include).
if [[ "${1:-}" == postgres ]]; then
  shift
  args=(postgres)
  shopt -s nullglob
  for f in "$CONF_DIR"/*.conf; do
    while IFS= read -r line; do
      line="${line%%#*}"; line="${line#"${line%%[![:space:]]*}"}"; line="${line%"${line##*[![:space:]]}"}"
      [[ -z "$line" || "$line" != *=* ]] && continue
      key="${line%%=*}"; key="${key%"${key##*[![:space:]]}"}"
      val="${line#*=}"; val="${val#"${val%%[![:space:]]*}"}"; val="${val#\'}"; val="${val%\'}"
      args+=(-c "$key=$val")
    done < "$f"
  done
  set -- "${args[@]}" "$@"
fi
exec /usr/local/bin/docker-entrypoint.sh "$@"
