#!/usr/bin/env bash
# Add or safely remove an extra dynamic compute node from the local simulation.
set -Eeuo pipefail
umask 077

project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$project_dir"
# shellcheck source=lib/runtime-env.sh
source scripts/lib/runtime-env.sh

usage() {
    cat <<'EOF'
Usage:
  scripts/local-node.sh add NODE_NAME master|worker
  scripts/local-node.sh remove NODE_NAME [WAIT_SECONDS]

NODE_NAME must be a lowercase DNS label, for example compute-host-006.
Removal drains the node and preserves .runtime/state/slurmd/NODE_NAME.
EOF
}
die() { printf 'local-node: %s\n' "$*" >&2; exit 1; }

[[ $# -ge 2 ]] || { usage >&2; exit 64; }
action=$1
node=$2
[[ $node =~ ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$ ]] ||
    die 'NODE_NAME must be one lowercase DNS label'
container="finsim-dynamic-$node"
runtime_env_load .runtime/cluster.env || die 'invalid local runtime environment'

case $action in
    add)
        [[ $# -eq 3 ]] || { usage >&2; exit 64; }
        case $3 in
            master) role=compute-master; feature=finsim-master ;;
            worker) role=compute-worker; feature=finsim-worker ;;
            *) die 'node profile must be master or worker' ;;
        esac
        podman container exists "$container" && die "container already exists: $container"
        podman network exists finsim_control || die 'local cluster network is absent; start the cluster first'
        spool="$project_dir/.runtime/state/slurmd/$node"
        podman unshare install -d -o 0 -g 0 -m 0700 "$spool"
        declare -a command=(
            podman run --detach --name "$container" --hostname "$node"
            --label io.finsim.dynamic-node=true
            --network finsim_control --read-only
            --security-opt no-new-privileges --cap-drop ALL
            --cap-add CHOWN --cap-add DAC_OVERRIDE --cap-add FOWNER
            --cap-add KILL --cap-add SETGID --cap-add SETUID --cap-add SYS_NICE
            --tmpfs /run:rw,nodev,nosuid,noexec,size=64m,mode=0755
            --tmpfs /tmp:rw,nodev,nosuid,noexec,size=128m,mode=1777
            --env "CONTAINER_ROLE=$role"
            --env CLUSTER_DEPLOYMENT_PROFILE=simulation
            --env START_EMBEDDED_MUNGE=1
            --env MUNGE_KEY_FILE=/run/secrets/munge.key
            --env SLURMD_REGISTRATION_MODE=dynamic
            --env SLURM_CONF_SERVER=controller:6817
            --env "SLURMD_FEATURE=$feature"
            --volume "$project_dir/.runtime/config:/etc/finsim-cluster:ro,z"
            --volume "$project_dir/.runtime/secrets/munge.key:/run/secrets/munge.key:ro,z"
            --volume "$project_dir/.runtime/container-secrets/cache-client.jwt:/finsim-credentials/cache.jwt:ro,z"
            --volume "$project_dir/.runtime/apps:/finsim-app:ro,z"
            --volume "$project_dir/.runtime/data:/finsim-data:z"
            --volume "$spool:/var/spool/slurmd:z"
            --health-cmd /usr/local/sbin/cluster-healthcheck
            --health-interval 10s --health-timeout 5s --health-retries 12)
        if [[ $role == compute-master ]]; then
            command+=(--volume "$project_dir/.runtime/credentials:/run/credentials:ro,z")
        fi
        command+=("localhost/finsim-runtime:$FINSIM_IMAGE_TAG")
        "${command[@]}" >/dev/null
        for _ in {1..60}; do
            if podman exec finsim-controller scontrol show node "$node" --oneliner \
                    2>/dev/null | grep -q 'State=IDLE'; then
                printf 'Dynamic node is ready: %s (%s)\n' "$node" "$feature"
                exit 0
            fi
            sleep 2
        done
        podman logs --tail=100 "$container" >&2 || true
        die "node did not register: $node"
        ;;
    remove)
        [[ $# -le 3 ]] || { usage >&2; exit 64; }
        wait_seconds=${3:-600}
        [[ $wait_seconds =~ ^[0-9]+$ ]] && ((wait_seconds >= 0 && wait_seconds <= 86400)) ||
            die 'WAIT_SECONDS must be between 0 and 86400'
        podman container exists "$container" || die "dynamic container does not exist: $container"
        podman exec finsim-controller scontrol update \
            "NodeName=$node" State=DRAIN Reason=local-node-removal
        deadline=$((SECONDS + wait_seconds))
        while podman exec finsim-controller squeue --noheader --nodes "$node" | grep -q .; do
            ((SECONDS < deadline)) || die 'timed out waiting for allocations to finish'
            sleep 5
        done
        podman stop --time 60 "$container" >/dev/null
        for _ in {1..30}; do
            if podman exec finsim-controller scontrol delete "NodeName=$node" 2>/dev/null; then
                podman rm "$container" >/dev/null
                printf 'Removed dynamic node %s; spool was retained.\n' "$node"
                exit 0
            fi
            sleep 2
        done
        die 'container is stopped but Slurm node deletion failed; investigate before removing it'
        ;;
    *) usage >&2; exit 64 ;;
esac
