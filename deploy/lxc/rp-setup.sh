#!/usr/bin/env bash
# Interactive first-run setup for a native LXC install. Run INSIDE the container as root:
#   rp-setup [all|db|api] [--yes] [--no-provision] [--first-boot]
# It asks for everything the server needs (and generates strong random values when you just press Enter), writes
# /root/rp.env (mode 0600), sets up certificates, then runs deploy/lxc/provision-<role>.sh.
#   --yes            never prompt; use values already in /root/rp.env or the environment (defaults where they exist)
#   --no-provision   only collect answers / certificates, do not install anything
#   preseed          put answers in /root/rp-preseed.env (see deploy/lxc/preseed.env.example): RP_ROLE plus any values;
#                    unanswered questions are still asked. Complete file = unattended.
#   --first-boot     used by rp-firstboot.service: shows a banner on the console and waits for Enter before starting
#                    (skip permanently with: touch /var/lib/reflectopatronus/skip-firstboot)
# Anything already present in /root/rp.env (or exported) is used as-is and not asked again, so re-running is safe and
# unattended installs can pre-seed rp.env. Passwords must not contain  ;  '  "  \  $  or spaces.
set -euo pipefail
B=/opt/reflectopatronus/bundle
ENVF=/root/rp.env
PKI_IN=/root/pki-in
ROLE="" ASSUME_YES=false PROVISION=true FIRST_BOOT=false
for a in "$@"; do case "$a" in all|db|api) ROLE="$a";; --yes|-y) ASSUME_YES=true;; --no-provision) PROVISION=false;; --first-boot) FIRST_BOOT=true;;
  -h|--help) sed -n 2,17p "${BASH_SOURCE[0]}"; exit 0;; *) echo "unknown argument: $a" >&2; exit 2;; esac; done
[[ $EUID -eq 0 ]] || { echo "run as root" >&2; exit 1; }
INTERACTIVE=true; { $ASSUME_YES || [[ ! -t 0 ]]; } && INTERACTIVE=false
[[ -f $ENVF ]] && { set -a; source "$ENVF"; set +a; }
# Preseed file: pre-answers the questions. Anything missing is still asked; a complete one makes the install fully
# unattended (no banner, no confirmation). It is deleted after a successful install because it holds passwords.
PRESEED_FILE=/root/rp-preseed.env; PRESEED=false
load_preseed() { if [[ -f $PRESEED_FILE ]]; then set -a; source "$PRESEED_FILE"; set +a; PRESEED=true; ROLE="${ROLE:-${RP_ROLE:-}}"; fi; return 0; }
load_preseed
if $FIRST_BOOT && ! $PRESEED; then
  [[ -e /var/lib/reflectopatronus/skip-firstboot || -e /var/lib/reflectopatronus/configured ]] && exit 0
  [[ -t 0 ]] || exit 0
  # Output printed before someone attaches to the console is lost, so repeat the banner until a key is pressed.
  while true; do
    clear 2>/dev/null || true
    echo "=============================================="
    echo "  ReflectoPatronus - first-time setup"
    echo "=============================================="
    echo "This container is not configured yet. The setup asks for passwords, addresses, certificates"
    echo "and the first administrator's login (there is no default login: you choose it)."
    echo "(Ctrl-C skips it; run  rp-setup  later. To never show this: touch /var/lib/reflectopatronus/skip-firstboot)"
    echo
    if read -rt 10 -p "Press Enter to begin... " _; then break; fi
    if [[ -f $PRESEED_FILE ]]; then echo; echo "Preseed file found, continuing automatically."; load_preseed; break; fi
  done
  echo
elif $FIRST_BOOT; then
  [[ -e /var/lib/reflectopatronus/configured ]] && exit 0
  echo "ReflectoPatronus first-time setup: using $PRESEED_FILE"
fi
[[ -f $B/deploy/common/setup-lib.sh ]] || { echo "ERROR: $B/deploy/common/setup-lib.sh missing (is the bundle installed?)" >&2; exit 1; }
source "$B/deploy/common/setup-lib.sh"   # gen, ask, ask_admin, verify_login, ... (shared with the Docker setup script)

if [[ -z $ROLE ]]; then
  $INTERACTIVE || die "role required: rp-setup all|db|api (or RP_ROLE in $PRESEED_FILE)"
  echo "What should this container run?"
  echo "  1) all  - database AND application together in this one container (simplest)"
  echo "  2) db   - PostgreSQL database only (pair it with an api container)"
  echo "  3) api  - application server + web console only (needs a db container)"
  read -rp "Choice [1]: " c; case "${c:-1}" in 1|all) ROLE=all;; 2|db) ROLE=db;; 3|api) ROLE=api;; *) die "choose 1, 2 or 3";; esac
fi

my_ip() { ip -4 -o addr show scope global 2>/dev/null | awk '{sub("/.*","",$4); print $4; exit}'; }

echo "== ReflectoPatronus setup: $ROLE container =="
WANT_DB=false WANT_API=false
[[ $ROLE == db || $ROLE == all ]] && WANT_DB=true
[[ $ROLE == api || $ROLE == all ]] && WANT_API=true
if [[ $ROLE == all ]]; then LISTEN_ADDRESSES=127.0.0.1; DB_IP=127.0.0.1; fi   # db is local only; not exposed to the network
if $WANT_DB; then
  ask POSTGRES_DB      "Database name"                          reflectopatronus name
  ask POSTGRES_USER    "Database admin role (owns the schema)"  reflectopatronus_admin name
  ask POSTGRES_PASSWORD "Database admin password"               ""  newsecret
  ask APP_DB_USER      "Application role (restricted, DML only)" reflectopatronus_app name
  ask APP_DB_PASSWORD  "Application role password" "" newsecret
  [[ $ROLE == all ]] || ask LISTEN_ADDRESSES "Address PostgreSQL listens on (this container's IP)" "$(my_ip)" host

  NEED_FILES=(ca.crt server.crt server.key); [[ $ROLE == all ]] && NEED_FILES+=(api-client.crt api-client.key)
  missing=false; for f in "${NEED_FILES[@]}"; do [[ -f $PKI_IN/$f ]] || missing=true; done
  if $missing; then
    gen_certs=true
    if [[ -n ${RP_PKI_DIR:-} ]]; then
      for f in "${NEED_FILES[@]}"; do [[ -f $RP_PKI_DIR/$f ]] || die "RP_PKI_DIR: $RP_PKI_DIR/$f not found"; done
      gen_certs=false; install -d -m 0700 "$PKI_IN"; for f in "${NEED_FILES[@]}"; do cp "$RP_PKI_DIR/$f" "$PKI_IN/"; done
    elif $INTERACTIVE && ! $PRESEED; then
      echo "Certificates (PostgreSQL requires TLS 1.3 + client certificates):"
      echo "  1) Generate a new private CA and the server + API client certificates here (simplest)"
      echo "  2) I will provide my own (a directory with ${NEED_FILES[*]})"
      read -rp "Choice [1]: " c
      if [[ ${c:-1} == 2 ]]; then
        gen_certs=false; read -rp "Directory with those files: " d
        for f in "${NEED_FILES[@]}"; do [[ -f $d/$f ]] || die "$d/$f not found"; done
        install -d -m 0700 "$PKI_IN"; for f in "${NEED_FILES[@]}"; do cp "$d/$f" "$PKI_IN/"; done
      fi
    fi
    if $gen_certs; then
      echo "Generating certificates..."
      export POSTGRES_USER DB_EXTRA_SANS="IP:$LISTEN_ADDRESSES,DNS:$(hostname)"
      bash "$B/scripts/generate-dev-certs.sh" >/dev/null
      C="$B/certs"; install -d -m 0700 "$PKI_IN"
      install -m 0644 "$C/ca.crt" "$PKI_IN/ca.crt"; install -m 0644 "$C/postgres-server.crt" "$PKI_IN/server.crt"
      install -m 0600 "$C/postgres-server.key" "$PKI_IN/server.key"
      if [[ $ROLE == all ]]; then
        install -m 0644 "$C/api-client.crt" "$PKI_IN/api-client.crt"; install -m 0644 "$C/api-client.key" "$PKI_IN/api-client.key"
      else
        tar -C "$C" -czf /root/rp-pki-for-api.tgz ca.crt api-client.crt api-client.key; chmod 0600 /root/rp-pki-for-api.tgz
        echo "  The API container needs /root/rp-pki-for-api.tgz from here (instructions at the end)."
      fi
      echo "  The CA private key stays in $C/ca.key; keep a copy offline."
    fi
  fi
fi
if $WANT_API; then
  [[ $ROLE == all ]] || ask DB_IP "Address of the database container (db)" "" host
  ask POSTGRES_DB      "Database name"                           reflectopatronus name
  ask APP_DB_USER      "Application role name"                   reflectopatronus_app name
  ask APP_DB_PASSWORD  "Application role password (the one you set on the DB container)" "" secret
  ask_admin   # no default login: the installer chooses the first administrator
  # Remote access: Caddy requiring a device certificate (mTLS) with post-quantum key exchange, on its own port so
  # 80/443 stay free for something else (e.g. a Synology reverse proxy).
  ask_yn RP_GATEWAY "Enable the remote-access gateway (client-certificate mTLS + post-quantum TLS)?" n
  if [[ $RP_GATEWAY == y ]]; then
    ask GATEWAY_HOSTNAMES "Public host name(s) or IP(s) clients will connect to (comma separated)" "" text
    ask GATEWAY_PORT "Public TCP port for the gateway (not 80/443)" 8443 text
    [[ $GATEWAY_PORT =~ ^[0-9]+$ && $GATEWAY_PORT -ge 1024 && $GATEWAY_PORT -le 65535 ]] || die "GATEWAY_PORT must be 1024-65535"
    [[ -n ${GATEWAY_SHARED_SECRET:-} ]] || GATEWAY_SHARED_SECRET="$(gen 40)"
  fi
  if [[ $ROLE == api ]] && [[ ! -f $PKI_IN/ca.crt || ! -f $PKI_IN/api-client.crt || ! -f $PKI_IN/api-client.key ]]; then
    src=/root/rp-pki-for-api.tgz; [[ -n ${RP_PKI_DIR:-} ]] && src="$RP_PKI_DIR"
    if [[ ! -f $src && ! -d $src ]] && $INTERACTIVE; then
      echo "The API needs ca.crt, api-client.crt and api-client.key (made by 'rp-setup db'). On the Proxmox host:"
      echo "  pct pull <db-ctid> /root/rp-pki-for-api.tgz /tmp/rp-pki.tgz && pct push <this-ctid> /tmp/rp-pki.tgz /root/rp-pki-for-api.tgz"
      read -rp "Path to the .tgz or a directory with the files (Enter = wait for it at $src): " p
      if [[ -n $p ]]; then src="$p"; else read -rp "Press Enter once the file is in place... " _; fi
    fi
    install -d -m 0700 "$PKI_IN"
    if [[ -d $src ]]; then cp "$src/ca.crt" "$src/api-client.crt" "$src/api-client.key" "$PKI_IN/"
    elif [[ -f $src ]]; then tar -C "$PKI_IN" -xzf "$src" ca.crt api-client.crt api-client.key
    else die "certificates not found ($src). Copy them in, then run rp-setup api again."; fi
  fi
fi

# --- save the answers (re-runs reuse them) ---
KEYS_DB=(POSTGRES_DB POSTGRES_USER POSTGRES_PASSWORD APP_DB_USER APP_DB_PASSWORD LISTEN_ADDRESSES)
KEYS_API=(DB_IP POSTGRES_DB APP_DB_USER APP_DB_PASSWORD BOOTSTRAP_ADMIN_USER BOOTSTRAP_ADMIN_PASSWORD)
KEYS_GW=(RP_GATEWAY GATEWAY_HOSTNAMES GATEWAY_PORT GATEWAY_SHARED_SECRET)
KEYS_API+=("${KEYS_GW[@]}")
case "$ROLE" in db) KEYS=("${KEYS_DB[@]}");; api) KEYS=("${KEYS_API[@]}");; *) KEYS=(POSTGRES_DB POSTGRES_USER POSTGRES_PASSWORD APP_DB_USER APP_DB_PASSWORD LISTEN_ADDRESSES DB_IP BOOTSTRAP_ADMIN_USER BOOTSTRAP_ADMIN_PASSWORD "${KEYS_GW[@]}");; esac
( umask 077; { for k in "${KEYS[@]}"; do printf '%s=%q\n' "$k" "${!k:-}"; done; } > "$ENVF" )
echo "Saved answers to $ENVF (mode 0600)."
if ((${#GENERATED[@]})); then echo "Generated values (shown ONCE here and stored in $ENVF; record them in your password manager):"; printf '  %s\n' "${GENERATED[@]}"; fi

if $INTERACTIVE && ! $PRESEED; then
  echo; echo "About to install and configure the $ROLE role."; read -rp "Continue? [Y/n] " c; [[ ${c:-Y} =~ ^[Yy] ]] || { echo "Stopped. Run rp-setup $ROLE to resume."; exit 0; }
fi
$PROVISION || { echo "--no-provision: stopping before installation."; exit 0; }
$WANT_DB && bash "$B/deploy/lxc/provision-db.sh"
$WANT_API && bash "$B/deploy/lxc/provision-api.sh"
install -d -m 0755 /var/lib/reflectopatronus && touch /var/lib/reflectopatronus/configured   # stops the first-boot wizard from running again
[[ -f $PRESEED_FILE ]] && shred -u "$PRESEED_FILE" 2>/dev/null || rm -f "$PRESEED_FILE"

if $WANT_API && [[ ${RP_GATEWAY:-n} == y ]]; then
  echo "Setting up the remote-access gateway..."
  GWP=/root/rp-gateway-pki; GWD=/etc/reflectopatronus/gateway-pki
  CERT_DIR="$GWP" bash "$B/scripts/gateway-certs.sh" "$GATEWAY_HOSTNAMES" | grep -vE "subject=|self-signature"
  install -d -m 0750 -o root -g rpgw "$GWD"
  install -m 0644 -o root -g rpgw "$GWP/client-ca.crt" "$GWP/gateway.crt" "$GWD/"
  install -m 0640 -o root -g rpgw "$GWP/gateway.key" "$GWD/gateway.key"
  install -m 0644 "$B/deploy/gateway/Caddyfile" /etc/reflectopatronus/Caddyfile
  ( umask 077; printf 'GATEWAY_PORT=%s\nGATEWAY_CERT_DIR=%s\nAPI_UPSTREAM=127.0.0.1:8080\nGATEWAY_SHARED_SECRET=%s\n' \
      "$GATEWAY_PORT" "$GWD" "$GATEWAY_SHARED_SECRET" > /etc/reflectopatronus/gateway.env )
  chown root:rpgw /etc/reflectopatronus/gateway.env; chmod 0640 /etc/reflectopatronus/gateway.env
  systemctl enable --now rp-gateway
  echo "  Gateway listening on port $GATEWAY_PORT (TLS 1.3, device certificate required). The API now listens on 127.0.0.1 only."
  echo "  The device CA key is in $GWP/client-ca.key: back it up off this container, it signs every device certificate."
  if $INTERACTIVE && ! $PRESEED; then read -rp "Name of the first device to issue a certificate for (Enter to skip): " RP_DEVICE_NAME; fi
  if [[ -n ${RP_DEVICE_NAME:-} ]]; then rp-issue-device "$RP_DEVICE_NAME"; echo "  Copy it out:  pct pull <ctid> $GWP/devices/$RP_DEVICE_NAME.p12 ./$RP_DEVICE_NAME.p12"; fi
fi

if $WANT_API; then
  # The first-admin password only has to exist for the first start. Prove the login works, then remove it from disk.
  echo "Checking the administrator login..."
  if wait_for_login http://127.0.0.1:8080 "$BOOTSTRAP_ADMIN_USER" "$BOOTSTRAP_ADMIN_PASSWORD"; then
    echo "  Login works. Removing the first-admin password from $ENVF and /etc/reflectopatronus/api.env."
    sed -i -e '/^Bootstrap__/d' /etc/reflectopatronus/api.env
    sed -i -e '/^BOOTSTRAP_ADMIN_PASSWORD=/d' "$ENVF"
    systemctl restart reflectopatronus-api
  else
    echo "  WARNING: could not log in as $BOOTSTRAP_ADMIN_USER yet; leaving the settings in place. Check: journalctl -u reflectopatronus-api"
  fi
fi

echo; echo "================ $ROLE container is ready ================"
if [[ $ROLE == db ]]; then
  echo "Next, set up the API container. Copy the certificates over (run on the Proxmox host):"
  echo "  pct pull <db-ctid> /root/rp-pki-for-api.tgz /tmp/rp-pki.tgz && pct push <api-ctid> /tmp/rp-pki.tgz /root/rp-pki-for-api.tgz && shred -u /tmp/rp-pki.tgz"
  echo "Then in the API container:  rp-setup api   (DB address: ${LISTEN_ADDRESSES}; application role password: from above)"
else
  [[ $ROLE == all ]] && echo "Database and application are both running in this container (the database only listens on 127.0.0.1)."
  if [[ ${RP_GATEWAY:-n} == y ]]; then echo "Open https://${GATEWAY_HOSTNAMES%%,*}:${GATEWAY_PORT} (device certificate required) and log in as ${BOOTSTRAP_ADMIN_USER}."; echo "  Router: forward TCP ${GATEWAY_PORT} to this container; leave 80/443 alone. Issue more devices with: rp-issue-device NAME"
  else echo "Open http://$(my_ip):8080 and log in as ${BOOTSTRAP_ADMIN_USER} with the password you chose."; fi
fi
