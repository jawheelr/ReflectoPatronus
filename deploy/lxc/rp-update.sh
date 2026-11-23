#!/usr/bin/env bash
# rp-update: update THIS ReflectoPatronus container to the newest build from GitHub.
#   rp-update                interactive: shows what is new, asks, backs up, applies, checks it came back
#   rp-update --check        only look (exit 10 if an update is available); --check --notify also leaves a note for the login message
#   rp-update --stage        look, and if there is a newer build download + verify it now (the weekly timer does this), so the
#                            console's "Update now" button only has to install it
#   rp-update --from-request handle a request written by the web console (stage or apply); run by systemd, shows progress on /update-progress
#   rp-update --yes          no questions (for scripts)          rp-update --backup-db   also dump the database first
#   rp-update --channel edge|stable   edge (default) = every commit on main; stable = the latest vX.Y.Z release
#   rp-update --force        re-apply even when already on that commit
# Containers that hold only the database, or only the API, are updated separately: update the DATABASE container first.
set -euo pipefail
REPO="${RP_REPO:-jawheelr/ReflectoPatronus}"; BRANCH="${RP_BRANCH:-main}"; CHANNEL=edge; ASSUME=0; CHECK=0; NOTIFY=0; FORCE=0; BACKUPDB=0; STAGE=0; JOB=0
OPT=/opt/reflectopatronus; STATE=/var/lib/reflectopatronus; BAK=/var/backups/reflectopatronus; UPD="$STATE/update"; STAGED="$UPD/staged"
while [[ $# -gt 0 ]]; do case "$1" in
  --check) CHECK=1 ;; --notify) NOTIFY=1 ;; --yes|-y) ASSUME=1 ;; --force) FORCE=1 ;; --backup-db) BACKUPDB=1 ;; --stage) STAGE=1; NOTIFY=1 ;;
  --from-request) JOB=1; ASSUME=1 ;;
  --channel) CHANNEL="${2:?edge or stable}"; shift ;; -h|--help) sed -n 2,13p "$0"; exit 0 ;;
  *) echo "unknown option $1" >&2; exit 2 ;; esac; shift; done
say() { printf '%s\n' "$*"; }
ask() { [[ $ASSUME -eq 1 ]] && return 0; local a; read -r -p "$1 [y/N] " a; [[ "$a" =~ ^[Yy] ]]; }
curl_() { curl -fsSL --max-time "${CURL_MAX:-30}" -A "ReflectoPatronus-update-check" "$@"; }
clear_staged() { rm -f "${STAGED:?}"/* 2>/dev/null || true; }
current=$(cat "$OPT/COMMIT" 2>/dev/null || echo unknown)
ST="$UPD/status.json"

# ---- progress for the web console (/update-progress): $UPD/status.json, same format as the Docker updater ----
HAVE_JQ=0; command -v jq >/dev/null 2>&1 && HAVE_JQ=1
if [[ $JOB -eq 1 || $STAGE -eq 1 ]]; then
  mkdir -p "$UPD"; chmod 0777 "$UPD" 2>/dev/null || true; echo lxc > "$UPD/updater-lxc"; chmod 0644 "$UPD/updater-lxc"
  if [[ $HAVE_JQ -eq 0 ]] && command -v apt-get >/dev/null; then apt-get install -y --no-install-recommends jq >/dev/null 2>&1 && HAVE_JQ=1 || true; fi
  [[ -s "$ST" ]] || echo '{}' > "$ST"
fi
STEP=checking
S() { if [[ $JOB -ne 1 || $HAVE_JQ -ne 1 ]]; then [[ $JOB -eq 1 ]] || say "$4"; return 0; fi
  jq -c --arg st "$1" --arg step "$2" --argjson pct "$3" --arg msg "$4" --argjson now "$(date +%s)" \
    '.state=$st|.step=$step|.percent=$pct|.message=$msg|.updated=$now|.log=((.log//[])+[[$now,$msg]])[-80:]' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"; chmod 0644 "$ST"; STEP="$2"; say "[$1] $4"; }
JOBFAIL() { if [[ $JOB -eq 1 ]]; then S failed "$STEP" 100 "$1"; else say "$1"; fi; }
job_state() { jq -r .state "$ST" 2>/dev/null || true; }
set_staged() {  # sha message  (stage info shown in the console)
  [[ $HAVE_JQ -eq 1 && -d "$UPD" ]] || return 0
  jq -c --arg sha "$1" --arg msg "$2" --argjson now "$(date +%s)" '.staged={sha:$sha,short:($sha[0:7]),message:$msg,commits:[],at:$now}' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"; chmod 0644 "$ST"; }

# a request from the console
if [[ $JOB -eq 1 ]]; then
  [[ $EUID -eq 0 ]] || { say "must run as root"; exit 1; }
  [[ -f "$UPD/request.json" ]] || exit 0
  mv "$UPD/request.json" "$UPD/request.taken"
  ACTION=$(jq -r '.action // empty' "$UPD/request.taken"); [[ "$(jq -r '.backup // true' "$UPD/request.taken")" == false ]] || BACKUPDB=1; rm -f "$UPD/request.taken"
  case "$ACTION" in stage) STAGE=1; JOB=0 ;; apply) FORCE=0 ;; *) exit 0 ;; esac
  if [[ "$ACTION" == apply ]]; then jq -c --argjson now "$(date +%s)" --arg from "$current" '{staged:.staged, from:$from, started:$now, updated:$now, log:[]}' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"; fi
fi
if grep -qs '^Updates__Enabled=false' /etc/reflectopatronus/api.env 2>/dev/null && [[ $CHECK -eq 1 ]]; then exit 0; fi
TMP=$(mktemp -d)
on_exit() { local rc=$?; rm -rf "${TMP:?}"
  if [[ $rc -ne 0 && $JOB -eq 1 ]]; then case "$(job_state)" in failed|rolled_back|done) ;; *) S failed "$STEP" 100 "The update stopped unexpectedly (see: journalctl -u rp-update-apply)." ;; esac; fi; }
trap on_exit EXIT

# 1. what is the newest commit, and are we behind?
S checking checking 3 "Checking for the newest version"
latest=$(curl_ -H 'Accept: application/vnd.github+json' "https://api.github.com/repos/$REPO/commits/$BRANCH" | grep -o '"sha": *"[0-9a-f]\{40\}"' | head -1 | grep -o '[0-9a-f]\{40\}') || { JOBFAIL "Could not reach GitHub."; exit 1; }
[[ -n "$latest" ]] || { JOBFAIL "GitHub did not answer as expected."; exit 1; }
behind=""; if [[ "$current" != unknown && "$current" != "$latest" ]]; then
  behind=$(curl_ "https://api.github.com/repos/$REPO/compare/$current...$latest" 2>/dev/null | grep -o '"ahead_by": *[0-9]*' | head -1 | grep -o '[0-9]*$' || true); fi
mkdir -p "$STATE"
if [[ "$current" == "$latest" || "${current:0:7}" == "${latest:0:7}" ]]; then
  rm -f "$STATE/update-available"; say "Up to date (${latest:0:7})."
  if [[ $JOB -eq 1 ]]; then S done done 100 "Already up to date (${latest:0:7})."; fi
  [[ $FORCE -eq 1 ]] || exit 0
else
  say "Running ${current:0:7}; newest on $BRANCH is ${latest:0:7}${behind:+ ($behind newer changes)}."
  if [[ $NOTIFY -eq 1 ]]; then echo "${latest:0:7}" > "$STATE/update-available"; fi
fi
[[ $CHECK -eq 1 ]] && exit 10
[[ $EUID -eq 0 ]] || { say "Run rp-update as root."; exit 1; }

# 2. which bundle, and download it (into the staging folder when staging, so the console's button only has to install)
if [[ -n "${RP_UPDATE_URL:-}" ]]; then url="$RP_UPDATE_URL"   # an internal mirror (or a test server) instead of GitHub releases
elif [[ "$CHANNEL" == stable ]]; then
  url=$(curl_ "https://api.github.com/repos/$REPO/releases/latest" | grep -o '"browser_download_url": *"[^"]*reflectopatronus-update-[^"]*\.tar\.gz"' | head -1 | sed 's/.*"\(https[^"]*\)"/\1/')
  [[ -n "$url" ]] || { JOBFAIL "No stable release with an update bundle yet. Try --channel edge."; exit 1; }
else url="https://github.com/$REPO/releases/download/edge/reflectopatronus-update-edge.tar.gz"; fi
fetch_bundle() {   # dest.tgz -> verified download (return 2 = checksum mismatch)
  CURL_MAX=900 curl_ -o "$1" "$url" || return 1; curl_ -o "$1.sha" "$url.sha256" 2>/dev/null || true
  if [[ -s "$1.sha" ]]; then ( cd "$(dirname "$1")" && echo "$(awk '{print $1}' "$1.sha")  $(basename "$1")" | sha256sum -c - >/dev/null ) || return 2; else say "(no checksum published; continuing)"; fi
}
if [[ $STAGE -eq 1 ]]; then
  mkdir -p "$STAGED"; clear_staged
  say "Downloading the update…"
  fetch_bundle "$STAGED/u.tgz" || { say "The download failed or is damaged (checksum). Nothing was staged."; clear_staged; exit 1; }
  mkdir "$TMP/peek"; tar -xzf "$STAGED/u.tgz" -C "$TMP/peek" COMMIT 2>/dev/null || true
  cat "$TMP/peek/COMMIT" > "$STAGED/COMMIT" 2>/dev/null || echo "$latest" > "$STAGED/COMMIT"
  set_staged "$latest" "$(curl_ "https://api.github.com/repos/$REPO/commits/$BRANCH" 2>/dev/null | grep -o '"message": *"[^"]*"' | head -1 | sed 's/"message": *"//;s/"$//;s/\\n.*//' || true)"
  say "Downloaded and verified. Install it from the console (Update now) or with: rp-update"
  exit 0
fi
ask "Update to ${latest:0:7} now? The API restarts for a few seconds." || { say "Nothing changed."; exit 0; }
S downloading downloading 12 "Downloading the new version"
if [[ -s "$STAGED/u.tgz" && "$(cat "$STAGED/COMMIT" 2>/dev/null)" != "$current" ]]; then   # already downloaded ahead of time: re-check it instead of downloading again
  cp "$STAGED/u.tgz" "$TMP/u.tgz"
  if [[ -s "$STAGED/u.tgz.sha" ]] && ! ( cd "$TMP" && echo "$(awk '{print $1}' "$STAGED/u.tgz.sha")  u.tgz" | sha256sum -c - >/dev/null ); then
    clear_staged; fetch_bundle "$TMP/u.tgz" || { JOBFAIL "The download is damaged (checksum). Nothing changed."; exit 1; }
  fi
else fetch_bundle "$TMP/u.tgz" || { JOBFAIL "The download failed or is damaged (checksum). Nothing changed."; exit 1; }; fi
S building building 28 "Unpacking the new version"
mkdir "$TMP/new"; tar -xzf "$TMP/u.tgz" -C "$TMP/new"
new=$(cat "$TMP/new/COMMIT" 2>/dev/null || echo unknown)
if [[ "$new" == "$current" && $FORCE -eq 0 ]]; then
  if [[ $JOB -eq 1 ]]; then S failed checking 100 "The newest build is not published yet (GitHub is still preparing it). Nothing was changed; try again in a few minutes."; else say "The published build is the one already running."; fi
  exit 0
fi
if [[ $JOB -eq 1 ]]; then jq -c --arg v "$new" '.version=$v' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"; fi

HAS_API=0; [[ -d $OPT/api && -f /etc/systemd/system/reflectopatronus-api.service ]] && HAS_API=1
HAS_DB=0; command -v pg_lsclusters >/dev/null && [[ -n "$(pg_lsclusters -h 2>/dev/null)" ]] && HAS_DB=1
[[ $HAS_API -eq 1 || $HAS_DB -eq 1 ]] || { JOBFAIL "This container has neither the API nor a database cluster: nothing to update."; exit 1; }
mkdir -p "$BAK"; TS=$(date +%Y%m%d-%H%M%S); DUMP=""

# 3. backups
if [[ $HAS_DB -eq 1 ]]; then
  if [[ $BACKUPDB -eq 1 ]] || { [[ $ASSUME -eq 0 ]] && ask "Back up the database to $BAK first? (large databases take a while)"; }; then
    S backup backup 35 "Backing up the database (large databases take a few minutes)"
    DUMP="$BAK/db-$TS.dump"
    runuser -u postgres -- pg_dump -Fc "${POSTGRES_DB:-reflectopatronus}" > "$DUMP" || { rm -f "$DUMP"; JOBFAIL "The database backup failed, so nothing was changed."; exit 1; }
    say "  saved $DUMP"; ls -1t "$BAK"/db-*.dump | tail -n +4 | xargs -r rm -f
    if [[ $JOB -eq 1 ]]; then jq -c --arg b "$DUMP" '.backupFile=$b' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"; fi
  fi
fi
if [[ $HAS_API -eq 1 ]]; then tar -C "$OPT" -czf "$BAK/api-$TS.tgz" api && ls -1t "$BAK"/api-*.tgz | tail -n +4 | xargs -r rm -f; fi

# 4. database first (so the new API never meets an old schema)
cp -a "$TMP/new/bundle/." "$OPT/bundle/"
if [[ $HAS_DB -eq 1 ]]; then
  [[ -f /root/rp.env ]] && { set -a; . /root/rp.env; set +a; }
  if [[ -z "${POSTGRES_PASSWORD:-}" ]]; then
    if [[ $JOB -eq 1 ]]; then JOBFAIL "The database administrator password is not stored on this container (/root/rp.env), so the console cannot apply database changes. Run rp-update in the container's console instead."; exit 1; fi
    read -r -s -p "Database administrator password: " POSTGRES_PASSWORD; echo; export POSTGRES_PASSWORD
  fi
  S migrating migrating 55 "Updating the database (only the new changes are applied)"; bash "$OPT/bundle/scripts/migrate.sh" local || { JOBFAIL "A database change did not apply. The old version keeps running."; exit 1; }
fi

# the pieces that let the console trigger updates (idempotent; also upgrades containers installed before this existed)
install_updater_bits() {
  mkdir -p "$UPD"; chmod 0777 "$UPD"; echo lxc > "$UPD/updater-lxc"; chmod 0644 "$UPD/updater-lxc"
  local b="$OPT/bundle/deploy/lxc/template" u
  for u in rp-update-request.path rp-update-apply.service rp-update-check.service rp-update-check.timer; do [[ -f "$b/$u" ]] && install -m 0644 "$b/$u" "/etc/systemd/system/$u"; done
  if [[ -f /etc/systemd/system/reflectopatronus-api.service ]]; then
    mkdir -p /etc/systemd/system/reflectopatronus-api.service.d
    printf '[Service]\nReadWritePaths=%s\n' "$UPD" > /etc/systemd/system/reflectopatronus-api.service.d/updates.conf
    grep -qs '^Updates__Directory=' /etc/reflectopatronus/api.env || echo "Updates__Directory=$UPD" >> /etc/reflectopatronus/api.env
    chown -R rpapi:rpapi "$UPD" 2>/dev/null || true; chmod 0777 "$UPD"
  fi
  systemctl daemon-reload; systemctl enable --now rp-update-request.path >/dev/null 2>&1 || true; systemctl enable rp-update-check.timer >/dev/null 2>&1 || true
  command -v jq >/dev/null || { command -v apt-get >/dev/null && apt-get install -y --no-install-recommends jq >/dev/null 2>&1 || true; }
}

# 5. the program
if [[ $HAS_API -eq 1 ]]; then
  S switching switching 75 "Restarting ReflectoPatronus on the new version"; systemctl stop reflectopatronus-api
  rm -rf "${OPT:?}/api.prev"; mv "$OPT/api" "$OPT/api.prev"; mkdir "$OPT/api"; tar -xzf "$TMP/new/api-dist.tar.gz" -C "$OPT/api"; chown -R rpapi:rpapi "$OPT/api"
  cp -f "$TMP/new/api-dist.tar.gz" "$OPT/api-dist.tar.gz"
  install_updater_bits
  systemctl start reflectopatronus-api
  S verifying verifying 90 "Checking that the new version answers"
  ok=0; for _ in $(seq 1 60); do curl -fsS http://127.0.0.1:8080/health/live >/dev/null 2>&1 && { ok=1; break; }; sleep 1; done
  if [[ $ok -eq 0 ]]; then
    say "The new version did not start. Putting the old one back."; systemctl stop reflectopatronus-api || true
    rm -rf "${OPT:?}/api"; mv "$OPT/api.prev" "$OPT/api"; systemctl start reflectopatronus-api
    if [[ $JOB -eq 1 ]]; then S rolled_back verifying 100 "The new version did not start, so ReflectoPatronus went back to the previous version. Your data is untouched${DUMP:+; a backup from just before the update is at $DUMP}."; else say "Rolled back. Look at: journalctl -u reflectopatronus-api -n 50"; fi
    exit 1
  fi
  rm -rf "${OPT:?}/api.prev"
else install_updater_bits
fi
echo "$new" > "$OPT/COMMIT"; cat "$TMP/new/VERSION" > "$OPT/VERSION" 2>/dev/null || true; rm -f "$STATE/update-available"; clear_staged
if [[ $HAVE_JQ -eq 1 && -s "$ST" ]]; then jq -c '.staged=null' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"; chmod 0644 "$ST"; fi
for f in rp-setup:deploy/lxc/rp-setup.sh; do install -m 0755 "$OPT/bundle/${f#*:}" "/usr/local/sbin/${f%%:*}.new" && mv "/usr/local/sbin/${f%%:*}.new" "/usr/local/sbin/${f%%:*}"; done
install -m 0755 "$OPT/bundle/deploy/lxc/rp-update.sh" /usr/local/sbin/rp-update.new && mv /usr/local/sbin/rp-update.new /usr/local/sbin/rp-update
S done done 100 "Updated to ${new:0:7}."
say "Updated to ${new:0:7}."
if [[ $HAS_API -eq 1 && $HAS_DB -eq 0 ]]; then say "Note: this container has no database. If the database lives in another container, update THAT one first (rp-update there)."; fi
exit 0
