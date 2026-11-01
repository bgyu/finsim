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

Compute registration is explicit. `dynamic` uses supported `slurmd -Z`
self-registration, `future` matches predeclared FUTURE nodes, and `static`
matches precreated static or cloud inventory. The default production unit
example is dynamic. Slurm-driven power saving requires the validated
`cloud-nodes.conf` input, static registration and the external lifecycle
service; lifecycle rendering fails when the scheduler-visible inventory is
absent.

`publish-app.sh` runs restore, Release build, managed tests and three publishes
inside `finsim-dev`. It creates a checksum manifest, makes the release
root-owned and non-writable in the container namespace, atomically installs it
and updates `current`. Submissions use the explicit configured release path.

## State

MariaDB, controller and per-node Slurmd state are separate mounts. Application
release storage is read-only to services. Workflow data is writable only to
the FinSim identity. Long-lived secrets are read-only role-filtered mounts;
short-lived tokens use `/run/credentials`.

Production application roles fail closed unless Kestrel receives a validated
PEM certificate/key and an explicit `https://0.0.0.0:PORT` listener. The key is
a role-filtered, read-only root:finsim 0440 mount, after which the bootstrap
process replaces itself with UID 64032. A public service CA bundle is
mounted at the same path on proxy and compute roles; the proxy places that path
in the master environment and the master propagates it to workers so .NET can
validate cache and Slurm REST certificates without disabling verification.

Production Quadlets are generated with `render-production-unit.sh`. The
renderer requires a digest-pinned image and a deployment-provided routable
network and mounts secrets individually. Database, accounting, controller,
REST and application roles drop the default capability set and prohibit host
namespaces and privileged mode. Compute roles are different: root `slurmd` is
an explicit trusted host agent using privileged Podman, the host cgroup
namespace and the host cgroup v2 hierarchy on a dedicated node. Their
read-only image/application mounts and secret filtering remain enforced, but a
compute compromise is a host compromise. The offline validator prevents this
profile from leaking to another role and target-host acceptance proves job
cgroups and namespace isolation before promotion.

FinSimCache and the repository MariaDB role make integration testable; a large
production deployment normally replaces them with separately operated HA data
services. The interface and mount/credential contracts remain the same.

The complete rationale and verification gates are in the approved design in
`docs/plans/2026-10-30-two-image-production-parity-design.md`.
