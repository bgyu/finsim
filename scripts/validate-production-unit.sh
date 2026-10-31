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
grep -Fxq 'NoNewPrivileges=true' "$unit" || fail 'no-new-privileges is missing'
grep -Fxq 'DropCapability=all' "$unit" || fail 'default capabilities are not dropped'
grep -Fxq 'Delegate=yes' "$unit" || fail 'systemd cgroup delegation is missing'
! grep -Eq '@[A-Z_]+@' "$unit" || fail 'unit contains unresolved template markers'
! grep -Eq '^Network=host$|^UserNS=host$|SYS_ADMIN|--privileged' "$unit" ||
    fail 'unit requests a prohibited broad privilege'
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
        grep -q '/run/finsim/credentials:/run/credentials:ro' "$unit" ||
            fail 'master credential mount is missing'
        grep -q '/run/secrets/service-ca-bundle.pem:ro' "$unit" ||
            fail 'master service CA bundle is missing'
        ;;
    compute-worker)
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
        ;;
    *) fail "unsupported rendered role: $role" ;;
esac

printf 'Production unit is structurally valid: %s\n' "$unit"
