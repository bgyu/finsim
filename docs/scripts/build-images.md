# `build-images.sh`

**Tags:** `build`, `shared`

Source: [scripts/build-images.sh](../../scripts/build-images.sh)

## Purpose

Builds the two published image targets from [Containerfile](../../Containerfile):
`finsim-runtime` and `finsim-dev`. The internal `rpm-builder` stage is used as
an input stage; it is not published by this script.

## Caller and call chain

```text
operator / CI
    ↓
build-images.sh
    ├── podman build --target finsim-runtime
    │     └── rpm-builder stage as dependency
    └── podman build --target finsim-dev
          └── finsim-runtime stage as dependency
```

The script changes to the repository root before invoking Podman. It does not
start containers or the cluster.

## Workflow

1. Enable strict Bash error handling.
2. Resolve the repository directory from the script location.
3. Read `FINSIM_IMAGE_TAG`, defaulting to `latest`.
4. Reject empty tags and characters outside letters, digits, `_`, `.` and `-`.
5. Build the runtime target with a local image name.
6. Build the development target with the same tag.

## Definitions

This script has no user-defined functions. Its important definitions are the
`project_dir`, `tag` and shared Podman argument array. `set -Eeuo pipefail`
makes a failed build stop the script instead of allowing the second build to
mask the failure.

## Outputs and failure behavior

With the default tag, the outputs are:

```text
localhost/finsim-runtime:latest
localhost/finsim-dev:latest
```

The command exits non-zero if Podman cannot pull the base image, build the RPM
stage, execute a Containerfile instruction or tag the result. It does not
delete previous images.
