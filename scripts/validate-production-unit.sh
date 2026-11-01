#!/usr/bin/env bash
# Offline checks for a rendered production Quadlet. This does not replace the
# target host's Quadlet generator, SELinux, network, PKI or storage checks.
set -Eeuo pipefail

[[ $# -eq 1 ]] || {
    printf 'usage: %s RENDERED.container\n' "$0" >&2
    exit 64
}
unit=$1
[[ -f $unit && ! -L $unit ]] || {
    printf 'validate-production-unit: input must be a regular file\n' >&2
    exit 78
}
fail() { printf 'validate-production-unit: %s\n' "$*" >&2; exit 78; }

grep -Eq '^Image=[^[:space:]]+@sha256:[a-f0-9]{64}$' "$unit" ||
    fail 'image is not digest pinned'
grep -Eq '^Network=[A-Za-z0-9][A-Za-z0-9_.-]*(\.network)?$' "$unit" ||
    fail 'network is not a named network'
grep -Fxq 'ReadOnly=true' "$unit" || fail 'root filesystem is not read-only'
grep -Fxq 'Delegate=yes' "$unit" || fail 'systemd cgroup delegation is missing'
grep -Eq '^HealthCmd=' "$unit" || fail 'health command is missing'
! grep -Eq '@[A-Z_]+@' "$unit" || fail 'unit contains unresolved template markers'
! grep -Eq '^Network=host$|^UserNS=host$|SYS_ADMIN' "$unit" ||
    fail 'unit requests a prohibited host namespace or explicit SYS_ADMIN capability'
! grep -Eq '^Volume=/etc/finsim/secrets:/run/secrets' "$unit" ||
    fail 'unit mounts the complete secret directory'

role=$(sed -n 's/^Environment=CONTAINER_ROLE=//p' "$unit")
[[ $(grep -c '^Environment=CONTAINER_ROLE=' "$unit") -eq 1 ]] ||
    fail 'unit must define exactly one container role'
case $role in
    database)
        grep -Fq '/mariadb-slurm.password:/run/secrets/mariadb-slurm.password:ro' "$unit" ||
            fail 'database password mount is missing'
        grep -Fq '/database-server-key.pem:/run/secrets/database-server-key.pem:ro' "$unit" ||
            fail 'database TLS key mount is missing'
        ! grep -q 'slurm-jwks' "$unit" || fail 'database must not receive Slurm JWKS'
        ;;
    slurmdbd|controller)
        grep -q 'slurm-jwks.json' "$unit" || fail "$role JWKS mount is missing"
        grep -q '/run/munge' "$unit" || fail "$role MUNGE socket is missing"
        ;;
    restd)
        grep -q 'slurm-jwks.json' "$unit" || fail 'restd JWKS mount is missing'
        ! grep -q 'mariadb-slurm.password' "$unit" || fail 'restd received a database secret'
        ;;
    compute-master)
        grep -Fxq 'PodmanArgs=--privileged --cgroupns=host' "$unit" ||
            fail 'master is missing the trusted compute-host privilege contract'
        grep -Fxq 'Volume=/sys/fs/cgroup:/sys/fs/cgroup:rw' "$unit" ||
            fail 'master is missing host cgroup v2 access'
        grep -q '/run/finsim/credentials:/run/credentials:ro' "$unit" ||
            fail 'master credential mount is missing'
        grep -q '/run/secrets/service-ca-bundle.pem:ro' "$unit" ||
            fail 'master service CA bundle is missing'
        ;;
    compute-worker)
        grep -Fxq 'PodmanArgs=--privileged --cgroupns=host' "$unit" ||
            fail 'worker is missing the trusted compute-host privilege contract'
        grep -Fxq 'Volume=/sys/fs/cgroup:/sys/fs/cgroup:rw' "$unit" ||
            fail 'worker is missing host cgroup v2 access'
        ! grep -q '/run/finsim/credentials:/run/credentials' "$unit" ||
            fail 'worker received a Slurm bearer-token directory'
        grep -q '/run/secrets/service-ca-bundle.pem:ro' "$unit" ||
            fail 'worker service CA bundle is missing'
        ;;
    application)
        grep -Eq '^Exec=/finsim-app/releases/.+/(FinSimProxy|FinSimCache)/' "$unit" ||
            fail 'application executable is missing or unversioned'
        grep -Fxq 'Environment=APPLICATION_TLS_CA_FILE=/run/secrets/service-ca-bundle.pem' "$unit" ||
            fail 'application service CA is missing'
        grep -q '/application-role-key.pem:/run/secrets/application-role-key.pem:ro' "$unit" ||
            fail 'application TLS key mount is missing'
        grep -Eq '^HealthCmd=/usr/bin/curl .*--cacert /run/secrets/service-ca-bundle.pem https://[^/]+:(8088|8090)/health/ready$' "$unit" ||
            fail 'application HTTPS readiness check is missing'
        ;;
    *) fail "unsupported rendered role: $role" ;;
esac

if [[ $role == compute-master || $role == compute-worker ]]; then
    ! grep -q 'NoNewPrivileges=true' "$unit" ||
        fail 'trusted compute unit cannot claim no-new-privileges isolation'
    ! grep -q '^DropCapability=all$' "$unit" ||
        fail 'trusted compute unit cannot claim a dropped capability boundary'
    grep -Fq '/etc/finsim/config:/etc/finsim-cluster:ro' "$unit" ||
        fail 'compute configuration mount is not read-only'
    grep -Fq '/srv/finsim/apps:/finsim-app:ro' "$unit" ||
        fail 'compute application mount is not read-only'
    ! grep -Eq 'mariadb-slurm\.password|slurm-jwks\.json|lifecycle-client-key|/var/spool/slurmctld' "$unit" ||
        fail 'compute unit received a control-plane secret or state mount'
else
    grep -Fxq 'NoNewPrivileges=true' "$unit" || fail 'no-new-privileges is missing'
    grep -Fxq 'DropCapability=all' "$unit" || fail 'default capabilities are not dropped'
    ! grep -Eq -- '--privileged|--cgroupns=host|^Volume=/sys/fs/cgroup:' "$unit" ||
        fail 'non-compute role received trusted compute-host privileges'
fi

printf 'Production unit is structurally valid: %s\n' "$unit"
