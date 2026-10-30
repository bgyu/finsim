# Dependency provenance

The base is Red Hat UBI 10.2. The build scripts use signed public UBI
BaseOS/AppStream packages, signed EPEL 10 and command-scoped CentOS Stream 10
BaseOS/AppStream/CRB packages where the public UBI subset lacks build or
runtime dependencies. Repository setup and key verification are centralized in
`scripts/lib/build-common.sh`.

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
```
