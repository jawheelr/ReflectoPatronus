#!/usr/bin/env bash
# Interactive first-run setup for a native LXC install. Run INSIDE the container as root:
#   rp-setup [db|api] [--yes] [--no-provision] [--first-boot]
# It asks for everything the server needs (and generates strong random values when you just press Enter), writes
# /root/rp.env (mode 0600), sets up certificates, then runs deploy/lxc/provision-<role>.sh.
#   --yes            never prompt; use values already in /root/rp.env or the environment (defaults where they exist)
#   --no-provision   only collect answers / certificates, do not install anything
#   --first-boot     used by rp-firstboot.service: shows a banner on the console and waits for Enter before starting
#                    (skip permanently with: touch /var/lib/reflectopatronus/skip-firstboot)
# Anything already present in /root/rp.env (or exported) is used as-is and not asked again, so re-running is safe and
# unattended installs can pre-seed rp.env. Passwords must not contain  ;  '  "  \  $  or spaces.
set -euo pipefail
B=/opt/reflectopatronus/bundle
ENVF=/root/rp.env
PKI_IN=/root/pki-in
ROLE="" ASSUME_YES=false PROVISION=true FIRST_BOOT=false
for a in "$@"; do case "$a" in db|api) ROLE="$a";; --yes|-y) ASSUME_YES=true;; --no-provision) PROVISION=false;; --first-boot) FIRST_BOOT=true;;
  -h|--help) sed -n 2,14p "${BASH_SOURCE[0]}"; exit 0;; *) echo "unknown argument: $a" >&2; exit 2;; esac; done
[[ $EUID -eq 0 ]] || { echo "run as root" >&2; exit 1; }
INTERACTIVE=true; { $ASSUME_YES || [[ ! -t 0 ]]; } && INTERACTIVE=false
die() { echo "ERROR: $*" >&2; exit 1; }
if $FIRST_BOOT; then
  [[ -e /var/lib/reflectopatronus/skip-firstboot || -e /var/lib/reflectopatronus/configured ]] && exit 0
  [[ -t 0 ]] || exit 0
  # Output printed before someone attaches to the console is lost, so repeat the banner until a key is pressed.
  while true; do
    clear 2>/dev/null || true
    echo "=============================================="
    echo "  ReflectoPatronus - first-time setup"
    echo "=============================================="
    echo "This container is not configured yet. The setup asks for passwords, addresses and certificates."
    echo "(Ctrl-C skips it; run  rp-setup  later. To never show this: touch /var/lib/reflectopatronus/skip-firstboot)"
    echo
    if read -rt 10 -p "Press Enter to begin... " _; then break; fi
  done
  echo
fi
GENERATED=()   # "label: value" lines shown once at the end

[[ -f $ENVF ]] && { set -a; source "$ENVF"; set +a; }

if [[ -z $ROLE ]]; then
  $INTERACTIVE || die "role required: rp-setup db|api"
  echo "What is this container?"; echo "  1) db   - PostgreSQL database"; echo "  2) api  - application server + web console"
  read -rp "Choice [1/2]: " c; case "$c" in 1|db) ROLE=db;; 2|api) ROLE=api;; *) die "choose 1 or 2";; esac
fi

gen() { openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c "${1:-24}"; }
valid_pw() { [[ ${#1} -ge 12 && $1 != *[\;\'\"\\\$\ ]* ]]; }
valid_name() { [[ $1 =~ ^[A-Za-z_][A-Za-z0-9_]{0,62}$ ]]; }
valid_host() { [[ $1 =~ ^[A-Za-z0-9._-]+$ ]]; }

# ask VAR "Prompt" [default] [kind]      kind: text | name | host | secret | newsecret (Enter = generate)
ask() {
  local var="$1" prompt="$2" def="${3:-}" kind="${4:-text}" v v2
  [[ -n ${!var:-} ]] && return 0
  if ! $INTERACTIVE; then
    if [[ -n $def ]]; then printf -v "$var" '%s' "$def"; return 0; fi
    [[ $kind == newsecret ]] && { v="$(gen)"; printf -v "$var" '%s' "$v"; GENERATED+=("$prompt: $v"); return 0; }
    die "$var is not set (put it in $ENVF or run interactively)"
  fi
  while true; do
    case "$kind" in
      newsecret) read -rsp "$prompt (Enter = generate a random one): " v; echo
        if [[ -z $v ]]; then v="$(gen)"; GENERATED+=("$prompt: $v"); break; fi
        valid_pw "$v" || { echo "  Need 12+ characters, none of ; ' \" \\ \$ or space."; continue; }
        read -rsp "  Repeat it: " v2; echo; [[ $v == "$v2" ]] && break || echo "  They differ, try again.";;
      secret) read -rsp "$prompt: " v; echo; valid_pw "$v" && break || echo "  Need 12+ characters, none of ; ' \" \\ \$ or space.";;
      name) read -rp "$prompt${def:+ [$def]}: " v; v="${v:-$def}"; valid_name "$v" && break || echo "  Letters, digits and underscore only.";;
      host) read -rp "$prompt${def:+ [$def]}: " v; v="${v:-$def}"; [[ -n $v ]] && valid_host "$v" && break || echo "  Enter a host name or IP address.";;
      *) read -rp "$prompt${def:+ [$def]}: " v; v="${v:-$def}"; [[ -n $v ]] && break || echo "  A value is required.";;
    esac
  done
  printf -v "$var" '%s' "$v"
}

my_ip() { ip -4 -o addr show scope global 2>/dev/null | awk '{sub("/.*","",$4); print $4; exit}'; }

echo "== ReflectoPatronus setup: $ROLE container =="
if [[ $ROLE == db ]]; then
  ask POSTGRES_DB      "Database name"                          reflectopatronus name
  ask POSTGRES_USER    "Database admin role (owns the schema)"  reflectopatronus_admin name
  ask POSTGRES_PASSWORD "Database admin password"               ""  newsecret
  ask APP_DB_USER      "Application role (restricted, DML only)" reflectopatronus_app name
  ask APP_DB_PASSWORD  "Application role password (the API container needs this too)" "" newsecret
  ask LISTEN_ADDRESSES "Address PostgreSQL listens on (this container's IP)" "$(my_ip)" host

  if [[ ! -f $PKI_IN/server.crt || ! -f $PKI_IN/server.key || ! -f $PKI_IN/ca.crt ]]; then
    gen_certs=true
    if $INTERACTIVE; then
      echo "Certificates (PostgreSQL requires TLS 1.3 + client certificates):"
      echo "  1) Generate a new private CA and the server + API client certificates here (simplest)"
      echo "  2) I will provide my own (a directory with ca.crt, server.crt, server.key)"
      read -rp "Choice [1]: " c
      if [[ ${c:-1} == 2 ]]; then
        gen_certs=false; read -rp "Directory with the three files: " d
        for f in ca.crt server.crt server.key; do [[ -f $d/$f ]] || die "$d/$f not found"; done
        install -d -m 0700 "$PKI_IN"; cp "$d/ca.crt" "$d/server.crt" "$d/server.key" "$PKI_IN/"
      fi
    fi
    if $gen_certs; then
      echo "Generating certificates..."
      export POSTGRES_USER DB_EXTRA_SANS="IP:$LISTEN_ADDRESSES,DNS:$(hostname)"
      bash "$B/scripts/generate-dev-certs.sh" >/dev/null
      C="$B/certs"; install -d -m 0700 "$PKI_IN"
      install -m 0644 "$C/ca.crt" "$PKI_IN/ca.crt"; install -m 0644 "$C/postgres-server.crt" "$PKI_IN/server.crt"
      install -m 0600 "$C/postgres-server.key" "$PKI_IN/server.key"
      tar -C "$C" -czf /root/rp-pki-for-api.tgz ca.crt api-client.crt api-client.key; chmod 0600 /root/rp-pki-for-api.tgz
      echo "  The API container needs /root/rp-pki-for-api.tgz from here (instructions at the end)."
      echo "  The CA private key stays in $C/ca.key; keep it safe and offline once both containers are set up."
    fi
  fi
else
  ask DB_IP            "Address of the database container (db)" "" host
  ask POSTGRES_DB      "Database name"                           reflectopatronus name
  ask APP_DB_USER      "Application role name"                   reflectopatronus_app name
  ask APP_DB_PASSWORD  "Application role password (the one you set on the DB container)" "" secret
  ask BOOTSTRAP_ADMIN_USER "First admin user code"               ADMIN name
  ask BOOTSTRAP_ADMIN_PASSWORD "First admin password (12+ characters)" "" newsecret
  if [[ ! -f $PKI_IN/ca.crt || ! -f $PKI_IN/api-client.crt || ! -f $PKI_IN/api-client.key ]]; then
    src=/root/rp-pki-for-api.tgz
    if [[ ! -f $src ]] && $INTERACTIVE; then
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
if [[ $ROLE == db ]]; then KEYS=("${KEYS_DB[@]}"); else KEYS=("${KEYS_API[@]}"); fi
( umask 077; { for k in "${KEYS[@]}"; do printf '%s=%q\n' "$k" "${!k}"; done; } > "$ENVF" )
echo "Saved answers to $ENVF (mode 0600)."
if ((${#GENERATED[@]})); then echo "Generated values (shown ONCE here and stored in $ENVF; record them in your password manager):"; printf '  %s\n' "${GENERATED[@]}"; fi

if $INTERACTIVE; then
  echo; echo "About to install and configure the $ROLE role."; read -rp "Continue? [Y/n] " c; [[ ${c:-Y} =~ ^[Yy] ]] || { echo "Stopped. Run rp-setup $ROLE to resume."; exit 0; }
fi
$PROVISION || { echo "--no-provision: stopping before installation."; exit 0; }
bash "$B/deploy/lxc/provision-$ROLE.sh"
install -d -m 0755 /var/lib/reflectopatronus && touch /var/lib/reflectopatronus/configured   # stops the first-boot wizard from running again

echo; echo "================ $ROLE container is ready ================"
if [[ $ROLE == db ]]; then
  echo "Next, set up the API container. Copy the certificates over (run on the Proxmox host):"
  echo "  pct pull <db-ctid> /root/rp-pki-for-api.tgz /tmp/rp-pki.tgz && pct push <api-ctid> /tmp/rp-pki.tgz /root/rp-pki-for-api.tgz && shred -u /tmp/rp-pki.tgz"
  echo "Then in the API container:  rp-setup api   (DB address: ${LISTEN_ADDRESSES}; application role password: from above)"
else
  echo "Open http://$(my_ip):8080 and log in as ${BOOTSTRAP_ADMIN_USER}. The first-admin password is only used on the first start;"
  echo "remove BOOTSTRAP_ADMIN_PASSWORD from $ENVF and Bootstrap__AdminPassword from /etc/reflectopatronus/api.env afterwards."
fi
