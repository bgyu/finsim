#!/usr/bin/env bash
# Create all mutable host paths and map ownership into the rootless namespace.
set -Eeuo pipefail
umask 077
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
root=${FINSIM_DEPLOY_ROOT:-$project_dir/.runtime}

command -v podman >/dev/null || { printf 'prepare-storage: podman is required\n' >&2; exit 127; }

for directory in \
    apps/releases data/cache data/logs data/reports data/workflows credentials \
    state/mariadb state/slurmctld state/slurmdbd \
    state/slurmd/compute001 state/slurmd/compute002 state/slurmd/compute003 \
    state/slurmd/compute004 state/slurmd/compute005 \
    logs/database logs/slurmdbd logs/controller logs/restd \
    logs/compute001 logs/compute002 logs/compute003 logs/compute004 logs/compute005; do
    # Create paths from the same rootless user namespace used by containers.
    # This remains idempotent after service UID ownership is mapped: on the
    # host those paths may deliberately appear to be owned by "nobody".
    podman unshare install -d -m 0700 "$root/$directory"
done

podman unshare chown -R 64032:64032 "$root/data"
podman unshare chown -R 64032:64032 "$root/credentials"
podman unshare chown -R 0:0 "$root/apps"
# Services and Slurm payloads need only traverse/read the immutable release
# tree. Individual releases are hardened to 0555/0444 by publish-app.sh.
chmod 0755 "$root/apps" "$root/apps/releases"
podman unshare chown -R 27:27 "$root/state/mariadb"
podman unshare chown -R 64030:64030 "$root/state/slurmctld" "$root/state/slurmdbd"
for spool in "$root"/state/slurmd/*; do podman unshare chown -R 0:0 "$spool"; done
printf 'Prepared runtime storage in %s\n' "$root"
