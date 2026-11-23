#!/usr/bin/env bash
# ReflectoPatronus updater (Docker). Talks to the API only through files in $UPDATE_DIR:
#   request.json  written by the API   {"action":"stage"|"apply","sha":...,"backup":true}
#   status.json   written here         state, step, percent, message, log, staged{...}   (shown on /update-progress)
#   heartbeat     touched here every few seconds (the console shows "automatic updates available" while it is fresh)
set -uo pipefail
P="${RP_PROJECT_DIR:-}"; D="${UPDATE_DIR:-/data/update}"; BRANCH="${RP_BRANCH:-main}"
ST="$D/status.json"; REQ="$D/request.json"
mkdir -p "$D"; chmod 0777 "$D" 2>/dev/null || true
if [[ "$P" != /* || ! -d "$P/.git" || ! -f "$P/.env" ]]; then
  echo "updater: RP_PROJECT_DIR ('$P') is not a git checkout with a .env, so one-click updates are off. Run scripts/setup.sh from the project folder."
  sleep infinity
fi
cd "$P"
git config --global --add safe.directory '*'
OWNER="$(stat -c %u:%g "$P")"
FILES=(-f docker-compose.yml); grep -qs '^RP_IMAGES=1' .env && FILES+=(-f docker-compose.ghcr.yml)
dc() { docker compose --project-directory "$P" --env-file "$P/.env" "${FILES[@]}" "$@"; }
now() { date +%s; }
[[ -s "$ST" ]] || echo '{}' > "$ST"; chmod 0644 "$ST"

# S state step percent message  -> status.json (atomic), appends to the log
S() { jq -c --arg st "$1" --arg step "$2" --argjson pct "$3" --arg msg "$4" --argjson now "$(now)" \
  '.state=$st|.step=$step|.percent=$pct|.message=$msg|.updated=$now|.log=((.log//[])+[[$now,$msg]])[-80:]' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"; chmod 0644 "$ST"; echo "[$1] $4"; }
newjob() { jq -c --arg v "$1" --arg from "$2" --argjson now "$(now)" '{staged:.staged, version:$v, from:$from, started:$now, updated:$now, log:[]}' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"; }
healthy() { local c; c="$(dc ps -q "$1" 2>/dev/null | head -1)"; [[ -n "$c" && "$(docker inspect -f '{{.State.Health.Status}}' "$c" 2>/dev/null)" == healthy ]]; }
wait_healthy() { local i; for ((i = 0; i < $2; i++)); do healthy "$1" && return 0; sleep 2; done; return 1; }
running_sha() { docker exec "$(dc ps -q api | head -1)" printenv RP_BUILD_SHA 2>/dev/null || true; }

# ---- stage: download the new source now so installing later is one click ----
do_stage() {
  if ! git fetch -q origin "$BRANCH" 2>/dev/null; then echo "updater: could not fetch (offline?)"; return 1; fi
  local there here msg
  there="$(git rev-parse "origin/$BRANCH")"; here="$(git rev-parse HEAD)"; msg="$(git log -1 --format=%s "origin/$BRANCH")"
  local commits; commits="$(git --no-pager log --format='%h %s' --no-decorate "$here..$there" 2>/dev/null | head -30 | jq -R . | jq -sc .)"
  jq -c --arg sha "$there" --arg msg "$msg" --argjson commits "${commits:-[]}" --argjson now "$(now)" \
    '.staged={sha:$sha,short:($sha[0:7]),message:$msg,commits:$commits,at:$now} | if (.state|IN("failed","rolled_back","done",null)) then .state="staged" else . end' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"; chmod 0644 "$ST"
  echo "updater: staged ${there:0:7}"
}

# ---- apply ----
FAILED=""
fail() { FAILED="$1"; return 1; }
rollback() {
  local prev="$1" img; S switching switching 90 "Something went wrong: $FAILED. Putting the previous version back."
  git merge --abort 2>/dev/null; git reset -q --hard "$prev"
  img="$(dc config --format json 2>/dev/null | jq -r '.services.api.image // empty')"; [[ -n "$img" ]] || img="reflectopatronus-api"
  docker tag rp-rollback:api "$img:latest" 2>/dev/null
  if dc up -d --no-build api >/dev/null 2>&1 && wait_healthy api 60; then
    local bk=""; [[ -n "${BACKUP_FILE:-}" ]] && bk=" Your data is untouched; a database backup from just before the update is at $BACKUP_FILE."
    jq -c --arg b "${BACKUP_FILE:-}" '.backupFile=$b' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"
    S rolled_back "$STEP" 100 "The update did not work: $FAILED. ReflectoPatronus went back to the previous version.$bk"
  else
    S failed "$STEP" 100 "The update failed: $FAILED. The previous version did not restart on its own. On the server run: docker compose logs api"
  fi
}
do_apply() {
  local want="$1" backup="$2" prev target from img
  STEP=checking; BACKUP_FILE=""
  from="$(git rev-parse HEAD)"
  newjob "$want" "$(running_sha)"
  S checking checking 3 "Checking that this server is ready"
  git fetch -q origin "$BRANCH" || { S failed checking 100 "Could not reach GitHub to download the update."; return; }
  target="$(git rev-parse "origin/$BRANCH")"; prev="$from"
  jq -c --arg v "$target" '.version=$v' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"
  if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then S failed checking 100 "The program folder has local changes, so it cannot be updated safely. Nothing was changed."; return; fi
  if ! git merge-base --is-ancestor HEAD "$target"; then S failed checking 100 "The program folder is not on the main line of $BRANCH. Nothing was changed."; return; fi
  local free; free="$(df -Pk "$P" | awk 'NR==2{print int($4/1024)}')"
  if [[ "${free:-0}" -lt 2048 ]]; then S failed checking 100 "Not enough free disk space (${free} MB; at least 2048 MB are needed). Nothing was changed."; return; fi
  healthy db || { S failed checking 100 "The database is not healthy right now, so the update was not started. Nothing was changed."; return; }
  img="$(dc ps -q api | head -1 | xargs -r docker inspect -f '{{.Image}}' 2>/dev/null)"; [[ -n "$img" ]] && docker tag "$img" rp-rollback:api

  if [[ "$backup" == true ]]; then
    STEP=backup; S backup backup 8 "Backing up the database (large databases take a few minutes)"
    set -a; . ./.env; set +a; mkdir -p backups; BACKUP_FILE="backups/pre-update-$(date +%Y%m%d-%H%M%S).dump"
    if ! dc exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db pg_dump -Fc -U "${POSTGRES_USER:-reflectopatronus_admin}" "${POSTGRES_DB:-reflectopatronus}" > "$BACKUP_FILE" 2>/dev/null; then
      rm -f "$BACKUP_FILE"; S failed backup 100 "The database backup failed, so the update was not started. Nothing was changed."; return
    fi
    S backup backup 20 "Backup saved ($(du -h "$BACKUP_FILE" | cut -f1))"; ls -1t backups/pre-update-*.dump 2>/dev/null | tail -n +4 | xargs -r rm -f
  fi

  STEP=downloading; S downloading downloading 24 "Applying the downloaded changes"
  if [[ "$(git rev-parse HEAD)" != "$target" ]]; then git merge -q --ff-only "$target" || { fail "could not apply the downloaded changes"; rollback "$prev"; return; }; fi
  export GIT_SHA="$target"
  local files; files="$(git diff --name-only "$prev" "$target" 2>/dev/null)"

  STEP=building
  if grep -qs '^RP_IMAGES=1' .env; then
    S building building 35 "Downloading the published program images"
    dc pull api >/dev/null 2>&1 || { fail "could not download the program images"; rollback "$prev"; return; }
  else
    S building building 35 "Building the web console"
    ./scripts/build-web.sh >/dev/null 2>&1 || { fail "the web console did not build"; rollback "$prev"; return; }
    S building building 55 "Building the program"
    dc build api >/dev/null 2>&1 || { fail "the program did not build"; rollback "$prev"; return; }
  fi

  STEP=migrating; S migrating migrating 70 "Updating the database (only the new changes are applied)"
  dc up -d db >/dev/null 2>&1; wait_healthy db 60 || { fail "the database did not come back"; rollback "$prev"; return; }
  if ! ./scripts/migrate.sh docker >"$D/migrate.log" 2>&1; then fail "a database change did not apply (see the Details log)"; tail -5 "$D/migrate.log" | while read -r l; do S migrating migrating 70 "$l"; done; rollback "$prev"; return; fi

  STEP=switching; S switching switching 82 "Restarting ReflectoPatronus on the new version"
  local svcs; svcs="$(dc config --services 2>/dev/null | grep -vx -e db -e updater | tr '\n' ' ')"
  # shellcheck disable=SC2086
  dc up -d --no-build $svcs >/dev/null 2>&1 || { fail "the new version could not be started"; rollback "$prev"; return; }

  STEP=verifying; S verifying verifying 92 "Checking that the new version answers"
  if ! wait_healthy api 60; then fail "the new version did not answer within two minutes"; rollback "$prev"; return; fi
  local sha; sha="$(running_sha)"
  if [[ -n "$sha" && "$sha" != unknown && "$sha" != "$target" ]]; then fail "the running program is not the one that was built"; rollback "$prev"; return; fi
  chown -R "$OWNER" .git src/ReflectoPatronus.Api/wwwroot backups 2>/dev/null
  docker image prune -f >/dev/null 2>&1
  jq -c '.staged=null' "$ST" > "$ST.tmp" && mv "$ST.tmp" "$ST"
  S done done 100 "Updated to ${target:0:7}."
  if grep -qE '^(docker/updater/|docker-compose\.yml)' <<<"$files"; then   # the updater itself changed: replace it last
    ( sleep 5; dc up -d --build updater >/dev/null 2>&1 ) >/dev/null 2>&1 &
  fi
}

echo "updater: watching $D for requests (project $P, branch $BRANCH)"
last_stage=0
while true; do
  touch "$D/heartbeat"; chmod 0644 "$D/heartbeat" 2>/dev/null
  if [[ -f "$REQ" ]]; then
    mv "$REQ" "$REQ.taken" 2>/dev/null
    action="$(jq -r '.action // empty' "$REQ.taken" 2>/dev/null)"
    case "$action" in
      apply) do_apply "$(jq -r '.sha // empty' "$REQ.taken")" "$(jq -r 'if .backup == false then "false" else "true" end' "$REQ.taken")" ;;
      stage) do_stage; last_stage="$(now)" ;;
    esac
    rm -f "$REQ.taken"
  elif (( $(now) - last_stage > 86400 )); then   # also look once a day, so the update is already downloaded when someone opens the console
    do_stage; last_stage="$(now)"
  fi
  sleep 3
done
