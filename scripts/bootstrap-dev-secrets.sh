#!/usr/bin/env bash
# Create disposable local credentials. Production uses an external issuer and
# secret delivery; it never invokes this script or persists signing keys here.
set -Eeuo pipefail
umask 077

project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
runtime_dir="$project_dir/.runtime"
secret_dir="$runtime_dir/secrets"
public_dir="$runtime_dir/public"
container_secret_dir="$runtime_dir/container-secrets"
image_tag=${FINSIM_IMAGE_TAG:-latest}
app_release=${FINSIM_APP_RELEASE:-dev}
proxy_port=${FINSIM_PROXY_PORT:-8088}

install -d -m 0700 "$runtime_dir" "$secret_dir" "$container_secret_dir"
install -d -m 0755 "$public_dir"

die() {
    printf 'bootstrap-dev-secrets: %s\n' "$*" >&2
    exit 1
}

for command in python3 podman; do
    command -v "$command" >/dev/null 2>&1 || die "$command is required"
done

[[ $image_tag =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$ ]] ||
    die 'FINSIM_IMAGE_TAG is invalid'
[[ $app_release =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$ ]] ||
    die 'FINSIM_APP_RELEASE is invalid'
[[ $proxy_port =~ ^[0-9]{1,5}$ ]] &&
    ((10#$proxy_port >= 1 && 10#$proxy_port <= 65535)) ||
    die 'FINSIM_PROXY_PORT must be between 1 and 65535'

crypto_image=${FINSIM_CRYPTO_IMAGE:-localhost/finsim-runtime:$image_tag}
use_container_crypto=0
if ! command -v openssl >/dev/null 2>&1; then
    podman image exists "$crypto_image" ||
        die "openssl is absent and crypto helper image $crypto_image is not built"
    use_container_crypto=1
fi

run_openssl() {
    if [[ $use_container_crypto == 0 ]]; then
        openssl "$@"
    else
        podman run --rm --entrypoint /usr/bin/openssl \
            --volume "$project_dir:$project_dir:Z" \
            --workdir "$project_dir" "$crypto_image" "$@"
    fi
}

run_jwt_tool() {
    if [[ $use_container_crypto == 0 ]]; then
        python3 "$project_dir/scripts/dev-jwt.py" "$@"
    else
        podman run --rm --entrypoint /usr/bin/python3 \
            --volume "$project_dir:$project_dir:Z" \
            --workdir "$project_dir" "$crypto_image" \
            "$project_dir/scripts/dev-jwt.py" "$@"
    fi
}

create_random_file() {
    local destination=$1 bytes=$2 encoding=${3:-raw} temporary
    [[ ! -L $destination ]] || die "refusing symlink target: $destination"
    [[ ! -e $destination ]] || return 0
    temporary=$(mktemp --tmpdir="$secret_dir" '.secret.XXXXXXXX')
    if [[ $encoding == hex ]]; then
        run_openssl rand -hex "$bytes" >"$temporary"
    else
        run_openssl rand "$bytes" >"$temporary"
    fi
    chmod 0600 "$temporary"
    mv -n -- "$temporary" "$destination"
    [[ ! -e $temporary ]] || rm -f -- "$temporary"
}

create_random_file "$secret_dir/munge.key" 1024 raw
create_random_file "$secret_dir/slurm-jwt-hs256.key" 32 raw
create_random_file "$secret_dir/mariadb-slurm.password" 32 hex

private_key="$secret_dir/app-jwt-private.pem"
if [[ ! -e $private_key ]]; then
    temporary=$(mktemp --tmpdir="$secret_dir" '.rsa.XXXXXXXX')
    run_openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 \
        -out "$temporary" 2>/dev/null
    chmod 0600 "$temporary"
    mv -n -- "$temporary" "$private_key"
    [[ ! -e $temporary ]] || rm -f -- "$temporary"
fi
[[ -f $private_key && ! -L $private_key ]] || die 'invalid application JWT key'
chmod 0600 "$private_key"

key_id=finsim-dev-1
issuer=finsim-development
run_jwt_tool jwks \
    --private-key "$private_key" --key-id "$key_id" \
    --output "$public_dir/app-jwks.json"
run_jwt_tool token \
    --private-key "$private_key" --key-id "$key_id" --issuer "$issuer" \
    --audience finsim-proxy --subject local-developer --scope finsim.submit \
    --lifetime 86400 --output "$secret_dir/api-client.jwt"
run_jwt_tool token \
    --private-key "$private_key" --key-id "$key_id" --issuer "$issuer" \
    --audience finsim-cache --subject finsim --scope finsim.cache \
    --lifetime 86400 --output "$secret_dir/cache-client.jwt"

# Batch/proxy processes run as container UID 64032. Make a separate runtime
# copy whose host ownership maps to that UID in the rootless user namespace;
# retain the host-owned source token for safe regeneration.
temporary=$(mktemp --tmpdir="$container_secret_dir" '.cache-jwt.XXXXXXXX')
install -m 0600 "$secret_dir/cache-client.jwt" "$temporary"
mv -f -- "$temporary" "$container_secret_dir/cache-client.jwt"
podman unshare chown 64032:64032 "$container_secret_dir/cache-client.jwt"
podman unshare chmod 0400 "$container_secret_dir/cache-client.jwt"

printf 'FINSIM_IMAGE_TAG=%s\nFINSIM_APP_RELEASE=%s\nFINSIM_PROXY_PORT=%s\n' \
    "$image_tag" "$app_release" "$proxy_port" >"$runtime_dir/cluster.env"
chmod 0600 "$runtime_dir/cluster.env"

printf 'Development credentials are ready in %s (values were not printed).\n' \
    "$runtime_dir"
