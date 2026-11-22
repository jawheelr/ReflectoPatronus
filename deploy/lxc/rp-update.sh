#!/usr/bin/env bash
# rp-update: update THIS ReflectoPatronus container to the newest build from GitHub.
#   rp-update                interactive: shows what is new, asks, backs up, applies, checks it came back
#   rp-update --check        only look (exit 10 if an update is available); --check --notify also leaves a note for the login message
#   rp-update --yes          no questions (for scripts)          rp-update --backup-db   also dump the database first
#   rp-update --channel edge|stable   edge (default) = every commit on main; stable = the latest vX.Y.Z release
#   rp-update --force        re-apply even when already on that commit
# Containers that hold only the database, or only the API, are updated separately: update the DATABASE container first.
set -euo pipefail
REPO="${RP_REPO:-jawheelr/ReflectoPatronus}"; BRANCH="${RP_BRANCH:-main}"; CHANNEL=edge; ASSUME=0; CHECK=0; NOTIFY=0; FORCE=0; BACKUPDB=0
OPT=/opt/reflectopatronus; STATE=/var/lib/reflectopatronus; BAK=/var/backups/reflectopatronus
while [[ $# -gt 0 ]]; do case "$1" in
  --check) CHECK=1 ;; --notify) NOTIFY=1 ;; --yes|-y) ASSUME=1 ;; --force) FORCE=1 ;; --backup-db) BACKUPDB=1 ;;
  --channel) CHANNEL="${2:?edge or stable}"; shift ;; -h|--help) sed -n 2,10p "$0"; exit 0 ;;
  *) echo "unknown option $1" >&2; exit 2 ;; esac; shift; done
say() { printf '%s\n' "$*"; }
ask() { [[ $ASSUME -eq 1 ]] && return 0; local a; read -r -p "$1 [y/N] " a; [[ "$a" =~ ^[Yy] ]]; }
curl_() { curl -fsSL --max-time 30 -A "ReflectoPatronus-update-check" "$@"; }
current=$(cat "$OPT/COMMIT" 2>/dev/null || echo unknown)
if grep -qs '^Updates__Enabled=false' /etc/reflectopatronus/api.env 2>/dev/null && [[ $CHECK -eq 1 ]]; then exit 0; fi

# 1. what is the newest commit, and are we behind?
latest=$(curl_ -H 'Accept: application/vnd.github+json' "https://api.github.com/repos/$REPO/commits/$BRANCH" | grep -o '"sha": *"[0-9a-f]\{40\}"' | head -1 | grep -o '[0-9a-f]\{40\}') || { say "Could not reach GitHub."; exit 1; }
[[ -n "$latest" ]] || { say "GitHub did not answer as expected."; exit 1; }
behind=""; if [[ "$current" != unknown && "$current" != "$latest" ]]; then
  behind=$(curl_ "https://api.github.com/repos/$REPO/compare/$current...$latest" 2>/dev/null | grep -o '"ahead_by": *[0-9]*' | head -1 | grep -o '[0-9]*$' || true); fi
mkdir -p "$STATE"
if [[ "$current" == "$latest" || "${current:0:7}" == "${latest:0:7}" ]]; then
  rm -f "$STATE/update-available"; say "Up to date (${latest:0:7})."; [[ $FORCE -eq 1 ]] || exit 0
else
  say "Running ${current:0:7}; newest on $BRANCH is ${latest:0:7}${behind:+ ($behind newer changes)}."
  if [[ $NOTIFY -eq 1 ]]; then echo "${latest:0:7}" > "$STATE/update-available"; fi
fi
[[ $CHECK -eq 1 ]] && exit 10
[[ $EUID -eq 0 ]] || { say "Run rp-update as root."; exit 1; }

# 2. which bundle?
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
if [[ -n "${RP_UPDATE_URL:-}" ]]; then url="$RP_UPDATE_URL"   # an internal mirror (or a test server) instead of GitHub releases
elif [[ "$CHANNEL" == stable ]]; then
  url=$(curl_ "https://api.github.com/repos/$REPO/releases/latest" | grep -o '"browser_download_url": *"[^"]*reflectopatronus-update-[^"]*\.tar\.gz"' | head -1 | sed 's/.*"\(https[^"]*\)"/\1/')
  [[ -n "$url" ]] || { say "No stable release with an update bundle yet. Try --channel edge."; exit 1; }
else url="https://github.com/$REPO/releases/download/edge/reflectopatronus-update-edge.tar.gz"; fi
ask "Update to ${latest:0:7} now? The API restarts for a few seconds." || { say "Nothing changed."; exit 0; }
say "Downloading…"; curl_ -o "$TMP/u.tgz" "$url"; curl_ -o "$TMP/u.sha" "$url.sha256" || true
if [[ -s "$TMP/u.sha" ]]; then ( cd "$TMP" && echo "$(awk '{print $1}' u.sha)  u.tgz" | sha256sum -c - >/dev/null ) || { say "The download is damaged (checksum). Nothing changed."; exit 1; }; else say "(no checksum published; continuing)"; fi
mkdir "$TMP/new"; tar -xzf "$TMP/u.tgz" -C "$TMP/new"
new=$(cat "$TMP/new/COMMIT" 2>/dev/null || echo unknown)
[[ "$new" == "$current" && $FORCE -eq 0 ]] && { say "The published build is the one already running."; exit 0; }

HAS_API=0; [[ -d $OPT/api && -f /etc/systemd/system/reflectopatronus-api.service ]] && HAS_API=1
HAS_DB=0; command -v pg_lsclusters >/dev/null && [[ -n "$(pg_lsclusters -h 2>/dev/null)" ]] && HAS_DB=1
[[ $HAS_API -eq 1 || $HAS_DB -eq 1 ]] || { say "This container has neither the API nor a database cluster: nothing to update."; exit 1; }
mkdir -p "$BAK"; TS=$(date +%Y%m%d-%H%M%S)

# 3. backups
if [[ $HAS_DB -eq 1 ]]; then
  if [[ $BACKUPDB -eq 1 ]] || { [[ $ASSUME -eq 0 ]] && ask "Back up the database to $BAK first? (large databases take a while)"; }; then
    say "Dumping the database…"; runuser -u postgres -- pg_dump -Fc "${POSTGRES_DB:-reflectopatronus}" > "$BAK/db-$TS.dump" && say "  saved $BAK/db-$TS.dump"
  fi
fi
[[ $HAS_API -eq 1 ]] && tar -C "$OPT" -czf "$BAK/api-$TS.tgz" api && ls -1t "$BAK"/api-*.tgz | tail -n +4 | xargs -r rm -f

# 4. database first (so the new API never meets an old schema)
cp -a "$TMP/new/bundle/." "$OPT/bundle/"
if [[ $HAS_DB -eq 1 ]]; then
  [[ -f /root/rp.env ]] && { set -a; . /root/rp.env; set +a; }
  if [[ -z "${POSTGRES_PASSWORD:-}" ]]; then read -r -s -p "Database administrator password: " POSTGRES_PASSWORD; echo; export POSTGRES_PASSWORD; fi
  say "Applying database changes…"; bash "$OPT/bundle/scripts/migrate.sh" local
fi

# 5. the program
if [[ $HAS_API -eq 1 ]]; then
  say "Updating the API…"; systemctl stop reflectopatronus-api
  rm -rf "$OPT/api.prev"; mv "$OPT/api" "$OPT/api.prev"; mkdir "$OPT/api"; tar -xzf "$TMP/new/api-dist.tar.gz" -C "$OPT/api"; chown -R rpapi:rpapi "$OPT/api"
  cp -f "$TMP/new/api-dist.tar.gz" "$OPT/api-dist.tar.gz"
  systemctl start reflectopatronus-api
  ok=0; for _ in $(seq 1 45); do curl -fsS http://127.0.0.1:8080/health/live >/dev/null 2>&1 && { ok=1; break; }; sleep 1; done
  if [[ $ok -eq 0 ]]; then
    say "The new version did not start. Putting the old one back."; systemctl stop reflectopatronus-api || true
    rm -rf "$OPT/api"; mv "$OPT/api.prev" "$OPT/api"; systemctl start reflectopatronus-api
    say "Rolled back. Look at: journalctl -u reflectopatronus-api -n 50"; exit 1
  fi
  rm -rf "$OPT/api.prev"
fi
echo "$new" > "$OPT/COMMIT"; cat "$TMP/new/VERSION" > "$OPT/VERSION" 2>/dev/null || true; rm -f "$STATE/update-available"
for f in rp-setup:deploy/lxc/rp-setup.sh; do install -m 0755 "$OPT/bundle/${f#*:}" "/usr/local/sbin/${f%%:*}.new" && mv "/usr/local/sbin/${f%%:*}.new" "/usr/local/sbin/${f%%:*}"; done
install -m 0755 "$OPT/bundle/deploy/lxc/rp-update.sh" /usr/local/sbin/rp-update.new && mv /usr/local/sbin/rp-update.new /usr/local/sbin/rp-update
say "Updated to ${new:0:7}."
[[ $HAS_API -eq 1 && $HAS_DB -eq 0 ]] && say "Note: this container has no database. If the database lives in another container, update THAT one first (rp-update there)."
exit 0
