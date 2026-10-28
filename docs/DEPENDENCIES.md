# Dependency and repository provenance

## Why UBI alone is insufficient

UBI is a redistributable subset of RHEL, not the complete RHEL package set.
UBI BaseOS/AppStream provide the base runtime and .NET packages, but several
Slurm build dependencies (`libjwt-devel`, `llhttp-devel`, `s2n-tls-devel`,
`mariadb-connector-c-devel`, and related packages) are not all available from
the enabled UBI repositories. A UBI image does not magically inherit a host's
RHEL subscription.

Red Hat documents the UBI repository scope and redistribution model in
[Types of container images](https://docs.redhat.com/en/documentation/red_hat_enterprise_linux/10/html/building_running_and_managing_containers/types-of-container-images)
and [UBI images, repositories, packages, and source code](https://access.redhat.com/articles/4238681).

## What this repository does

The public, subscription-free build path is explicit in
`scripts/lib/build-common.sh`:

| Content | Source | Used where |
|---|---|---|
| BaseOS/AppStream and .NET 10 | UBI 10.2 repositories | Builder and runtime |
| General EL10 build dependencies | signed EPEL 10 | Builder; selected runtime dependencies |
| Missing development/runtime libraries | signed CentOS Stream 10 BaseOS/AppStream/CRB, command-scoped | Builder and dependency resolution |
| MUNGE source | official `dun/munge` GitHub release | RPM build |
| Slurm source | `download.schedmd.com` | RPM build |

The EPEL release RPM, CentOS signing key, MUNGE archive, and Slurm archive are
pinned by SHA-256 in `build/versions.conf`. Source override requires a version
and digest pair. Repository GPG checking stays enabled. The builder emits both
RPMs and SRPMs plus `/var/packages/BUILD-MANIFEST`, which records source pins
and the digest of every artifact. `install-runtime.sh` refuses missing, extra,
duplicate, symlinked, changed, or differently pinned packages.

The Stream repository arguments are scoped to individual DNF invocations;
they are not written as permanently enabled repository files. A newer Stream
`kernel-headers` is installed only in the disposable build stage because the
current `libbpf-devel` dependency requires it.

## Package phases

`build-rpms.sh` installs build tooling, lets the upstream spec files declare
their normal BuildRequires, and explicitly adds the optional libraries needed
for the selected Slurm plugins. It builds MUNGE with checks, installs those
fresh RPMs into the builder, then builds Slurm with cgroup v2, JWT,
`slurmrestd`, and YAML enabled.

`install-runtime.sh` selects only:

```text
munge, munge-libs, slurm, slurm-slurmctld, slurm-slurmd,
slurm-slurmdbd, slurm-slurmrestd
```

It then installs ASP.NET Core 10 and runtime libraries and verifies that the
expected executables and plugins load without unresolved libraries. MariaDB
server is opt-in and is added only by the `finsim-sim` target.

## Production supply-chain policy

The public path is reproducible and useful for development/CI, but mixing UBI,
EPEL, and CentOS Stream is not the same as a fully Red Hat-supported RHEL
software stack. A production organization must make an explicit support and
supply-chain decision:

1. run the same scripts on a subscribed RHEL 10.2 builder using approved RHEL
   repositories plus an approved source for dependencies that RHEL does not
   ship; or
2. snapshot the exact public repositories in an internal, signed, immutable
   mirror and treat the resulting stack as organization-supported; or
3. consume an internally supported Slurm RPM set and retain the manifest
   verification/install/configuration phases.

Do not pass subscription certificates as normal build arguments, copy them
into layers, or commit them. If an entitled container build is used, provide
entitlements through the build engine's secret-mount mechanism and confirm
they are absent from every layer. CI should additionally generate an SBOM,
scan OS/NuGet artifacts, sign the image digest, attach provenance/manifest,
and promote the exact tested digest rather than rebuilding in production.

`NuGet.Config` and `packages.lock.json` make managed restore locked and
repeatable. The SDK is present only in build/dev stages. The application image
contains the ASP.NET runtime and published output, not the SDK, source, test
assets, or package-manager cache.

## Upgrade procedure

1. Choose upstream releases; never use an unpinned `latest` source archive.
2. Verify release provenance and calculate SHA-256 out of band.
3. Change `build/versions.conf` and matching Containerfile defaults in one
   reviewed commit.
4. Build from an empty package output and preserve RPMs, SRPMs, manifest, SBOM,
   scan results, and image digest.
5. Run managed tests, Python tests, and the full acceptance test.
6. Test controller/database compatibility and rolling node behavior in a
   non-production cluster.
7. Promote the signed digest through environments; keep the prior digest and
   database/controller backups for rollback.
