#!/usr/bin/env bash
# Render one rootful production Quadlet and a non-secret environment example.
# Secrets are mounted individually; the network must be provisioned separately.
set -Eeuo pipefail
umask 077

project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
role= instance= image= network= output_dir= app_release= lifecycle=0

usage() {
    cat <<'EOF'
Usage: scripts/render-production-unit.sh OPTIONS
  --role ROLE          database|slurmdbd|controller|restd|compute-master|
                       compute-worker|proxy|cache
  --instance NAME      Stable DNS-compatible host/instance name
  --image IMAGE        Fully-qualified finsim-runtime reference by sha256 digest
  --network NAME       Pre-created routable Podman network (host is forbidden)
  --output-dir DIR     Destination directory
  --app-release ID     Required for proxy and cache
  --enable-lifecycle   Mount controller lifecycle mTLS inputs
EOF
}

while (($#)); do
    case $1 in
        --role) role=${2:?}; shift 2 ;;
        --instance) instance=${2:?}; shift 2 ;;
        --image) image=${2:?}; shift 2 ;;
        --network) network=${2:?}; shift 2 ;;
        --output-dir) output_dir=${2:?}; shift 2 ;;
        --app-release) app_release=${2:?}; shift 2 ;;
        --enable-lifecycle) lifecycle=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'render-production-unit: unknown option: %s\n' "$1" >&2; exit 64 ;;
    esac
done

[[ $role =~ ^(database|slurmdbd|controller|restd|compute-master|compute-worker|proxy|cache)$ ]] || {
    printf 'render-production-unit: unsupported role\n' >&2; exit 64;
}
[[ $instance =~ ^[a-z0-9]([a-z0-9.-]{0,61}[a-z0-9])?$ ]] || {
    printf 'render-production-unit: invalid instance name\n' >&2; exit 64;
}
[[ $image =~ ^[A-Za-z0-9.-]+(:[0-9]{1,5})?/[A-Za-z0-9._/-]+@sha256:[a-f0-9]{64}$ ]] || {
    printf 'render-production-unit: image must be fully qualified and digest pinned\n' >&2; exit 64;
}
[[ $network =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}(\.network)?$ && $network != host ]] || {
    printf 'render-production-unit: network must be a named, pre-created routable network\n' >&2; exit 64;
}
[[ -n $output_dir ]] || { printf 'render-production-unit: --output-dir is required\n' >&2; exit 64; }
if [[ $role == proxy || $role == cache ]]; then
    [[ $app_release =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$ ]] || {
        printf 'render-production-unit: application roles require --app-release\n' >&2; exit 64;
    }
elif [[ -n $app_release ]]; then
    printf 'render-production-unit: --app-release is valid only for proxy/cache\n' >&2; exit 64
fi
[[ $lifecycle == 0 || $role == controller ]] || {
    printf 'render-production-unit: lifecycle inputs belong only on controllers\n' >&2; exit 64;
}
[[ $output_dir == /* ]] || output_dir=$PWD/$output_dir
install -d -m 0750 "$output_dir"

declare -a env_lines=() caps=() volumes=() example=() security_lines=()
container_role=$role
exec_line=
health_command='HealthCmd=/usr/local/sbin/cluster-healthcheck'
trusted_compute=0

add_slurm_config() {
    env_lines+=("Environment=SLURM_CONF=/etc/finsim-cluster/slurm.conf")
    volumes+=("Volume=/etc/finsim/config:/etc/finsim-cluster:ro")
}
add_munge_socket() {
    env_lines+=("Environment=START_EMBEDDED_MUNGE=0")
    volumes+=("Volume=/run/munge:/run/munge:rw")
}
add_control_tls() {
    env_lines+=(
        "Environment=SLURM_TLS_CA_FILE=/run/secrets/slurm-ca.pem"
        "Environment=SLURM_TLS_CERT_FILE=/run/secrets/slurm-role.pem"
        "Environment=SLURM_TLS_KEY_FILE=/run/secrets/slurm-role-key.pem")
    volumes+=(
        "Volume=/etc/finsim/secrets/slurm-ca.pem:/run/secrets/slurm-ca.pem:ro"
        "Volume=/etc/finsim/secrets/$instance/slurm-role.pem:/run/secrets/slurm-role.pem:ro"
        "Volume=/etc/finsim/secrets/$instance/slurm-role-key.pem:/run/secrets/slurm-role-key.pem:ro")
}
add_jwks() {
    env_lines+=("Environment=SLURM_JWKS_FILE=/run/secrets/slurm-jwks.json")
    volumes+=("Volume=/etc/finsim/public/slurm-jwks.json:/run/secrets/slurm-jwks.json:ro")
}
add_application_tls() {
    env_lines+=(
        "Environment=APPLICATION_TLS_CA_FILE=/run/secrets/service-ca-bundle.pem"
        "Environment=APPLICATION_TLS_CERT_FILE=/run/secrets/application-role.pem"
        "Environment=APPLICATION_TLS_KEY_FILE=/run/secrets/application-role-key.pem")
    volumes+=(
        "Volume=/etc/finsim/public/service-ca-bundle.pem:/run/secrets/service-ca-bundle.pem:ro"
        "Volume=/etc/finsim/secrets/$instance/application-role.pem:/run/secrets/application-role.pem:ro"
        "Volume=/etc/finsim/secrets/$instance/application-role-key.pem:/run/secrets/application-role-key.pem:ro")
}
add_service_caps() { caps+=("AddCapability=CHOWN DAC_OVERRIDE FOWNER KILL SETGID SETUID"); }

case $role in
    database)
        caps+=("AddCapability=CHOWN DAC_OVERRIDE FOWNER SETGID SETUID")
        env_lines+=("Environment=MARIADB_SLURM_PASSWORD_FILE=/run/secrets/mariadb-slurm.password")
        volumes+=(
            "Volume=/etc/finsim/config:/etc/finsim-cluster:ro"
            "Volume=/etc/finsim/secrets/mariadb-slurm.password:/run/secrets/mariadb-slurm.password:ro"
            "Volume=/etc/finsim/secrets/database-ca.pem:/run/secrets/database-ca.pem:ro"
            "Volume=/etc/finsim/secrets/$instance/database-server.pem:/run/secrets/database-server.pem:ro"
            "Volume=/etc/finsim/secrets/$instance/database-server-key.pem:/run/secrets/database-server-key.pem:ro"
            "Volume=/var/lib/finsim/mariadb:/var/lib/mysql:rw")
        example+=("# No additional settings are required for this role.") ;;
    slurmdbd)
        add_service_caps; add_slurm_config; add_munge_socket; add_control_tls; add_jwks
        env_lines+=(
            "Environment=SLURMDBD_CONFIG_TEMPLATE=/etc/finsim-cluster/slurmdbd.conf.template"
            "Environment=SLURMDBD_STORAGE_PASSWORD_FILE=/run/secrets/mariadb-slurm.password"
            "Environment=SLURMDBD_STORAGE_TLS_CA_FILE=/run/secrets/database-ca.pem")
        volumes+=(
            "Volume=/etc/finsim/secrets/mariadb-slurm.password:/run/secrets/mariadb-slurm.password:ro"
            "Volume=/etc/finsim/secrets/database-ca.pem:/run/secrets/database-ca.pem:ro"
            "Volume=/var/lib/finsim/slurmdbd:/var/lib/slurm:rw")
        example+=("SLURMDBD_STORAGE_HOST=database.service.internal" "SLURMDBD_STORAGE_PORT=3306"
            "SLURMDBD_STORAGE_DATABASE=slurm_acct_db" "SLURMDBD_STORAGE_USER=slurm") ;;
    controller)
        add_service_caps; add_slurm_config; add_munge_socket; add_control_tls; add_jwks
        volumes+=("Volume=/var/lib/finsim/slurmctld:/var/spool/slurmctld:rw"
            "Volume=/srv/finsim/data:/finsim-data:rw")
        if [[ $lifecycle == 1 ]]; then
            env_lines+=("Environment=SLURM_LIFECYCLE_CONFIG=/etc/finsim-cluster/lifecycle-client.conf")
            volumes+=("Volume=/etc/finsim/secrets/lifecycle-ca.pem:/run/secrets/lifecycle-ca.pem:ro"
                "Volume=/etc/finsim/secrets/$instance/lifecycle-client.pem:/run/secrets/lifecycle-client.pem:ro"
                "Volume=/etc/finsim/secrets/$instance/lifecycle-client-key.pem:/run/secrets/lifecycle-client-key.pem:ro")
        fi
        example+=("# Controller endpoints are rendered into slurm.conf.") ;;
    restd)
        add_service_caps; add_slurm_config; add_control_tls; add_jwks
        env_lines+=("Environment=REST_AUTH_MODE=jwt"
            "Environment=SLURMRESTD_LISTEN=https://0.0.0.0:6820")
        example+=("SLURMRESTD_THREADS=32") ;;
    compute-master|compute-worker)
        # Root slurmd is a host-trusted agent. Cgroup device filtering and the
        # job_container/tmpfs mount namespace need host integration that a
        # portable capability allow-list cannot guarantee. Limit this profile
        # to dedicated compute hosts and never copy it to another role.
        trusted_compute=1
        add_slurm_config; add_munge_socket; add_control_tls
        volumes+=("Volume=/srv/finsim/apps:/finsim-app:ro"
            "Volume=/srv/finsim/data:/finsim-data:rw"
            "Volume=/etc/finsim/public/service-ca-bundle.pem:/run/secrets/service-ca-bundle.pem:ro"
            "Volume=/etc/finsim/secrets/cache-client.jwt:/finsim-credentials/cache.jwt:ro"
            "Volume=/var/lib/finsim/slurmd:/var/spool/slurmd:rw")
        if [[ $role == compute-master ]]; then
            volumes+=("Volume=/run/finsim/credentials:/run/credentials:ro")
            example+=("SLURMD_FEATURE=finsim-master")
        else
            example+=("SLURMD_FEATURE=finsim-worker")
        fi
        example+=("SLURMD_REGISTRATION_MODE=dynamic"
            "SLURM_CONF_SERVER=controller-primary:6817,controller-backup:6817") ;;
    proxy)
        container_role=application
        caps+=("AddCapability=SETGID SETUID")
        add_application_tls
        volumes+=("Volume=/srv/finsim/apps:/finsim-app:ro"
            "Volume=/srv/finsim/data:/finsim-data:rw"
            "Volume=/etc/finsim/public/app-jwks.json:/run/secrets/app-jwks.json:ro"
            "Volume=/etc/finsim/secrets/cache-client.jwt:/run/secrets/cache-client.jwt:ro"
            "Volume=/run/finsim/credentials:/run/credentials:ro")
        exec_line="Exec=/finsim-app/releases/$app_release/FinSimProxy/FinSimProxy"
        health_command="HealthCmd=/usr/bin/curl --fail --silent --show-error --max-time 5 --cacert /run/secrets/service-ca-bundle.pem https://$instance:8088/health/ready"
        example+=("ASPNETCORE_URLS=https://0.0.0.0:8088"
            "Jwt__Issuer=https://identity.example.invalid/" "Jwt__Audience=finsim-proxy"
            "Jwt__JwksFile=/run/secrets/app-jwks.json"
            "Cache__BaseUrl=https://cache.service.internal/"
            "Cache__TokenFile=/run/secrets/cache-client.jwt"
            "Cache__JobTokenFile=/finsim-credentials/cache.jwt"
            "Slurm__BaseUrl=https://slurmrestd.service.internal:6820/"
            "Slurm__TokenFile=/run/credentials/finsim.jwt" "Slurm__User=finsim"
            "Slurm__MasterPartition=orchestrator" "Slurm__MasterFeature=finsim-master"
            "Slurm__WorkerPartition=batch" "Slurm__WorkerFeature=finsim-worker"
            "Jobs__ServiceCaFile=/run/secrets/service-ca-bundle.pem"
            "Paths__DataRoot=/finsim-data"
            "Paths__ApplicationExecutable=/finsim-app/releases/$app_release/FinSim/FinSim") ;;
    cache)
        container_role=application
        caps+=("AddCapability=SETGID SETUID")
        add_application_tls
        volumes+=("Volume=/srv/finsim/apps:/finsim-app:ro"
            "Volume=/etc/finsim/public/app-jwks.json:/run/secrets/app-jwks.json:ro"
            "Volume=/var/lib/finsim/cache:/var/lib/finsim/cache:rw")
        exec_line="Exec=/finsim-app/releases/$app_release/FinSimCache/FinSimCache"
        health_command="HealthCmd=/usr/bin/curl --fail --silent --show-error --max-time 5 --cacert /run/secrets/service-ca-bundle.pem https://$instance:8090/health/ready"
        example+=("ASPNETCORE_URLS=https://0.0.0.0:8090"
            "Jwt__Issuer=https://identity.example.invalid/" "Jwt__Audience=finsim-cache"
            "Jwt__JwksFile=/run/secrets/app-jwks.json" "Cache__DataRoot=/var/lib/finsim/cache") ;;
esac

if [[ $trusted_compute == 1 ]]; then
    security_lines+=(
        "# TRUSTED COMPUTE HOST: root slurmd is equivalent to host root."
        "# Run only on a dedicated, replaceable compute host after the EL10 acceptance gate."
        "PodmanArgs=--privileged --cgroupns=host"
        "Volume=/sys/fs/cgroup:/sys/fs/cgroup:rw")
else
    security_lines+=("NoNewPrivileges=true" "DropCapability=all")
fi

join_lines() { local IFS=$'\n'; printf '%s' "$*"; }
unit="$output_dir/finsim-$instance.container"
python3 - "$project_dir/deploy/quadlet/finsim-role.container.in" "$unit" \
    "$role" "$instance" "$image" "$container_role" "$network" \
    "$(join_lines "${env_lines[@]}")" "$(join_lines "${caps[@]}")" \
    "$(join_lines "${volumes[@]}")" "$exec_line" \
    "$(join_lines "${security_lines[@]}")" "$health_command" <<'PY'
from pathlib import Path
import re, sys
source = Path(sys.argv[1]).read_text(encoding="utf-8")
values = dict(zip(("ROLE", "INSTANCE", "IMAGE", "CONTAINER_ROLE", "NETWORK",
                   "ENVIRONMENT", "CAPABILITIES", "VOLUMES", "EXEC",
                   "SECURITY_OPTIONS", "HEALTH_COMMAND"),
                  sys.argv[3:], strict=True))
for name, value in values.items():
    marker = f"@{name}@"
    if source.count(marker) < 1:
        raise SystemExit(f"template marker {marker} is missing")
    source = source.replace(marker, value)
if re.search(r"@[A-Z_]+@", source):
    raise SystemExit("unresolved Quadlet template marker")
Path(sys.argv[2]).write_text(source, encoding="utf-8")
PY
chmod 0644 "$unit"
example_file="$output_dir/finsim-$instance.env.example"
{
    printf '# Non-secret settings for /etc/finsim/roles/%s.env\n' "$instance"
    printf '%s\n' "${example[@]}"
} >"$example_file"
chmod 0644 "$example_file"
printf 'Rendered %s and %s\n' "$unit" "$example_file"
