#!/usr/bin/env bash
# Validate prepared inputs without reading or printing credential values.
set -Eeuo pipefail
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
root=${FINSIM_DEPLOY_ROOT:-$project_dir/.runtime}
die() { printf 'validate-deployment: %s\n' "$*" >&2; exit 78; }
command -v podman >/dev/null 2>&1 || die 'podman is required'
for path in config/slurm.conf config/cgroup.conf config/slurmdbd.conf.template \
    config/mariadb-server.cnf config/generated-manifest.json secrets/munge.key \
    secrets/slurm-jwt-hs256.key secrets/mariadb-slurm.password \
    public/app-jwks.json container-secrets/cache-client.jwt cluster.env; do
    [[ -f $root/$path && ! -L $root/$path ]] || die "missing regular file: $path"
done
# shellcheck source=lib/runtime-env.sh
source "$project_dir/scripts/lib/runtime-env.sh"
runtime_env_load "$root/cluster.env" || die 'invalid cluster.env'
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
# Check the release from Podman's user namespace. Files owned by the rootless
# host user correctly appear as UID 0 there, matching their ownership inside
# every runtime container.
podman unshare python3 - "$release_dir" "$release" <<'PY'
from pathlib import Path
import hashlib, json, os, stat, sys

root = Path(sys.argv[1])
release = sys.argv[2]
manifest_path = root / "release-manifest.json"
root_info = root.lstat()
if root_info.st_uid != 0 or root_info.st_mode & 0o022:
    raise SystemExit("release root must be container-root-owned and non-writable")
try:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
except (OSError, ValueError) as error:
    raise SystemExit(f"invalid release manifest: {error}")
if manifest.get("schema") != 1 or manifest.get("release") != release:
    raise SystemExit("release manifest identity does not match selected release")
expected = manifest.get("files")
if not isinstance(expected, dict) or not expected:
    raise SystemExit("release manifest has no file map")

actual: dict[str, str] = {}
for path in sorted(root.rglob("*")):
    relative = str(path.relative_to(root))
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode):
        raise SystemExit(f"release contains a symlink: {relative}")
    if info.st_uid != 0:
        raise SystemExit(f"release path is not container-root-owned: {relative}")
    if info.st_mode & 0o022:
        raise SystemExit(f"release path is group/world writable: {relative}")
    if path.is_file() and path != manifest_path:
        actual[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
    elif not path.is_dir() and not path.is_file():
        raise SystemExit(f"release contains an unsupported file type: {relative}")

if set(actual) != set(expected):
    missing = sorted(set(expected) - set(actual))
    extra = sorted(set(actual) - set(expected))
    raise SystemExit(f"release file set mismatch; missing={missing}, extra={extra}")
for name, digest in actual.items():
    if expected.get(name) != digest:
        raise SystemExit(f"release digest mismatch: {name}")
PY
printf 'Deployment inputs are valid for release %s\n' "$release"
