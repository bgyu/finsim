#!/usr/bin/env bash
# Render one canonical configuration bundle before any container starts.
set -Eeuo pipefail
umask 077

project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
profile=simulation
output_dir=$project_dir/.runtime/config

while (($#)); do
    case $1 in
        --profile) profile=${2:?}; shift 2 ;;
        --output-dir) output_dir=${2:?}; shift 2 ;;
        -h|--help)
            printf 'usage: %s [--profile simulation|production] [--output-dir DIR]\n' "$0"
            exit 0 ;;
        *) printf 'prepare-config: unknown option: %s\n' "$1" >&2; exit 64 ;;
    esac
done
[[ $profile == simulation || $profile == production ]] || {
    printf 'prepare-config: profile must be simulation or production\n' >&2
    exit 64
}
[[ $output_dir == /* ]] || output_dir=$project_dir/$output_dir
install -d -m 0700 "$output_dir"
rm -f -- "$output_dir/lifecycle-client.conf"

validate_host() {
    local value=$1 description=$2
    [[ $value =~ ^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$ ]] || {
        printf 'prepare-config: invalid %s\n' "$description" >&2
        exit 64
    }
}

validate_bounded_uint() {
    local value=$1 minimum=$2 maximum=$3 description=$4
    [[ $value =~ ^[0-9]+$ ]] && ((value >= minimum && value <= maximum)) || {
        printf 'prepare-config: %s must be between %s and %s\n' \
            "$description" "$minimum" "$maximum" >&2
        exit 64
    }
}

if [[ $profile == simulation ]]; then
    cluster_name=${FINSIM_CLUSTER_NAME:-finsim-local}
    controller_lines='SlurmctldHost=controller'
    accounting_lines='AccountingStorageHost=slurmdbd'
    auth_alt='jwt_key=/run/slurm/config/jwt_hs256.key'
    tls_lines=''
    proctrack=proctrack/linuxproc
    task_plugin=task/none
    job_acct=jobacct_gather/linux
    containment=''
    cgroup_plugin=disabled
    cgroup_constraints='# Rootless simulation does not claim production isolation.'
    max_nodes=${FINSIM_MAX_NODES:-100}
    max_jobs=${FINSIM_MAX_JOBS:-10000}
    private_data=''
    power_save=''
    dbd_auth=''
    dbd_tls=''
    dbd_hosts='DbdHost=slurmdbd'
    mariadb_tls=''
    [[ ${FINSIM_LIFECYCLE_ENABLE:-0} == 0 ]] || {
        printf 'prepare-config: lifecycle integration is production-only\n' >&2
        exit 64
    }
else
    cluster_name=${FINSIM_CLUSTER_NAME:-finsim}
    primary=${FINSIM_CONTROLLER_PRIMARY:?set FINSIM_CONTROLLER_PRIMARY}
    backup=${FINSIM_CONTROLLER_BACKUP:?set FINSIM_CONTROLLER_BACKUP}
    dbd_primary=${FINSIM_SLURMDBD_PRIMARY:?set FINSIM_SLURMDBD_PRIMARY}
    dbd_backup=${FINSIM_SLURMDBD_BACKUP:?set FINSIM_SLURMDBD_BACKUP}
    validate_host "$primary" FINSIM_CONTROLLER_PRIMARY
    validate_host "$backup" FINSIM_CONTROLLER_BACKUP
    validate_host "$dbd_primary" FINSIM_SLURMDBD_PRIMARY
    validate_host "$dbd_backup" FINSIM_SLURMDBD_BACKUP
    controller_lines=$(printf 'SlurmctldHost=%s\nSlurmctldHost=%s' "$primary" "$backup")
    accounting_lines=$(printf 'AccountingStorageHost=%s\nAccountingStorageBackupHost=%s' "$dbd_primary" "$dbd_backup")
    auth_alt='jwks=/run/slurm/config/slurm-jwks.json,use_jwt_client_ids_only,disable_token_creation'
    tls_lines='TLSType=tls/s2n
TLSParameters=ca_cert_file=/run/slurm/config/ca_cert.pem,ctld_cert_file=/run/slurm/config/ctld_cert.pem,ctld_cert_key_file=/run/slurm/config/ctld_cert_key.pem,restd_cert_file=/run/slurm/config/restd_cert.pem,restd_cert_key_file=/run/slurm/config/restd_cert_key.pem,slurmd_cert_file=/run/slurm/config/slurmd_cert.pem,slurmd_cert_key_file=/run/slurm/config/slurmd_cert_key.pem'
    proctrack=proctrack/cgroup
    task_plugin=task/cgroup,task/affinity
    job_acct=jobacct_gather/cgroup
    containment='NamespaceType=namespace/tmpfs
PrologFlags=Contain'
    cgroup_plugin=cgroup/v2
    cgroup_constraints='ConstrainCores=yes
ConstrainRAMSpace=yes
ConstrainSwapSpace=yes
AllowedRAMSpace=100
AllowedSwapSpace=0
ConstrainDevices=yes'
    max_nodes=${FINSIM_MAX_NODES:-4096}
    max_jobs=${FINSIM_MAX_JOBS:-200000}
    private_data='PrivateData=accounts,events,jobs,reservations,usage,users'
    lifecycle_enable=${FINSIM_LIFECYCLE_ENABLE:-0}
    [[ $lifecycle_enable == 0 || $lifecycle_enable == 1 ]] || {
        printf 'prepare-config: FINSIM_LIFECYCLE_ENABLE must be 0 or 1\n' >&2
        exit 64
    }
    power_save=''
    if [[ $lifecycle_enable == 1 ]]; then
        lifecycle_api=${FINSIM_LIFECYCLE_API_URL:?set FINSIM_LIFECYCLE_API_URL}
        [[ $lifecycle_api =~ ^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?(/[-A-Za-z0-9._~]+)*$ ]] || {
            printf 'prepare-config: FINSIM_LIFECYCLE_API_URL must be a fixed HTTPS URL\n' >&2
            exit 64
        }
        resume_timeout=${FINSIM_RESUME_TIMEOUT:-900}
        suspend_timeout=${FINSIM_SUSPEND_TIMEOUT:-300}
        suspend_time=${FINSIM_SUSPEND_TIME:-1800}
        resume_rate=${FINSIM_RESUME_RATE:-60}
        suspend_rate=${FINSIM_SUSPEND_RATE:-120}
        for setting in resume_timeout:60:3600 suspend_timeout:30:3600 \
                suspend_time:60:86400 resume_rate:0:10000 suspend_rate:0:10000; do
            IFS=: read -r name minimum maximum <<<"$setting"
            validate_bounded_uint "${!name}" "$minimum" "$maximum" "FINSIM_${name^^}"
        done
        power_save=$(printf '%s\n' \
            'ResumeProgram=/usr/local/libexec/slurm/slurm-resume-program' \
            'SuspendProgram=/usr/local/libexec/slurm/slurm-suspend-program' \
            "ResumeTimeout=$resume_timeout" "SuspendTimeout=$suspend_timeout" \
            "SuspendTime=$suspend_time" "ResumeRate=$resume_rate" \
            "SuspendRate=$suspend_rate" 'ReconfigFlags=KeepPowerSaveSettings')
        cat >"$output_dir/lifecycle-client.conf" <<EOF
API_URL=${lifecycle_api%/}
CLUSTER_ID=$cluster_name
TLS_CA_FILE=/run/secrets/lifecycle-ca.pem
TLS_CERT_FILE=/run/secrets/lifecycle-client.pem
TLS_KEY_FILE=/run/secrets/lifecycle-client-key.pem
CONNECT_TIMEOUT_SECONDS=5
TOTAL_TIMEOUT_SECONDS=20
MAX_RESUME_CONTEXT_BYTES=1048576
EOF
        chmod 0644 "$output_dir/lifecycle-client.conf"
    fi
    dbd_auth='AuthAltTypes=auth/jwt
AuthAltParameters=jwks=/run/slurm/config/slurm-jwks.json,use_jwt_client_ids_only,disable_token_creation'
    dbd_tls='TLSType=tls/s2n
TLSParameters=ca_cert_file=/run/slurm/config/ca_cert.pem,dbd_cert_file=/run/slurm/config/dbd_cert.pem,dbd_cert_key_file=/run/slurm/config/dbd_cert_key.pem'
    dbd_hosts=$(printf 'DbdHost=%s\nDbdBackupHost=%s' "$dbd_primary" "$dbd_backup")
    mariadb_tls='ssl_ca=/run/secrets/database-ca.pem
ssl_cert=/run/secrets/database-server.pem
ssl_key=/run/secrets/database-server-key.pem
require_secure_transport=ON'
fi

for numeric in max_nodes max_jobs; do
    value=${!numeric}
    [[ $value =~ ^[1-9][0-9]*$ ]] || {
        printf 'prepare-config: %s must be a positive integer\n' "$numeric" >&2
        exit 64
    }
done
[[ $cluster_name =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$ ]] || {
    printf 'prepare-config: invalid cluster name\n' >&2
    exit 64
}

python3 - "$project_dir/config/slurm.conf.in" "$output_dir/slurm.conf" \
    "$cluster_name" "$controller_lines" "$auth_alt" "$tls_lines" \
    "$proctrack" "$task_plugin" "$containment" "$max_nodes" "$max_jobs" \
    "$accounting_lines" "$job_acct" "$private_data" "$power_save" <<'PY'
from pathlib import Path
import sys

source = Path(sys.argv[1]).read_text(encoding="utf-8")
names = ("CLUSTER_NAME", "CONTROLLER_LINES", "AUTH_ALT_PARAMETERS", "TLS_LINES",
         "PROCTRACK_TYPE", "TASK_PLUGIN", "CONTAINMENT_LINES", "MAX_NODE_COUNT",
         "MAX_JOB_COUNT", "ACCOUNTING_HOST_LINES", "JOB_ACCT_GATHER_TYPE",
         "PRIVATE_DATA_LINE", "POWER_SAVE_LINES")
values = dict(zip(names, sys.argv[3:], strict=True))
for key, value in values.items():
    marker = "@" + key + "@"
    if source.count(marker) != 1:
        raise SystemExit(f"template marker {marker} must occur once")
    source = source.replace(marker, value)
if "@" in source:
    raise SystemExit("unresolved slurm.conf template marker")
Path(sys.argv[2]).write_text(source, encoding="utf-8")
PY

python3 - "$project_dir/config/cgroup.conf.in" "$output_dir/cgroup.conf" \
    "$cgroup_plugin" "$cgroup_constraints" <<'PY'
from pathlib import Path
import sys
text = Path(sys.argv[1]).read_text(encoding="utf-8")
text = text.replace("@CGROUP_PLUGIN@", sys.argv[3])
text = text.replace("@CGROUP_CONSTRAINTS@", sys.argv[4])
Path(sys.argv[2]).write_text(text, encoding="utf-8")
PY

python3 - "$project_dir/config/slurmdbd.conf.template" \
    "$output_dir/slurmdbd.conf.template" "$dbd_auth" "$dbd_tls" "$dbd_hosts" <<'PY'
from pathlib import Path
import sys
text = Path(sys.argv[1]).read_text(encoding="utf-8")
for marker, value in zip(
    ("@AUTH_CONFIGURATION@", "@CONTROL_TLS_CONFIGURATION@", "@DBD_HOST_CONFIGURATION@"),
    sys.argv[3:], strict=True):
    if text.count(marker) != 1:
        raise SystemExit(f"template marker {marker} must occur once")
    text = text.replace(marker, value)
Path(sys.argv[2]).write_text(text, encoding="utf-8")
PY
chmod 0600 "$output_dir/slurmdbd.conf.template"
install -m 0644 "$project_dir/config/job_container.conf" "$output_dir/job_container.conf"
python3 - "$project_dir/config/mariadb-server.cnf.in" \
    "$output_dir/mariadb-server.cnf" "$mariadb_tls" <<'PY'
from pathlib import Path
import sys
text = Path(sys.argv[1]).read_text(encoding="utf-8")
marker = "@MARIADB_TLS_LINES@"
if text.count(marker) != 1:
    raise SystemExit("MariaDB template marker must occur once")
Path(sys.argv[2]).write_text(text.replace(marker, sys.argv[3]), encoding="utf-8")
PY
chmod 0644 "$output_dir/slurm.conf" "$output_dir/cgroup.conf"

python3 - "$output_dir" "$profile" <<'PY'
from pathlib import Path
import hashlib, json, sys
root = Path(sys.argv[1])
files = {}
for path in sorted(root.iterdir()):
    if path.is_file() and path.name != "generated-manifest.json":
        files[path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
manifest = {"schema": 1, "profile": sys.argv[2], "files": files}
(root / "generated-manifest.json").write_text(
    json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
PY
chmod 0644 "$output_dir/generated-manifest.json"
printf 'Prepared %s configuration in %s\n' "$profile" "$output_dir"
