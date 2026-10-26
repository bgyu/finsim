# Slurm POC provenance

The initial Slurm build, configuration, daemon-entrypoint, health-check, and
lifecycle assets in this repository were adapted on 2026-10-27 from:

- repository: `/home/ben/repos/projects/slurmpoc`;
- commit: `5cb253a` (`docs: define Slurm identity and permission model`); and
- base image contract: `registry.access.redhat.com/ubi10/ubi:10.2`.

The imported implementation pins MUNGE and Slurm source archives by version
and SHA-256, verifies generated RPM manifests before installation, and keeps
simulation and production profiles separate. FinSim replaces the POC's
`workflow` service identity with `finsim` while preserving UID/GID 64032 and
removes the Python workflow broker because FinSimProxy and the master call
`slurmrestd` directly.

See the source repository's `docs/operations/dependency-provenance.md` for the
original package-source audit. FinSim will carry its own updated copy before a
production release.
