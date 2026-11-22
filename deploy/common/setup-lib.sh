#!/usr/bin/env bash
# Shared prompt helpers for the first-time setup of BOTH runtimes (scripts/setup.sh for Docker, rp-setup for LXC).
# Source it; the caller sets (optionally) INTERACTIVE=true|false and GENERATED=() and ENVF (only used in messages).
#   ask VAR "Prompt" [default] [kind]
#     kind: text | name | host | secret | newsecret (Enter = generate) | newpw (typed twice, never generated) | adminname
#   A variable that is already set (environment, .env, preseed file) is used as-is and not asked again.
#   Not interactive and nothing set: the default is used; newsecret is generated; anything else is an error.
INTERACTIVE="${INTERACTIVE:-true}"
GENERATED=()
die() { echo "ERROR: $*" >&2; exit 1; }
gen() { openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c "${1:-24}"; }
valid_pw() { [[ ${#1} -ge 12 && $1 != *[\;\'\"\\\$\ ]* ]]; }
valid_name() { [[ $1 =~ ^[A-Za-z_][A-Za-z0-9_]{0,62}$ ]]; }
valid_user() { [[ $1 =~ ^[A-Za-z0-9._-]{2,32}$ ]]; }
valid_host() { [[ $1 =~ ^[A-Za-z0-9._-]+$ ]]; }
PW_RULE="Need 12+ characters, none of ; ' \" \\ \$ or space."

ask() {
  local var="$1" prompt="$2" def="${3:-}" kind="${4:-text}" v v2
  [[ -n ${!var:-} ]] && return 0
  if ! $INTERACTIVE; then
    if [[ -n $def ]]; then printf -v "$var" '%s' "$def"; return 0; fi
    [[ $kind == newsecret ]] && { v="$(gen)"; printf -v "$var" '%s' "$v"; GENERATED+=("$prompt: $v"); return 0; }
    die "$var is not set (set it in the environment / preseed file, or run interactively)"
  fi
  while true; do
    case "$kind" in
      newsecret) read -rsp "$prompt (Enter = generate a random one): " v; echo
        if [[ -z $v ]]; then v="$(gen)"; GENERATED+=("$prompt: $v"); break; fi
        valid_pw "$v" || { echo "  $PW_RULE"; continue; }
        read -rsp "  Repeat it: " v2; echo; [[ $v == "$v2" ]] && break || echo "  They differ, try again.";;
      newpw) read -rsp "$prompt: " v; echo
        valid_pw "$v" || { echo "  $PW_RULE"; continue; }
        read -rsp "  Repeat it: " v2; echo; [[ $v == "$v2" ]] && break || echo "  They differ, try again.";;
      secret) read -rsp "$prompt: " v; echo; valid_pw "$v" && break || echo "  $PW_RULE";;
      adminname) read -rp "$prompt: " v; valid_user "$v" && break || echo "  2-32 characters: letters, digits, dot, dash, underscore.";;
      name) read -rp "$prompt${def:+ [$def]}: " v; v="${v:-$def}"; valid_name "$v" && break || echo "  Letters, digits and underscore only.";;
      host) read -rp "$prompt${def:+ [$def]}: " v; v="${v:-$def}"; [[ -n $v ]] && valid_host "$v" && break || echo "  Enter a host name or IP address.";;
      *) read -rp "$prompt${def:+ [$def]}: " v; v="${v:-$def}"; [[ -n $v ]] && break || echo "  A value is required.";;
    esac
  done
  printf -v "$var" '%s' "$v"
}

# ask_yn VAR "Question" [y|n default]  -> VAR becomes y or n
ask_yn() {
  local var="$1" prompt="$2" def="${3:-n}" v
  if [[ -n ${!var:-} ]]; then case "$(printf %s "${!var}" | tr A-Z a-z)" in y|yes|1|true) printf -v "$var" y;; *) printf -v "$var" n;; esac; return 0; fi
  if ! $INTERACTIVE; then printf -v "$var" '%s' "$def"; return 0; fi
  read -rp "$prompt [$([[ $def == y ]] && echo Y/n || echo y/N)]: " v; v="${v:-$def}"
  case "$(printf %s "$v" | tr A-Z a-z)" in y|yes) printf -v "$var" y;; *) printf -v "$var" n;; esac
}

# The first admin is ALWAYS chosen by whoever installs: there is no built-in default login.
ask_admin() {
  ask BOOTSTRAP_ADMIN_USER "Choose the first administrator's login name" "" adminname
  ask BOOTSTRAP_ADMIN_PASSWORD "Choose that administrator's password" "" newpw
  valid_user "$BOOTSTRAP_ADMIN_USER" || die "BOOTSTRAP_ADMIN_USER must be 2-32 characters: letters, digits, dot, dash, underscore"
  valid_pw "$BOOTSTRAP_ADMIN_PASSWORD" || die "BOOTSTRAP_ADMIN_PASSWORD: $PW_RULE"
}

# verify_login BASE_URL USER PASSWORD  -> 0 if the API accepts the login (the session is revoked again straight away).
verify_login() {
  local url="$1" u="$2" p="$3" body tok gw=()
  # With the gateway enabled the API only serves requests that carry the gateway secret and a certificate fingerprint.
  if [[ -n ${GATEWAY_SHARED_SECRET:-} ]]; then gw=(-H "X-RP-Gateway-Key: $GATEWAY_SHARED_SECRET" -H "X-RP-Client-Cert-Fp: setup-check"); fi
  body="$(curl -fsS -m 10 -X POST "$url/api/v1/auth/login" -H 'content-type: application/json' ${gw[@]+"${gw[@]}"} \
    -d "{\"userCode\":\"$u\",\"password\":\"$p\",\"deviceName\":\"setup-check\",\"platform\":\"web\"}" 2>/dev/null)" || return 1
  tok="$(printf '%s' "$body" | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p')"
  [[ -n $tok ]] || return 1
  curl -fsS -m 10 -X POST "$url/api/v1/auth/logout" -H "authorization: Bearer $tok" ${gw[@]+"${gw[@]}"} >/dev/null 2>&1 || true
  return 0
}

# wait_for_login BASE_URL USER PASSWORD [seconds]
wait_for_login() { local i; for ((i=0; i<${4:-40}; i+=2)); do verify_login "$1" "$2" "$3" && return 0; sleep 2; done; return 1; }
