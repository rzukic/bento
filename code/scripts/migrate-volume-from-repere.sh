#!/usr/bin/env bash
# Moves a packaged install from before the rename to Bento onto the new data volume.
#
# The compose files used to keep the backend's data (SQLite databases, config.json
# with the JWT secret, backups) in a volume named "repere-data"; it is now
# "bento-data". This copies <project>_repere-data into <project>_bento-data once.
# The old volume is left untouched, so nothing is lost if anything goes wrong.
#
#   code/scripts/migrate-volume-from-repere.sh            # compose project "code" (the default)
#   code/scripts/migrate-volume-from-repere.sh my-stack   # a stack started with -p my-stack
#
# Stop the stack first (docker compose ... down, without -v), run this, then start
# it again with the current compose files.
set -euo pipefail

PROJECT_NAME="${1:-code}"
OLD_VOLUME="${PROJECT_NAME}_repere-data"
NEW_VOLUME="${PROJECT_NAME}_bento-data"
HELPER_IMAGE="${HELPER_IMAGE:-alpine:3.20}"

if ! docker volume inspect "$OLD_VOLUME" >/dev/null 2>&1; then
  echo "No volume ${OLD_VOLUME}; nothing to move."
  exit 0
fi

if [[ -n "$(docker ps -q --filter "volume=${OLD_VOLUME}")" ]]; then
  echo "A running container still uses ${OLD_VOLUME}. Stop the stack first (docker compose ... down)." >&2
  exit 1
fi

if docker volume inspect "$NEW_VOLUME" >/dev/null 2>&1; then
  if [[ -n "$(docker run --rm -v "${NEW_VOLUME}:/to:ro" "$HELPER_IMAGE" ls -A /to)" ]]; then
    echo "${NEW_VOLUME} already holds data; leaving both volumes as they are." >&2
    exit 1
  fi
else
  # The compose labels let "docker compose up" adopt the volume as its own.
  docker volume create \
    --label "com.docker.compose.project=${PROJECT_NAME}" \
    --label "com.docker.compose.volume=bento-data" \
    "$NEW_VOLUME" >/dev/null
fi

docker run --rm \
  -v "${OLD_VOLUME}:/from:ro" \
  -v "${NEW_VOLUME}:/to" \
  "$HELPER_IMAGE" cp -a /from/. /to/

echo "Copied ${OLD_VOLUME} into ${NEW_VOLUME}."
echo "Start the stack again; once everything is there, remove the old volume with:"
echo "  docker volume rm ${OLD_VOLUME}"
