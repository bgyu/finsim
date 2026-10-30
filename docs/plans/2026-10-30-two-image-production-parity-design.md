# FinSim two-image production-parity design

**Status:** Approved
**Date:** 2026-10-30

## Objective

FinSim publishes exactly two deployable OCI images:

- `finsim-runtime`: the universal runtime used by every local and production
  service role;
- `finsim-dev`: `FROM finsim-runtime`, adding only build, test, diagnostic and
  local-orchestration tools.

The local cluster and a production cluster of more than 2,000 compute nodes
use the same runtime image digest, entrypoint, identities, configuration
templates, mount paths, REST workflow and application-release layout.  They
differ only in scale and infrastructure implementations such as HA replicas,
storage, networking, PKI and secret delivery.

## Image boundary

An internal, disposable RPM builder stage builds pinned and checksum-verified
MUNGE and Slurm packages.  It is not a published deployment image.

`finsim-runtime` contains all runtime packages required by every role:
MUNGE, all Slurm daemons and clients, MariaDB server/client, the .NET and
ASP.NET runtimes, JWT/TLS support, and minimal startup and diagnostic tools.
The intentionally wider package set means one artifact can be built, scanned,
signed and promoted for the entire cluster.

The runtime image contains no active cluster configuration, credentials,
application binaries or mutable daemon state.  Those inputs are mounted at
runtime.  It contains a role-aware entrypoint which validates those mounts and
executes exactly one allow-listed role without evaluating shell strings.

`finsim-dev` adds the .NET SDK, source/build/test tools, Compose files and
cluster lifecycle helpers.  Cluster daemons still run from `finsim-runtime`
locally; the development image is a non-root developer/CI toolbox and cannot
be promoted as the production artifact.

## Runtime roles

Every container starts as root so it can validate mounts and establish the
runtime environment.  The effective daemon identity follows the daemon's
security model:

| Role | Effective identity | Responsibility |
|---|---|---|
| `database` | `mysql` (27) | MariaDB accounting database |
| `slurmdbd` | `slurm` (64030) | Slurm accounting gateway |
| `controller` | `slurm` (64030) | Slurm scheduling and state |
| `restd` | `slurmrestd` (64031) | Internal Slurm REST endpoint |
| `compute-master` | root daemon, `finsim` jobs | Master/orchestrator partition |
| `compute-worker` | root daemon, `finsim` jobs | Worker partition |
| `token-agent` | `finsim` | Short-lived development/service JWT renewal |
| `application` | `finsim` (64032) | Allow-listed .NET application executable |

MUNGE runs as `munge` (64029) beside roles which require its local socket.
`slurmd` remains root so it can manage cgroup v2 and switch payloads to the
allocated UID.  FinSim master and worker processes always run as the fixed
`finsim` UID/GID 64032.  `DisableRootJobs=YES`, accounting associations and
acceptance tests enforce the workload boundary.

## Configuration lifecycle

No secret or active configuration is generated during image build or daemon
startup.  Operators explicitly run preparation commands before cluster start:

1. render a canonical configuration bundle from validated inputs;
2. generate development secrets, or project production-managed secrets;
3. create state, application, data and log directories with fixed ownership;
4. validate permissions, image/config revisions and required mounts;
5. start the deployment.

`cluster-up.sh` fails closed when preparation is incomplete; it never silently
creates or rotates credentials.

Controllers receive the canonical Slurm configuration as a read-only mount.
Compute roles use Slurm configless mode and retrieve the same configuration
from the configured primary/backup controllers.  This avoids synchronizing
configuration files across thousands of nodes.

## Deployment and storage contract

Both environments use the same container paths:

| Path | Access and purpose |
|---|---|
| `/etc/finsim-cluster` | read-only generated configuration bundle |
| `/run/secrets` | role-filtered, read-only long-lived secret projection |
| `/run/credentials` | memory-backed short-lived credentials |
| `/finsim-app` | immutable, versioned application releases |
| `/finsim-data` | shared workflow inputs, results and reports |
| `/var/lib/mysql` | database-role persistent state only |
| `/var/spool/slurmctld` | controller state, shared by HA controllers |
| `/var/spool/slurmd` | node-local compute spool |
| `/var/log/finsim` | role-specific logs collected by the platform |

Local development implements these contracts with git-ignored host bind
mounts under `.runtime`.  Production maps the same paths to distributed POSIX
storage, persistent block storage, tmpfs credential projections and the
organization's secret delivery mechanism.  MariaDB members never share a
database directory; replication occurs at the database layer.

## Application releases

Application binaries are copied to create-only, immutable releases:

```text
/finsim-app/releases/<release-id>/
```

Each release includes a checksum manifest and is owned by root, not writable
by `finsim`.  Activation atomically updates `/finsim-app/current`, but submitted
jobs record and execute an absolute versioned release path so activation cannot
change an in-flight workflow.  Old releases remain until no queued or running
job references them.

Local development uses a bind mount.  Production uses a shared read-only
filesystem or a verified node-local cache backed by the same canonical release
tree.  No application image rebuild is required for an application iteration.

## Local and production parity

The default local topology is one database, one `slurmdbd`, one controller,
one `slurmrestd`, one proxy/cache pair, one master compute node and four worker
nodes.  All service containers use `finsim-runtime`.

Production uses the same role and mount contracts, with primary/backup
controllers, primary/backup `slurmdbd`, an HA database endpoint, multiple REST
and application instances, a small credential-bearing master partition and
more than 2,000 worker hosts.  Each production compute host runs one rootful
Podman container under systemd/Quadlet with host cgroup v2 access.

Compose is the single-host development scheduler.  Production uses per-host
Quadlet/systemd units rendered from the same environment contract; cloud-
specific provisioning and autoscaling remain adapters outside the image.

## Node lifecycle

The cluster reserves a stable node naming/capacity range.  Provisioning starts
one compute container with an assigned node name, obtains configuration from
the controllers, and registers the node.  Removal first drains the node, waits
for allocations to finish, stops the container and only then terminates the
host.  A generic lifecycle interface isolates provider-specific infrastructure
calls.

## Security and permissions

The deployment uses fixed numeric identities on every host.  Configuration
and application releases are root-owned.  Secret files are readable only by
their consuming daemon.  Workers never receive the JWT signing key, database
password or a Slurm REST credential.  Only master/orchestrator nodes receive a
short-lived `finsim` token through memory-backed credential storage.

Root container administration is allowed and expected.  It does not imply
root workload execution.  Tests submit jobs through Slurm REST and assert that
payload UID/GID are 64032, root submission fails, immutable application paths
cannot be modified, and role mounts do not expose unrelated credentials.

## Verification gates

The implementation is complete only when it proves:

- exactly `finsim-runtime` and `finsim-dev` are published targets;
- all local cluster services use the runtime image;
- startup fails without prepared configuration, secrets and state;
- the five-node local workflow completes through REST and accounting;
- master and worker payloads run as `finsim`, never root;
- a versioned application release is visible on every compute node;
- a new worker can join and a drained worker can be removed at runtime;
- runtime state survives an orderly restart;
- production-parity validation checks rootful cgroup v2 prerequisites;
- no obsolete baked configuration or superseded image target remains.

## Primary references

- Slurm administrator guide: <https://slurm.schedmd.com/quickstart_admin.html>
- Configless Slurm: <https://slurm.schedmd.com/configless_slurm.html>
- Dynamic/cloud nodes: <https://slurm.schedmd.com/dynamic_nodes.html>
- Slurm cgroup v2: <https://slurm.schedmd.com/cgroup_v2.html>
- Slurm REST: <https://slurm.schedmd.com/rest.html>
- Slurm JWT: <https://slurm.schedmd.com/jwt.html>
