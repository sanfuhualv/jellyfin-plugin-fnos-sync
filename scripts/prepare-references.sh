#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:12.4}"
mkdir -p "$repo_root/.build/references" "$repo_root/.build/server"
container_id="$(docker create "$image")"
trap 'docker rm "$container_id" >/dev/null' EXIT
docker cp "$container_id:/jellyfin/." "$repo_root/.build/server/"
find "$repo_root/.build/server" -maxdepth 1 -type f -name '*.dll' -exec cp '{}' "$repo_root/.build/references/" \;
test -f "$repo_root/.build/references/MediaBrowser.Controller.dll"
printf 'Prepared build-only references from %s\n' "$image"
