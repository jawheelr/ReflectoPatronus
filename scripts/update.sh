#!/usr/bin/env bash
# Update a Docker install to the newest commit on main. Asks before changing anything.
#   scripts/update.sh                 build from the updated source (needs Node for the web console)
#   scripts/update.sh --images        use the images published to ghcr.io instead of building (docker-compose.ghcr.yml)
#   scripts/update.sh --yes           no questions        --backup   dump the database first (to ./backups)      --branch NAME
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"; cd "$ROOT"
ASSUME=0; IMAGES=0; BACKUP=0; BRANCH=main
while [[ $# -gt 0 ]]; do case "$1" in
  --yes|-y) ASSUME=1 ;; --images) IMAGES=1 ;; --backup) BACKUP=1 ;; --branch) BRANCH="${2:?branch}"; shift ;;
  -h|--help) sed -n 2,6p "$0"; exit 0 ;; *) echo "unknown option $1" >&2; exit 2 ;; esac; shift; done
ask() { [[ $ASSUME -eq 1 ]] && return 0; local a; read -r -p "$1 [y/N] " a; [[ "$a" =~ ^[Yy] ]]; }
[[ -f .env ]] || { echo "No .env here. This does not look like a set-up install (run scripts/setup.sh first)."; exit 1; }
[[ -d .git ]] || { echo "This folder is not a git checkout, so there is nothing to pull. Clone the repository, or copy .env and certs/ into a clone."; exit 1; }
# Make sure one-click updates (the 'updater' service) are switched on for next time.
grep -q '^RP_PROJECT_DIR=' .env || { printf '\nRP_PROJECT_DIR="%s"\n' "$ROOT" >> .env; }
[[ $IMAGES -eq 1 ]] && ! grep -q '^RP_IMAGES=' .env && printf 'RP_IMAGES=1\n' >> .env
COMPOSE=(docker compose --env-file .env); [[ $IMAGES -eq 1 ]] && COMPOSE=(docker compose --env-file .env -f docker-compose.yml -f docker-compose.ghcr.yml)

git fetch -q origin "$BRANCH"
here="$(git rev-parse HEAD)"; there="$(git rev-parse "origin/$BRANCH")"
if [[ "$here" == "$there" ]]; then echo "Already on the newest $BRANCH (${here:0:7})."; [[ -n "${FORCE:-}" ]] || exit 0; fi
echo "What is new on $BRANCH:"; git --no-pager log --oneline --no-decorate "$here..$there" | head -30
if ! git diff --quiet || ! git diff --cached --quiet; then echo; echo "You have local changes in this folder; the update only works if they do not clash (git pull --ff-only)."; fi
ask "Update to ${there:0:7}? The program restarts for a few seconds." || { echo "Nothing changed."; exit 0; }

if [[ $BACKUP -eq 1 || $ASSUME -eq 0 ]] && { [[ $BACKUP -eq 1 ]] || ask "Back up the database to ./backups first? (large databases take a while)"; }; then
  set -a; . ./.env; set +a; mkdir -p backups; f="backups/pre-update-$(date +%Y%m%d-%H%M%S).dump"
  "${COMPOSE[@]}" exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db pg_dump -Fc -U "${POSTGRES_USER:-reflectopatronus_admin}" "${POSTGRES_DB:-reflectopatronus}" > "$f" && echo "saved $f"
fi

git pull -q --ff-only origin "$BRANCH"
export GIT_SHA="$(git rev-parse HEAD)"
if [[ $IMAGES -eq 1 ]]; then export RP_TAG="${RP_TAG:-$BRANCH}"; "${COMPOSE[@]}" pull
else
  command -v node >/dev/null || { echo "Node.js is needed to build the web console. Install it, or use: scripts/update.sh --images"; exit 1; }
  ./scripts/build-web.sh
fi
echo "Applying database changes…"
"${COMPOSE[@]}" up -d db
for _ in $(seq 1 60); do [[ "$(docker inspect --format '{{.State.Health.Status}}' "$("${COMPOSE[@]}" ps -q db)" 2>/dev/null)" == healthy ]] && break; sleep 2; done
./scripts/migrate.sh docker
echo "Restarting the program…"
if [[ $IMAGES -eq 1 ]]; then "${COMPOSE[@]}" up -d; else "${COMPOSE[@]}" up -d --build; fi   # includes the updater service
api="$("${COMPOSE[@]}" ps -q api)"
for _ in $(seq 1 60); do s="$(docker inspect --format '{{.State.Health.Status}}' "$api" 2>/dev/null || true)"; [[ "$s" == healthy ]] && { echo "Updated to ${GIT_SHA:0:7} and healthy."; exit 0; }; sleep 2; done
echo "The program did not report healthy within two minutes. Check: docker compose logs api"; exit 1
