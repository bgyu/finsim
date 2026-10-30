# Architecture and implementation

## Invariants

Local and production clusters run the same `finsim-runtime` digest, role
entrypoint, users, configuration renderer, mount paths, application releases
and REST workflow. Development changes scale and infrastructure backends, not
runtime behavior. `finsim-dev` is never a daemon image.

The internal `rpm-builder` stage calls `scripts/build-rpms.sh`; the runtime
stage calls `scripts/install-runtime.sh` and `scripts/configure-runtime.sh`.
Those commands remain directly callable on an EL10 build host. Source versions
and SHA-256 values are pinned in `build/versions.conf`.

## Roles

The allow-listed roles are `database`, `slurmdbd`, `controller`, `restd`,
`token-agent`, `compute-master`, `compute-worker`, and `application`. The
entrypoint never evaluates a command string. Application role commands must be
root-owned, non-writable executables below `/finsim-app/releases/` and are
executed as `finsim`.

Fixed identities are MUNGE 64029, Slurm 64030, slurmrestd 64031, FinSim 64032,
MariaDB 27, and developer 1000. Container root may stage read-only inputs into
tmpfs and drop privilege. `slurmd` is the intentional exception and remains
root while job payloads run as UID 64032.

## REST workflow

FinSimProxy authenticates the caller, writes idempotent workflow intent to the
cache interface and submits one master through Slurm REST. The master splits
input into cache keys and submits one throttled worker array through REST.
Workers read chunks, calculate JSON results and write keyed results. The master
validates every result and atomically writes the only final report.

Master nodes receive a short-lived `finsim` Slurm JWT. Worker nodes never mount
that credential. JWT signing material remains on controller/accounting roles.
The local token agent obtains expiring user tokens through authenticated Slurm
RPC; production uses the organization's trusted issuer/JWKS process.

## Configuration and release preparation

`prepare-config.sh` renders `config/slurm.conf.in` and `cgroup.conf.in` into a
digest manifest. Controllers mount the canonical bundle; compute nodes start
with the controller list and use configless Slurm. Production rendering
requires primary/backup controller and slurmdbd names and defaults to capacity
for 4,096 nodes.

`publish-app.sh` runs restore, Release build, managed tests and three publishes
inside `finsim-dev`. It creates a checksum manifest, makes the release
root-owned and non-writable in the container namespace, atomically installs it
and updates `current`. Submissions use the explicit configured release path.

## State

MariaDB, controller and per-node Slurmd state are separate mounts. Application
release storage is read-only to services. Workflow data is writable only to
the FinSim identity. Long-lived secrets are read-only role-filtered mounts;
short-lived tokens use `/run/credentials`.

The complete rationale and verification gates are in the approved design in
`docs/plans/2026-10-30-two-image-production-parity-design.md`.
