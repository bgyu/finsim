#!/usr/bin/env bash
# Build the only two published image targets.
set -Eeuo pipefail
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
tag=${FINSIM_IMAGE_TAG:-latest}
case $tag in *[!A-Za-z0-9_.-]*|'') printf 'build-images: invalid tag\n' >&2; exit 64;; esac
cd "$project_dir"
declare -a args=(--format docker --file Containerfile)
podman build "${args[@]}" --target finsim-runtime --tag "localhost/finsim-runtime:$tag" .
podman build "${args[@]}" --target finsim-dev --tag "localhost/finsim-dev:$tag" .
