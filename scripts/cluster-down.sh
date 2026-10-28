#!/usr/bin/env bash
set -Eeuo pipefail

project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$project_dir"

if [[ ! -f .runtime/cluster.env ]]; then
    printf 'cluster-down: local runtime has not been initialized\n'
    exit 0
fi

podman compose --env-file .runtime/cluster.env -f compose.yaml \
    down --remove-orphans "$@"
