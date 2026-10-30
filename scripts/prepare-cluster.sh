#!/usr/bin/env bash
# Explicit, idempotent local preparation. This command never starts services.
set -Eeuo pipefail
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
release=${FINSIM_APP_RELEASE:-dev}
"$project_dir/scripts/prepare-storage.sh"
"$project_dir/scripts/prepare-config.sh" --profile simulation
FINSIM_APP_RELEASE=$release "$project_dir/scripts/bootstrap-dev-secrets.sh"
if [[ ! -d $project_dir/.runtime/apps/releases/$release ]]; then
    "$project_dir/scripts/publish-app.sh" --release "$release"
fi
"$project_dir/scripts/validate-deployment.sh"
