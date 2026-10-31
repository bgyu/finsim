# Dependency provenance

The base is Red Hat UBI 10.2. The build scripts use signed public UBI
BaseOS/AppStream packages, signed EPEL 10 and command-scoped CentOS Stream 10
BaseOS/AppStream/CRB packages where the public UBI subset lacks build or
runtime dependencies. Repository setup and key verification are centralized in
`scripts/lib/build-common.sh`.

## Repository inventory

| Repository | How it is enabled | Purpose |
|---|---|---|
| UBI 10 BaseOS | enabled by the UBI image | core runtime and build utilities |
| UBI 10 AppStream | enabled by the UBI image | language runtimes and application libraries |
| EPEL 10 | checksum-pinned `epel-release-latest-10.noarch.rpm` | dependencies not present in public UBI |
| CentOS Stream 10 BaseOS | command-scoped `--repofrompath` | missing build/runtime dependencies |
| CentOS Stream 10 AppStream | command-scoped `--repofrompath` | newer headers and application libraries |
| CentOS Stream 10 CRB | command-scoped `--repofrompath` | development libraries used by Slurm plugins |

CentOS repository signing trust is imported from the checksum-pinned official
key. Stream repositories have lower DNF priority and are passed only to the
commands that need them. No RHEL subscription is required, but an image
containing EPEL/Stream content is not a pure Red Hat-supported package set.
Review support and redistribution policy before external publication.

## Directly requested packages

Repository bootstrap installs `dnf-plugins-core`, `curl`, `ca-certificates` and
the verified EPEL release package.

The RPM builder directly requests:

```text
rpm-build rpmdevtools tar xz bzip2 gzip procps-ng
dbus-devel json-c-devel kernel-headers libbpf-devel libjwt-devel
libyaml-devel llhttp-devel mariadb-connector-c-devel s2n-tls-devel
```

It also runs `dnf builddep` against the upstream MUNGE and Slurm spec files.
Those spec-controlled dependencies can change when a pinned source version is
updated and must be captured from the completed build transaction.

The runtime directly installs the locally built packages:

```text
munge munge-libs slurm slurm-slurmctld slurm-slurmd
slurm-slurmdbd slurm-slurmrestd
```

and repository packages:

```text
aspnetcore-runtime-10.0 bash ca-certificates curl hostname iproute
openssl procps-ng python3 python3-PyMySQL shadow-utils util-linux
mariadb-server
```

The development layer additionally installs:

```text
dotnet-sdk-10.0 git jq findutils
```

DNF installs transitive dependencies selected for the target architecture and
repository snapshot. Therefore this document is explanatory, not the
authoritative package inventory for a particular digest.

`scripts/build-rpms.sh` downloads checksum-pinned MUNGE and Slurm sources,
builds RPM/SRPM artifacts and writes a SHA-256 manifest under `/var/packages`.
`scripts/install-runtime.sh` verifies that manifest and installs MUNGE,
`slurm`, `slurmctld`, `slurmd`, `slurmdbd`, `slurmrestd`, MariaDB server/client,
ASP.NET 10 runtime and required libraries into `finsim-runtime`.

MariaDB is not a Slurm plugin. `slurmdbd` uses Slurm's
`accounting_storage/mysql` plugin to connect to the independent MariaDB server.
Locally its persistent data is `.runtime/state/mariadb`, mounted at
`/var/lib/mysql`. Production supplies a replicated database endpoint and
separate persistent volume per database member.

The universal image intentionally includes packages unused by some roles.
This increases the per-node package surface but produces one artifact to
inventory, scan, sign and patch. Network, mount, capability and role controls
still prevent unused daemons from starting or accessing unrelated credentials.

Inspect a built image without relying on this document:

```bash
podman run --rm --entrypoint rpm localhost/finsim-runtime:latest -qa
podman run --rm --user 0 --entrypoint dnf localhost/finsim-runtime:latest \
  history list
podman run --rm --entrypoint rpm localhost/finsim-runtime:latest \
  -qa --qf '%{NAME}\t%{VERSION}-%{RELEASE}\t%{VENDOR}\n'
```

Archive that output, `/var/packages/BUILD-MANIFEST`, the source pins, DNF
history, image SBOM and signature with every promoted digest. For regulated or
air-gapped builds, mirror and snapshot all repositories; source checksums alone
do not make changing repository metadata byte-for-byte reproducible.

Primary repository references:

- <https://docs.redhat.com/en/documentation/red_hat_enterprise_linux/10/html/building_running_and_managing_containers/types-of-container-images>
- <https://docs.redhat.com/en/documentation/red_hat_enterprise_linux/10/html/building_running_and_managing_containers/adding-software-to-a-ubi-container>
- <https://docs.fedoraproject.org/en-US/epel/>
- <https://www.centos.org/centos-stream/>
