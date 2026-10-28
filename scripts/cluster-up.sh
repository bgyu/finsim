#!/usr/bin/env bash
# Build and start the complete local FinSim/Slurm integration topology.
set -Eeuo pipefail

project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
runtime_dir="$project_dir/.runtime"
compose_file="$project_dir/compose.yaml"

command -v podman >/dev/null 2>&1 || {
    printf 'cluster-up: podman is required\n' >&2
    exit 127
}

slurm_version=${SLURM_VERSION:-26.05.5}
slurm_sha256=${SLURM_SHA256:-4680cafc82b0689865c885eb703441e7ebf764896c40fd03a50f2fcb60664f68}
munge_version=${MUNGE_VERSION:-0.5.18}
munge_sha256=${MUNGE_SHA256:-39c3ec6ef5604bfa206e8aa10fc05d5119040f6de4a554bc0fb98ca1aed838dc}
image_tag=${FINSIM_IMAGE_TAG:-latest}

cd "$project_dir"
declare -a build_args=(
    --format docker
    --build-arg "SLURM_VERSION=$slurm_version"
    --build-arg "SLURM_SHA256=$slurm_sha256"
    --build-arg "MUNGE_VERSION=$munge_version"
    --build-arg "MUNGE_SHA256=$munge_sha256"
    --file Containerfile
)
podman build "${build_args[@]}" --target slurm-runtime \
    --tag "localhost/finsim-slurm:$image_tag" .
podman build "${build_args[@]}" --target finsim-app \
    --tag "localhost/finsim-app:$image_tag" .
podman build "${build_args[@]}" --target finsim-sim \
    --tag "localhost/finsim-sim:$image_tag" .

FINSIM_IMAGE_TAG=$image_tag "$project_dir/scripts/bootstrap-dev-secrets.sh"

podman compose --env-file "$runtime_dir/cluster.env" -f "$compose_file" \
    up --no-build --detach --remove-orphans

# A green proxy listener is insufficient: it can start while a dependency is
# still failing or restarting.  Treat each application boundary as part of the
# cluster readiness contract before accepting submissions.
printf 'Waiting for FinSimCache readiness...\n'
cache_ready=0
for _ in {1..90}; do
    cache_health=$(podman inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' \
        finsim_cache_1 2>/dev/null || true)
    if [[ $cache_health == healthy ]]; then
        cache_ready=1
        break
    fi
    sleep 2
done
if [[ $cache_ready != 1 ]]; then
    printf 'cluster-up: FinSimCache did not become ready\n' >&2
    podman compose --env-file "$runtime_dir/cluster.env" -f "$compose_file" \
        logs --tail=160 cache >&2 || true
    exit 1
fi

printf 'Waiting for five FinSim compute nodes...\n'
idle_nodes=0
for _ in {1..120}; do
    node_list=$(podman exec finsim-controller \
        sinfo --noheader --states=idle --Node --format='%N' 2>/dev/null || true)
    idle_nodes=$(printf '%s\n' "$node_list" | sort -u | \
        awk 'NF { count++ } END { print count + 0 }')
    [[ $idle_nodes -ge 5 ]] && break
    sleep 2
done
if [[ $idle_nodes -lt 5 ]]; then
    printf 'cluster-up: only %s of 5 nodes became idle\n' "$idle_nodes" >&2
    podman compose --env-file "$runtime_dir/cluster.env" -f "$compose_file" \
        logs --tail=120 controller compute001 slurmdbd >&2 || true
    exit 1
fi

# Node records live in the persistent controller volume. Slurm correctly keeps
# administrator-managed Features across restarts, so reconcile this disposable
# topology explicitly when the declared master/worker split changes.
podman exec finsim-controller scontrol update \
    NodeName=compute-host-001 Features=finsim-master ActiveFeatures=finsim-master
podman exec finsim-controller scontrol update \
    'NodeName=compute-host-[002-005]' Features=finsim-worker ActiveFeatures=finsim-worker
feature_map=$(podman exec finsim-controller \
    sinfo --noheader --Node --format='%N|%f' 2>/dev/null || true)
master_nodes=$(awk -F'|' '$2 == "finsim-master" {n++} END {print n+0}' <<<"$feature_map")
worker_nodes=$(awk -F'|' '$2 == "finsim-worker" {n++} END {print n+0}' <<<"$feature_map")
if [[ $master_nodes -ne 1 || $worker_nodes -ne 4 ]]; then
    printf 'cluster-up: expected one master and four worker feature nodes\n' >&2
    printf '%s\n' "$feature_map" >&2
    exit 1
fi

printf 'Waiting for FinSimProxy readiness...\n'
proxy_ready=0
for _ in {1..90}; do
    if curl --fail --silent --max-time 3 \
        "http://127.0.0.1:${FINSIM_PROXY_PORT:-8088}/health/ready" >/dev/null; then
        proxy_ready=1
        break
    fi
    sleep 2
done
if [[ $proxy_ready != 1 ]]; then
    printf 'cluster-up: FinSimProxy did not become ready\n' >&2
    podman compose --env-file "$runtime_dir/cluster.env" -f "$compose_file" \
        logs --tail=160 token-issuer restd cache proxy >&2 || true
    exit 1
fi

printf '\nFinSim simulation cluster is ready.\n'
podman exec finsim-controller sinfo --Node --long
printf 'Submit demo: scripts/submit-simulation.sh --wait\n'
printf 'Stop:        scripts/cluster-down.sh\n'
