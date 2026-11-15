# `build-rpms.sh`

**Tags:** `build`, `shared`, `security`

Source: [scripts/build-rpms.sh](../../scripts/build-rpms.sh)

## Purpose

Builds reproducible MUNGE and Slurm RPM/SRPM artifacts on an EL10 build host.
The script verifies upstream source archives, builds in a disposable directory,
creates a manifest and publishes an atomic artifact set for the runtime image.

## Call chain

```text
Containerfile rpm-builder RUN
    ↓
build-rpms.sh
    ├── lib/build-common.sh
    ├── dnf repository/build-dependency setup
    ├── verified MUNGE download → rpmbuild
    ├── verified Slurm download → rpmbuild with cgroupv2/JWT/REST/YAML
    ├── BUILD-MANIFEST creation and verification
    └── atomic final artifact directory
```

It can also be called manually by an EL10 operator with the same pinned
versions and digest pairs.

## Inputs and outputs

Inputs include `build/versions.conf`, optional version/SHA overrides, an output
directory and a temporary work directory. A version override is accepted only
when its matching SHA-256 override is also supplied.

The output contains RPMs, SRPMs and `BUILD-MANIFEST`. The manifest records
versions, source digests and every artifact digest.

## Workflow

1. Resolve and load pinned versions through `build-common.sh`.
2. Validate paths, version syntax, SHA-256 syntax, root identity, EL10 and
   required tools.
3. If an existing manifest matches exactly, verify it and exit successfully.
4. Refuse to merge into an unknown non-empty output directory.
5. Create mode-0700 temporary build and staging directories.
6. Install build dependencies and repository configuration.
7. Download and verify MUNGE, build it, then install its build RPMs into the
   disposable builder.
8. Download and verify Slurm, install build dependencies, and build the
   cgroup-v2/JWT/REST/YAML feature set.
9. Copy artifacts to a staging directory, write and verify the manifest, then
   atomically publish the result.
10. Remove temporary directories on success or failure.

## Definitions

- `usage`: prints supported options.
- `cleanup`: removes only the script-owned temporary paths and preserves the
  original exit status.
- `build-common.sh` supplies the shared `build_*` validation, download,
  manifest and logging functions.

## Why the safeguards matter

Digest verification prevents an unexpected source archive from entering the
image. Manifest matching makes a repeated build a verified no-op. Staging and
atomic publication prevent a partially written RPM set from being consumed by
`install-runtime.sh`.
