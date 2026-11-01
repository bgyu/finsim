# FinSim on Slurm

FinSim is a cloud-neutral reference architecture for REST-driven batch jobs on
Slurm. The calculation is deliberately small; the production artifact is the
operational contract shared by a five-node Podman development cluster and a
distributed cluster with more than 2,000 compute hosts.

Start with the complete [repository user guide](docs/USER_GUIDE.md). It covers
host prerequisites, RPM/image/application compilation, local development,
TeamCity, production deployment, node scaling, upgrades and recovery. The
[implementation review](docs/REVIEW.md) records corrected issues and remaining
production responsibilities.

## Images

Only two images are published:

| Image | Contents | Use |
|---|---|---|
| `finsim-runtime` | UBI 10.2, MUNGE, every Slurm daemon/client, MariaDB, .NET/ASP.NET runtime, role entrypoint | Every local and production service |
| `finsim-dev` | `FROM finsim-runtime`, plus .NET SDK, git, diagnostics, source, tests and lifecycle assets | Non-root developer and TeamCity toolbox |

The RPM builder is an internal Containerfile stage, not a deployable image.
Applications and active configuration are not image layers. They are prepared
before startup and mounted into the runtime.

## Local workflow

```bash
# 1. Build the two images.
scripts/build-images.sh

# 2. Render config, create disposable development secrets and storage, build
#    and publish the immutable "dev" application release. Starts no service.
scripts/prepare-cluster.sh

# 3. Validate prepared inputs again and start the single-host topology.
scripts/cluster-up.sh

# 4. Exercise the REST-only workflow and security/accounting checks.
scripts/submit-simulation.sh --wait
scripts/acceptance-test.sh

# Optional: add and safely remove another dynamic local worker.
scripts/local-node.sh add compute-host-006 worker
scripts/local-node.sh remove compute-host-006 900

# 5. Stop containers without deleting persistent state or releases.
scripts/cluster-down.sh
```

Every Compose service uses `localhost/finsim-runtime:<tag>`. One controller,
one database, one `slurmdbd`, one `slurmrestd`, one token agent, proxy/cache
applications, one master node and four workers run on the development host.

Useful diagnostics:

```bash
podman exec finsim-controller sinfo --Node --long
podman exec finsim-controller squeue
podman exec finsim-controller sacct --starttime today
podman compose --env-file .runtime/cluster.env -f compose.yaml logs restd
```

`podman exec` takes a container name. `podman compose exec` takes a Compose
service name, for example `controller`, not `finsim-controller`.

## Prepared deployment tree

`.runtime/` is git-ignored and contains generated configuration, secrets,
short-lived credentials, immutable application releases, workflow data,
database/controller/compute state and logs. `cluster-up.sh` never generates
these inputs and fails closed when validation fails.

Applications are executed from
`/finsim-app/releases/<release>/...`. Jobs record the versioned path and never
use the mutable `current` link, so an activation cannot change a running job.

## Identity boundary

Containers start as root for administration. `slurmd` remains root so it can
manage cgroups and launch allocations. Controller, accounting, REST, MUNGE and
MariaDB daemons drop to their service identities. All FinSim proxy, cache,
master and worker code runs as UID/GID 64032. `DisableRootJobs=YES` and the
acceptance test enforce this boundary.

Production compute containers are explicit trusted host agents on dedicated
nodes: root `slurmd` receives the host cgroup namespace and privileged compute
access needed by cgroup/device and mount-namespace plugins. No other role gets
that profile or its secrets. Treat compromise of a compute container as host
compromise; the detailed target-host gate is in the production guide.

## Documentation

- [Architecture and implementation](docs/ARCHITECTURE.md)
- [Complete build, deployment and operations guide](docs/USER_GUIDE.md)
- [Exhaustive script reference](docs/SCRIPTS.md)
- [Implementation review and production cautions](docs/REVIEW.md)
- [Local operations](docs/OPERATIONS.md)
- [Production deployment](docs/PRODUCTION.md)
- [Dependency provenance](docs/DEPENDENCIES.md)
- [TeamCity and CI](docs/CI.md)
- [Approved design](docs/plans/2026-10-30-two-image-production-parity-design.md)
- [Implementation plan](docs/plans/2026-10-30-two-image-production-parity-implementation-plan.md)
- [Current compute and elasticity hardening design](docs/plans/2026-11-01-production-compute-elasticity-hardening-design.md)
- [Current compute and elasticity implementation plan](docs/plans/2026-11-01-production-compute-elasticity-hardening-implementation-plan.md)
