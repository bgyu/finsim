#!/usr/bin/env bash
# Start an already-built and already-prepared local cluster.
set -Eeuo pipefail
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$project_dir"
command -v podman >/dev/null || { printf 'cluster-up: podman is required\n' >&2; exit 127; }
scripts/validate-deployment.sh
# shellcheck disable=SC1091
source scripts/lib/runtime-env.sh
runtime_env_load .runtime/cluster.env
podman image exists "localhost/finsim-runtime:${FINSIM_IMAGE_TAG:-latest}" || {
    printf 'cluster-up: runtime image is missing; run scripts/build-images.sh\n' >&2; exit 78;
}
podman compose --env-file .runtime/cluster.env -f compose.yaml up --no-build --detach --remove-orphans
printf 'Waiting for five compute nodes and the FinSim API...\n'
idle=0
api=0
for _ in {1..120}; do
    idle=$(podman exec finsim-controller sinfo --noheader --states=idle --Node --format='%N' 2>/dev/null |
        sort -u | awk 'NF {n++} END {print n+0}')
    api=0
    curl --fail --silent --max-time 2 "http://127.0.0.1:${FINSIM_PROXY_PORT:-8088}/health/ready" >/dev/null && api=1 || true
    [[ $idle -ge 5 && $api -eq 1 ]] && break
    sleep 2
done
[[ $idle -ge 5 && $api -eq 1 ]] || {
    podman compose --env-file .runtime/cluster.env -f compose.yaml logs --tail=160 >&2 || true
    printf 'cluster-up: cluster did not become ready\n' >&2; exit 1;
}
printf 'FinSim cluster is ready. Submit with scripts/submit-simulation.sh --wait\n'
podman exec finsim-controller sinfo --Node --long
