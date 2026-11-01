# Production compute and elasticity hardening design

Date: 2026-11-01
Status: approved

## Objective

Close the remaining gap between the working FinSim integration cluster and the
provider-neutral production contract. The production artifacts must describe a
compute container that can actually perform Slurm cgroup and mount-namespace
operations, and lifecycle automation must have a scheduler-visible cloud-node
inventory on which queued work can trigger resume requests.

This change preserves the established architecture:

- `finsim-runtime` remains the one runtime image for every role;
- `finsim-dev` remains the developer and CI image;
- every job runs as the dedicated non-root `finsim` identity;
- application interaction with Slurm remains REST-only;
- production remains cloud-neutral; and
- Compose remains a single-host integration environment, not a production
  orchestrator.

## Findings

### Slurm-driven scaling has no node inventory

The production renderer can emit `ResumeProgram` and `SuspendProgram`, but the
resulting `slurm.conf` contains no `NodeName` records. Dynamic `slurmd -Z`
registration works when an external platform starts nodes first, but it cannot
provide Slurm-driven scale-from-zero: Slurm has no powered-down node to select
and therefore no reason to call the resume hook.

### Compute isolation understates the trust boundary

The generated compute Quadlet drops broad privileges while enabling
`proctrack/cgroup`, `task/cgroup`, device constraints and
`job_container/tmpfs`. These plugins require host-integrated cgroup and mount
namespace operations. A generic capability allow-list has not been proven on
the target EL10 kernel, Podman and Slurm combination and is not a production
contract.

`slurmd` is a host-trusted root scheduler agent even without containers. The
honest container equivalent is an explicitly trusted compute-only profile on a
dedicated host. Control-plane and application roles must not inherit that
profile.

### JWT and readiness contracts are incomplete

Production sets `use_jwt_client_ids_only`, but operator documentation does not
define every required POSIX claim or clearly state that the issuer is a
cluster-root trust boundary. Generated application health checks prove only
that a UID-64032 process exists, even though proxy and cache expose HTTPS
readiness endpoints. One guide still describes obsolete loopback HTTP
behavior.

## Selected design

### 1. Two explicit production compute trust classes

The Quadlet renderer will continue generating least-privilege units for
database, accounting, controller, REST and application roles. Only
`compute-master` and `compute-worker` become trusted compute-host agents.

Their generated units will:

- require rootful Podman on a dedicated compute host;
- enter the host cgroup namespace and expose the host cgroup v2 hierarchy;
- use the privilege required for Slurm cgroup, device, mount-namespace and UID
  transition operations;
- retain a read-only image, immutable application mount, role-filtered
  credentials and bounded process count;
- receive no database password, JWT signing key, lifecycle client identity or
  controller state; and
- continue launching payloads as the submitted UID/GID, with
  `DisableRootJobs=YES` as a scheduler backstop.

The documentation will state plainly that compromise of root `slurmd` in this
profile is equivalent to compromise of the dedicated compute host. Production
must isolate that tier with network policy, immutable image promotion, host
admission controls and node replacement rather than in-place repair.

The renderer and validator will make this trust difference visible. Broad
compute privilege must never be accepted on another role.

### 2. Validated cloud inventory input

`prepare-config.sh` will accept an optional production cloud-inventory file.
The input is data, never sourced or evaluated by a shell. The renderer will
permit blank lines, comments and `NodeName=` records only. Every effective
record must declare `State=CLOUD`, a bounded FinSim feature, and explicit CPU
and memory resources. Includes, partitions, controller settings, shell syntax
and arbitrary Slurm keys are rejected.

The validated records will be copied into the rendered bundle and inserted at
a single template marker in `slurm.conf`; the generated manifest will cover
the copied inventory. Enabling lifecycle automation will require a non-empty
cloud inventory. A cloud inventory without lifecycle hooks remains valid for a
site-operated provisioner that manages those static names.

The existing dynamic mode remains the default for externally initiated
elasticity. It supports adding and deleting nodes at runtime without a
predeclared range. The static mode is the normal mode for lifecycle-managed
`State=CLOUD` names. Documentation will distinguish the two paths and forbid
deleting predeclared cloud records with `scontrol delete`.

### 3. Complete JWT issuer contract

Production guidance will require Slurm JWTs to contain:

- `preferred_username`, numeric `uid` and numeric `gid`;
- an `id` object with matching `name`, `gecos`, `dir` and `shell`; and
- exactly one of `groups` or `gids` in the form required by the pinned Slurm
  release.

The issuer must pin the Slurm audience, issuer, validity interval and approved
`finsim` numeric identity. A caller must never choose UID, GID or supplemental
groups. Private signing keys remain outside the cluster image and nodes; only
public RS256 JWKS is distributed. Token files remain short-lived, owner-only,
memory-backed and atomically replaced.

Because the dedicated `finsim` identity can act on other jobs owned by the
same account, this is an application trust boundary, not per-workflow Slurm
authorization. FinSimProxy retains workflow ownership checks for external
callers. Production operators must not treat the shared FinSim Slurm token as a
tenant-isolation mechanism.

### 4. Role-specific HTTPS readiness

The Quadlet renderer will emit HTTPS readiness commands for proxy and cache,
using the configured service CA and the instance hostname. Other roles retain
their role-local daemon checks. The validator will require the application
health command and reject a generic process-only check for production proxy or
cache units.

Documentation will consistently state that production application services
bind routable HTTPS endpoints. A load balancer may add another TLS boundary but
must not silently downgrade the backend to cleartext.

## Error handling and fail-closed behavior

- Lifecycle rendering without a valid, non-empty inventory fails before any
  configuration is published.
- Invalid inventory lines, unsupported features, missing resources, duplicate
  node expressions and group/world-writable inputs are rejected.
- Production compute startup continues to verify non-remapped root and cgroup
  v2. It will additionally verify that it is in the host cgroup namespace and
  can create/remove a delegated probe cgroup before starting `slurmd`.
- Production unit validation rejects trusted-compute settings on every
  non-compute role and rejects compute units missing the complete host-agent
  contract.
- Health checks never print bearer tokens, private keys or response bodies.

## Verification

The implementation is complete only when all of these pass:

1. shell syntax and existing Python/static tests;
2. new positive and negative cloud-inventory tests;
3. lifecycle configuration rejection without inventory;
4. all production role renders and offline validation;
5. target Podman Quadlet generator inspection for the compute unit;
6. managed .NET build and contract tests;
7. Compose rendering;
8. the live REST master/worker acceptance suite; and
9. documentation link, stale-text and clean-worktree checks.

The local rootless cluster cannot prove the trusted production compute profile.
Promotion therefore also requires a disposable EL10 rootful host test that
starts the rendered compute unit, runs a constrained job, proves UID 64032,
verifies cgroup limits and device policy, exercises `/tmp` isolation, drains
the node and removes it cleanly.

## Alternatives rejected

### Documentation-only externalization

Leaving inventory and compute privilege entirely to each deployment would
avoid repository changes, but the supplied lifecycle switch would remain
incomplete and the generated compute unit would continue to overstate its
production readiness.

### Host-native slurmd

Installing `slurmd` directly on every host is conventional and avoids the
container namespace problem, but it breaks the selected same-runtime-image
architecture and introduces a second package/configuration lifecycle.

### Generic least-capability compute profile

A narrow capability profile can be developed for one exact kernel, Podman,
systemd and Slurm build. It is not portable enough to be the default contract
for a cloud-neutral reference architecture. Sites may derive and qualify one
as a later defense-in-depth profile after the trusted compute contract passes.
