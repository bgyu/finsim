#!/usr/bin/env bash
# Production-oriented acceptance test for the local simulation boundary.
# Administrative Slurm CLI calls appear only here to verify scheduler state;
# FinSim application code and the submitted workflow use REST exclusively.
set -Eeuo pipefail
umask 077

project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$project_dir"

fail() {
    printf 'acceptance-test: %s\n' "$*" >&2
    exit 1
}

for command in podman curl python3 rg; do
    command -v "$command" >/dev/null 2>&1 || fail "$command is required"
done

[[ -f .runtime/cluster.env ]] || fail 'run scripts/cluster-up.sh first'

# Prevent an accidental regression to shelling out from application code.
if rg -n '\b(sbatch|squeue|sacct|scancel|scontrol)\b' src; then
    fail 'application source must use the Slurm REST client, not Slurm CLI tools'
fi

python3 -m unittest discover -s scripts/tests -p 'test_*.py'

declare -a required_containers=(
    finsim-controller finsim_database_1 finsim_slurmdbd_1 finsim_restd_1
    finsim_token-issuer_1 finsim_cache_1 finsim_proxy_1
    finsim_compute001_1 finsim_compute002_1 finsim_compute003_1
    finsim_compute004_1 finsim_compute005_1
)
for container in "${required_containers[@]}"; do
    running=$(podman inspect --format '{{.State.Running}}' "$container" 2>/dev/null || true)
    [[ $running == true ]] || fail "$container is not running"
    health=$(podman inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' \
        "$container" 2>/dev/null || true)
    [[ -z $health || $health == healthy ]] || fail "$container is not healthy ($health)"
done

idle_nodes=$(podman exec finsim-controller \
    sinfo --noheader --states=idle --Node --format='%N' | sort -u | awk 'NF {n++} END {print n+0}')
[[ $idle_nodes -eq 5 ]] || fail "expected five idle nodes, found $idle_nodes"
feature_map=$(podman exec finsim-controller sinfo --noheader --Node --format='%N|%f')
[[ $(awk -F'|' '$2 == "finsim-master" {n++} END {print n+0}' <<<"$feature_map") -eq 1 ]] ||
    fail 'expected exactly one finsim-master node'
[[ $(awk -F'|' '$2 == "finsim-worker" {n++} END {print n+0}' <<<"$feature_map") -eq 4 ]] ||
    fail 'expected exactly four finsim-worker nodes'

# A worker payload must have no filesystem path to the Slurm bearer token.
for container in finsim_compute002_1 finsim_compute003_1 finsim_compute004_1 finsim_compute005_1; do
    token_mount=$(podman inspect --format \
        '{{range .Mounts}}{{if eq .Destination "/run/slurm-token"}}present{{end}}{{end}}' \
        "$container")
    [[ -z $token_mount ]] || fail "$container unexpectedly mounts the Slurm token"
done

# The public edge must challenge unauthenticated requests.  Do not print or
# place a bearer token in argv; the authenticated client reads its 0600 file.
unauth_status=$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --request POST --header 'Content-Type: application/json' --data '{}' \
    "http://127.0.0.1:${FINSIM_PROXY_PORT:-8088}/v1/simulations/")
[[ $unauth_status == 401 ]] || fail "unauthenticated API returned HTTP $unauth_status"

# DisableRootJobs is a scheduler invariant.  An administrative negative test
# is safe because a successful submission is immediately treated as failure.
if podman exec finsim-controller sbatch --parsable --wrap /bin/true \
    >/tmp/finsim-root-submit.out 2>/tmp/finsim-root-submit.err; then
    fail 'Slurm unexpectedly accepted a root-owned job'
fi

test_output=$(mktemp /tmp/finsim-acceptance.XXXXXX)
trap 'rm -f -- "$test_output" /tmp/finsim-root-submit.out /tmp/finsim-root-submit.err' EXIT
scripts/submit-simulation.sh --count 20 --chunk-size 4 --parallel 3 --wait | tee "$test_output"

python3 - "$test_output" <<'PY'
import json
import pathlib
import sys

text = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
start = text.find("{\n")
if start < 0:
    raise SystemExit("acceptance-test: final report was not printed")
report = json.loads(text[start:])
expected = {
    "Status": "SUCCEEDED",
    "ValueCount": 20,
    "ChunkCount": 5,
    "Sum": 210,
    "Minimum": 1,
    "Maximum": 20,
}
for key, value in expected.items():
    if report.get(key) != value:
        raise SystemExit(f"acceptance-test: report {key}={report.get(key)!r}, expected {value!r}")
PY

# Accounting is the authoritative proof that both master and worker payloads
# were owned by the dedicated service identity rather than container root.
accounting=$(podman exec finsim-controller sacct --starttime today --noheader \
    --parsable2 --format=JobName,User,State)
finsim_jobs=$(awk -F'|' '$1 ~ /^finsim-/ {print}' <<<"$accounting")
[[ -n $finsim_jobs ]] || fail 'no FinSim accounting records were found'
if awk -F'|' '$1 ~ /^finsim-/ && $2 != "finsim" {exit 1}' <<<"$finsim_jobs"; then
    :
else
    fail 'a FinSim job did not run as the finsim service account'
fi
master_nodes=$(podman exec finsim-controller sacct --starttime today --noheader \
    --parsable2 --format=JobName,NodeList | awk -F'|' '$1 ~ /^finsim-.*-m$/ {print $2}')
[[ -n $master_nodes ]] || fail 'no master placement record was found'
[[ -z $(awk '$0 != "compute-host-001" {print}' <<<"$master_nodes") ]] ||
    fail 'a master ran outside the credential-bearing node'
worker_nodes=$(podman exec finsim-controller sacct --starttime today --noheader \
    --parsable2 --format=JobName,NodeList | awk -F'|' '$1 ~ /^finsim-.*-w$/ {print $2}')
[[ -n $worker_nodes ]] || fail 'no worker placement record was found'
[[ -z $(awk '$0 == "compute-host-001" {print}' <<<"$worker_nodes") ]] ||
    fail 'a worker ran on the credential-bearing node'

[[ $(podman exec finsim-controller pgrep -u 64030 -x slurmctld | wc -l) -eq 1 ]] ||
    fail 'slurmctld is not running as uid 64030'
[[ $(podman exec finsim_restd_1 pgrep -u 64031 -x slurmrestd | wc -l) -eq 1 ]] ||
    fail 'slurmrestd is not running as uid 64031'
[[ $(podman exec finsim_proxy_1 id -u) == 64032 ]] || fail 'proxy is not uid 64032'
[[ $(podman exec finsim_cache_1 id -u) == 64032 ]] || fail 'cache is not uid 64032'

# Production artifacts exclude SDK/build inputs and local-only services.
podman run --rm --entrypoint /bin/bash localhost/finsim-app:latest -ceu '
    ! dotnet --list-sdks | grep -q .
    test ! -e /src
    test ! -e /workspace
    test ! -e /opt/finsim/FinSimCache
    ! rpm -q mariadb-server >/dev/null 2>&1
'

printf 'FinSim acceptance test passed.\n'
