#!/usr/bin/env bash
# TeamCity/CI entrypoint. Run this on the Podman-capable build agent host; do
# not mount the Podman socket into finsim-dev and do not run nested containers.
set -Eeuo pipefail

project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$project_dir"
mode=${1:-verify}
image_tag=${BUILD_TAG:-ci}

case "$image_tag" in
    *[!A-Za-z0-9_.-]*|'') printf 'ci: BUILD_TAG contains unsafe characters\n' >&2; exit 64 ;;
esac

verify() {
    bash -n scripts/*.sh scripts/lib/*.sh deploy/lifecycle/*
    PYTHONPYCACHEPREFIX=/tmp/finsim-ci-pycache \
        python3 -m py_compile scripts/*.py
    python3 -m unittest discover -s scripts/tests -p 'test_*.py'
    if rg -n '\b(sbatch|squeue|sacct|scancel|scontrol)\b' src; then
        printf 'ci: application source must use Slurm REST only\n' >&2
        exit 1
    fi

    FINSIM_IMAGE_TAG=$image_tag scripts/build-images.sh
}

case "$mode" in
    verify)
        verify
        ;;
    integration)
        verify
        cleanup() {
            [[ ${KEEP_CLUSTER:-0} == 1 ]] || scripts/cluster-down.sh || true
        }
        trap cleanup EXIT
        FINSIM_IMAGE_TAG=$image_tag FINSIM_APP_RELEASE="ci-$image_tag" \
            scripts/prepare-cluster.sh
        scripts/cluster-up.sh
        scripts/acceptance-test.sh
        ;;
    *)
        printf 'usage: scripts/ci.sh [verify|integration]\n' >&2
        exit 64
        ;;
esac
