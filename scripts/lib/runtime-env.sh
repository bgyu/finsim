#!/usr/bin/env bash
# Parse the generated local Compose environment as data, never as shell code.
# The file is intentionally limited to the three settings consumed by local
# tooling. Production deployments use role-specific environment files instead.

runtime_env_error() {
    printf 'runtime-env: %s\n' "$*" >&2
    return 78
}

runtime_env_load() {
    local file=$1 line key value mode permissions
    local image_tag= app_release= proxy_port=
    local seen_image=0 seen_release=0 seen_port=0

    [[ -f $file && ! -L $file && -r $file ]] ||
        runtime_env_error "not a readable regular file: $file" || return
    mode=$(stat --format='%a' -- "$file") || return
    [[ $mode =~ ^[0-7]{3,4}$ ]] || runtime_env_error 'invalid file mode' || return
    permissions=$((8#$mode))
    (((permissions & 0077) == 0)) ||
        runtime_env_error 'file must not be accessible by group or other' || return

    while IFS= read -r line || [[ -n $line ]]; do
        [[ -n $line && $line != *$'\r'* && $line == *=* ]] ||
            runtime_env_error 'malformed line' || return
        key=${line%%=*}
        value=${line#*=}
        case $key in
            FINSIM_IMAGE_TAG)
                ((seen_image++ == 0)) || runtime_env_error "duplicate $key" || return
                image_tag=$value
                ;;
            FINSIM_APP_RELEASE)
                ((seen_release++ == 0)) || runtime_env_error "duplicate $key" || return
                app_release=$value
                ;;
            FINSIM_PROXY_PORT)
                ((seen_port++ == 0)) || runtime_env_error "duplicate $key" || return
                proxy_port=$value
                ;;
            *) runtime_env_error "unknown setting: $key" || return ;;
        esac
    done < "$file"

    [[ $seen_image -eq 1 && $image_tag =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$ ]] ||
        runtime_env_error 'FINSIM_IMAGE_TAG is missing or invalid' || return
    [[ $seen_release -eq 1 && $app_release =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$ ]] ||
        runtime_env_error 'FINSIM_APP_RELEASE is missing or invalid' || return
    [[ $seen_port -eq 1 && $proxy_port =~ ^[0-9]{1,5}$ ]] ||
        runtime_env_error 'FINSIM_PROXY_PORT is missing or invalid' || return
    ((10#$proxy_port >= 1 && 10#$proxy_port <= 65535)) ||
        runtime_env_error 'FINSIM_PROXY_PORT is outside 1-65535' || return

    FINSIM_IMAGE_TAG=$image_tag
    FINSIM_APP_RELEASE=$app_release
    FINSIM_PROXY_PORT=$((10#$proxy_port))
    export FINSIM_IMAGE_TAG FINSIM_APP_RELEASE FINSIM_PROXY_PORT
}
