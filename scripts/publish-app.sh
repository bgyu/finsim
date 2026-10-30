#!/usr/bin/env bash
# Build, test and atomically publish an immutable application release.
set -Eeuo pipefail
umask 077
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
release=${FINSIM_APP_RELEASE:-dev}
image=${FINSIM_DEV_IMAGE:-localhost/finsim-dev:${FINSIM_IMAGE_TAG:-latest}}

while (($#)); do
    case $1 in
        --release) release=${2:?}; shift 2 ;;
        --image) image=${2:?}; shift 2 ;;
        -h|--help) printf 'usage: %s [--release ID] [--image IMAGE]\n' "$0"; exit 0 ;;
        *) printf 'publish-app: unknown option: %s\n' "$1" >&2; exit 64 ;;
    esac
done
[[ $release =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$ ]] || {
    printf 'publish-app: invalid release identifier\n' >&2; exit 64;
}

release_root=$project_dir/.runtime/apps/releases
destination=$release_root/$release
[[ ! -e $destination ]] || { printf 'publish-app: release already exists: %s\n' "$release" >&2; exit 73; }
temporary=$(mktemp -d --tmpdir="$release_root" '.release.XXXXXXXX')
cleanup() { [[ ! -d $temporary ]] || rm -rf -- "$temporary"; }
trap cleanup EXIT
# The dev image runs as UID 1000. Map the temporary output directory to that
# identity only for the build, then transfer it back to container root before
# publishing the immutable release.
podman unshare chown 1000:1000 "$temporary"

podman run --rm --user developer --entrypoint /bin/bash \
    --volume "$temporary:/publish:Z" "$image" -ceu '
dotnet restore FinSim.slnx --configfile NuGet.Config --locked-mode
dotnet build FinSim.slnx --configuration Release --no-restore
dotnet run --project tests/FinSim.Tests/FinSim.Tests.csproj --configuration Release --no-build
dotnet publish src/FinSim/FinSim.csproj --configuration Release --no-restore --output /publish/FinSim
dotnet publish src/FinSimProxy/FinSimProxy.csproj --configuration Release --no-restore --output /publish/FinSimProxy
dotnet publish src/FinSimCache/FinSimCache.csproj --configuration Release --no-restore --output /publish/FinSimCache
'
# Restore host/container-root ownership before the host creates the manifest.
# This also ensures the EXIT trap can remove a failed temporary release.
podman unshare chown -R 0:0 "$temporary"

python3 - "$temporary" "$release" <<'PY'
from pathlib import Path
import hashlib, json, os, sys
root = Path(sys.argv[1])
files = {}
for path in sorted(root.rglob("*")):
    if path.is_file():
        files[str(path.relative_to(root))] = hashlib.sha256(path.read_bytes()).hexdigest()
(root / "release-manifest.json").write_text(
    json.dumps({"schema": 1, "release": sys.argv[2], "files": files},
               indent=2, sort_keys=True) + "\n", encoding="utf-8")
for path in root.rglob("*"):
    os.chmod(path, 0o555 if path.is_dir() or os.access(path, os.X_OK) else 0o444)
os.chmod(root, 0o555)
PY
mv -- "$temporary" "$destination"
trap - EXIT
ln -sfn -- "releases/$release" "$project_dir/.runtime/apps/current.new"
mv -Tf -- "$project_dir/.runtime/apps/current.new" "$project_dir/.runtime/apps/current"
printf 'Published immutable application release %s\n' "$release"
