# Containerfile Exposed-Port Documentation Design

## Goal

Make the runtime image's network metadata self-documenting by identifying the
service associated with every `EXPOSE` port and the configuration that defines
the actual listener or client connection.

## Design

Keep the existing literal `EXPOSE 3306 6817 6818 6819 6820` instruction. Add a
comment immediately above it that records:

- 3306: MariaDB, configured by `port` in `config/mariadb-server.cnf.in`;
- 6817: `slurmctld`, configured by `SlurmctldPort` in
  `config/slurm.conf.in`;
- 6818: `slurmd`, configured by `SlurmdPort` in `config/slurm.conf.in`;
- 6819: `slurmdbd`, configured by `DbdPort` in
  `config/slurmdbd.conf.template`, with the controller-side destination set by
  `AccountingStoragePort` in `config/slurm.conf.in`; and
- 6820: `slurmrestd`, configured at runtime by `SLURMRESTD_LISTEN`, whose
  default is in `scripts/cluster-entrypoint.sh` and whose deployment-specific
  values are supplied by Compose or the generated production unit.

The comment will also state that `EXPOSE` is image metadata. It neither
publishes a host port nor overrides the role-specific listener configuration.
Each container starts only the service selected by `CONTAINER_ROLE`.

## Alternatives Considered

OCI labels would make the mapping machine-readable but less visible to an
operator reviewing the Containerfile. Build arguments could centralize the
numbers, but would add indirection without making Slurm's rendered
configuration authoritative. An adjacent comment is the smallest and clearest
change.

## Validation

Run `git diff --check`, inspect the changed Containerfile, and verify every
documented source against the configuration templates, entrypoint, Compose
file, and production-unit renderer.
