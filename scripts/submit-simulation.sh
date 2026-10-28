#!/usr/bin/env bash
set -Eeuo pipefail
project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
exec python3 "$project_dir/scripts/submit-simulation.py" "$@"
