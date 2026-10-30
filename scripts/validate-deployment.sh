#!/usr/bin/env bash
# Validate prepared inputs without reading or printing credential values.
set -Eeuo pipefail
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
root=${FINSIM_DEPLOY_ROOT:-$project_dir/.runtime}
die() { printf 'validate-deployment: %s\n' "$*" >&2; exit 78; }
for path in config/slurm.conf config/cgroup.conf config/slurmdbd.conf.template \
    config/mariadb-server.cnf config/generated-manifest.json secrets/munge.key \
    secrets/slurm-jwt-hs256.key secrets/mariadb-slurm.password \
    public/app-jwks.json container-secrets/cache-client.jwt cluster.env; do
    [[ -f $root/$path && ! -L $root/$path ]] || die "missing regular file: $path"
done
# shellcheck disable=SC1090
source "$root/cluster.env"
release=${FINSIM_APP_RELEASE:-}
[[ $release =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$ ]] || die 'invalid FINSIM_APP_RELEASE'
release_dir=$root/apps/releases/$release
for executable in FinSim/FinSim FinSimProxy/FinSimProxy FinSimCache/FinSimCache; do
    path=$release_dir/$executable
    [[ -f $path && -x $path && ! -L $path ]] || die "missing release executable: $executable"
done
[[ -f $release_dir/release-manifest.json ]] || die 'release manifest is missing'
for secret in "$root"/secrets/* "$root"/container-secrets/*; do
    mode=$(stat --format='%a' -- "$secret")
    permissions=$((8#$mode))
    (((permissions & 0077) == 0)) || die "secret is accessible by group/other: ${secret#$root/}"
done
python3 - "$root/config/generated-manifest.json" "$root/config" <<'PY'
from pathlib import Path
import hashlib, json, sys
manifest = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
root = Path(sys.argv[2])
for name, expected in manifest.get("files", {}).items():
    actual = hashlib.sha256((root / name).read_bytes()).hexdigest()
    if actual != expected:
        raise SystemExit(f"configuration digest mismatch: {name}")
PY
printf 'Deployment inputs are valid for release %s\n' "$release"
