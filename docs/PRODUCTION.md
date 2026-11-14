# Production deployment

## Host model

Pin `finsim-runtime` by digest. Each compute host runs one rootful Podman
container under systemd/Quadlet with host cgroup v2 access. Do not use Compose
as a 2,000-node scheduler. Render each role through
`scripts/render-production-unit.sh`; never run `envsubst` against its internal
template. The renderer provides role-filtered mounts and a non-secret
environment example.

Generated units require a pre-created named Podman network that provides
routable addresses across hosts. This can be an operator-managed
macvlan/ipvlan or an approved network plugin; a default host-local bridge is
not sufficient. Provide DNS/service discovery separately. Non-compute units
reject host networking, host namespaces and privileged mode. Compute units are
explicit trusted host agents: they use privileged Podman and the host cgroup
namespace on dedicated nodes so root `slurmd` can enforce cgroup devices and
job mount namespaces. Treat their compromise as host compromise.

Use separate failure domains for primary/backup controllers and slurmdbd.
Controllers share a strongly consistent `StateSaveLocation`; slurmdbd members
connect to the same HA MariaDB endpoint. Database members have separate data
volumes and database-layer replication. Run multiple stateless REST/proxy
instances behind internal TLS endpoints.

## Production contract and rationale

Production is not the local Compose topology with larger limits. It is a
different host and security contract built from the same `finsim-runtime`
image. The image supplies the programs and common entrypoint; the deployment
supplies role-specific configuration, credentials, mounts, networking and
privilege boundaries.

The design uses rootful Podman for system-level production Quadlets so systemd
can supervise the services and the host can provide the required storage,
network and cgroup integration. Rootful Podman does not mean every container is
privileged. Database, accounting, controller, REST and application roles drop
capabilities and use `NoNewPrivileges`; only the compute roles receive the
trusted host-agent profile.

Compute containers are the exception because `slurmd` must launch allocations
and enforce job-level CPU, memory, process, device and mount-namespace policy.
They run on dedicated nodes with rootful privileged Podman, the host cgroup
namespace and a writable host cgroup v2 mount. Treat a compromised compute
container as a compromised host. This boundary is deliberately not extended
to controller, database, REST, proxy or cache roles.

Simulation makes a different trade-off. It runs the same runtime image and
role entrypoint under a rootless-friendly profile, but disables Slurm cgroup
enforcement and uses Linux process tracking. It can test scheduling,
authentication, accounting and application workflows; it cannot prove
production job resource isolation, device filtering or mount-namespace
containment.

## Settings and source of truth

The production configuration is assembled through four layers:

```text
Containerfile and runtime scripts
        ↓ build the immutable image
Quadlet template + role renderer
        ↓ produce one systemd/Podman unit per role
prepare-config.sh
        ↓ produces the rendered Slurm configuration bundle
cluster-entrypoint.sh
        ↓ validates inputs, stages secrets and starts the selected daemon
```

Do not edit generated units or generated configuration as a normal operating
procedure. Change the source template or renderer, regenerate, validate, and
review the resulting diff.

### Image and common runtime

The [Containerfile](../Containerfile) creates the published `finsim-runtime`
image. Its runtime stage installs all Slurm daemons and clients, MariaDB and
the common scripts. The image declares the common [entrypoint](../Containerfile)
and health check, but does not contain active production secrets or mutable
deployment configuration. Those are supplied at startup.

The entrypoint is a root bootstrap boundary. It validates read-only inputs,
stages secrets into `/run`, renders role-local runtime files and then drops to
the fixed service identity for controller, accounting, REST, MUNGE, MariaDB
and application daemons. `slurmd` intentionally remains root because it must
enter the submitting identity and manage job resources. See
[cluster-entrypoint.sh](../scripts/cluster-entrypoint.sh) and the fixed
identity definitions in [configure-runtime.sh](../scripts/configure-runtime.sh).

### Quadlet and Podman settings

The common [Quadlet template](../deploy/quadlet/finsim-role.container.in)
defines settings shared by every generated production role:

| Setting | Meaning | Production rationale |
| --- | --- | --- |
| `Image=@IMAGE@` | Uses the selected image reference. | The renderer requires a fully qualified digest-pinned image, making promotion reproducible and preventing an unnoticed tag change. |
| `Network=@NETWORK@` | Attaches the container to a named routable network. | Distributed controllers, `slurmdbd`, database and compute nodes need stable cross-host reachability; host networking is rejected. |
| `ReadOnly=true` | Makes the image filesystem read-only. | Services cannot mutate the shipped runtime or install software at runtime. Writable state is supplied only through explicit mounts and tmpfs. |
| `ReadOnlyTmpfs=true` | Allows suitable temporary paths to use temporary filesystems. | Runtime scratch data does not become persistent image or host state. |
| `CgroupsMode=enabled` | Podman creates and manages a cgroup for the container. | Systemd and Podman can supervise and account for the container. This is distinct from Slurm's job-level `CgroupPlugin`. |
| `LogDriver=journald` | Sends container logs to the system journal. | Production operators get host-level log retention, filtering and collection without relying on a writable container filesystem. |
| `HealthCmd` and health timing | Runs a bounded role-local health check. | A service is considered ready only after its process and role-specific endpoint/state respond successfully. |
| `PidsLimit=4096` | Limits the number of processes in the container. | Prevents an accidental or malicious process explosion from exhausting the host. |
| `Delegate=yes` | Delegates the systemd cgroup subtree to the container manager. | Required for the compute service to create and manage its delegated child cgroups under systemd's ownership model. |
| `Restart=on-failure` | Restarts a failed system service. | Handles process failure while leaving persistent state and escalation to operators. It is not a substitute for HA or graceful Slurm draining. |

The renderer in [render-production-unit.sh](../scripts/render-production-unit.sh)
adds settings by role rather than accepting arbitrary caller-provided options.
This allow-list prevents a controller or application unit from accidentally
receiving the compute host-agent profile.

For `compute-master` and `compute-worker`, the renderer adds:

```text
PodmanArgs=--privileged --cgroupns=host
Volume=/sys/fs/cgroup:/sys/fs/cgroup:rw
```

These settings have separate purposes:

- `--privileged` supplies the broad capability and device access needed by the
  trusted compute agent. It is intentionally limited to dedicated compute
  hosts.
- `--cgroupns=host` exposes the host cgroup namespace rather than a private
  container cgroup namespace.
- The writable `/sys/fs/cgroup` mount lets `slurmd` interact with the host
  cgroup v2 hierarchy after systemd delegates the service subtree.

For every non-compute production role, the renderer instead adds:

```text
NoNewPrivileges=true
DropCapability=all
```

Role-specific capabilities are then added only where the daemon needs them,
for example `CHOWN`, `SETGID` and `SETUID` for controlled startup and identity
transitions. Non-compute roles do not receive `--privileged`, the host cgroup
namespace or a writable host cgroup mount.

### Slurm configuration

[prepare-config.sh](../scripts/prepare-config.sh) renders the templates in
`config/` into a validated configuration bundle. Production selects:

```text
ProctrackType=proctrack/cgroup
TaskPlugin=task/cgroup,task/affinity
JobAcctGatherType=jobacct_gather/cgroup
CgroupPlugin=cgroup/v2
ConstrainCores=yes
ConstrainRAMSpace=yes
ConstrainSwapSpace=yes
ConstrainDevices=yes
```

These settings make Slurm enforce allocation boundaries rather than merely
recording which processes were started. The compute host must therefore expose
the delegated cgroup v2 hierarchy and `slurmd` must remain root. Production
startup rejects a remapped user namespace, private cgroup namespace, missing
cgroup v2 hierarchy or non-writable delegation.

Production also requires:

- primary and backup controller and accounting endpoints;
- MUNGE authentication for daemon communication;
- RS256 JWKS trust for Slurm JWT client identities;
- TLS identities for Slurm control-plane connections and service endpoints;
- explicit `DisableRootJobs` and accounting rules so FinSim payloads run as
  UID/GID 64032 rather than as root;
- validated node registration and, when used, a production cloud inventory.

The templates and validators are the sources of truth: [slurm.conf.in](../config/slurm.conf.in),
[cgroup.conf.in](../config/cgroup.conf.in),
[prepare-config.sh](../scripts/prepare-config.sh),
[validate-production-config.sh](../scripts/validate-production-config.sh),
and the runtime checks in [cluster-entrypoint.sh](../scripts/cluster-entrypoint.sh).

### Mounts, identities and secrets

The renderer mounts only the files and directories required by each role. The
image is shared, but the credential view is not:

- controller and accounting roles receive their role-specific Slurm TLS and
  JWKS inputs;
- compute roles receive the MUNGE socket, Slurm TLS identity, application tree,
  workflow data and only the credentials needed for their node role;
- application roles receive role-filtered application certificates and
  short-lived credentials;
- database and Slurm state use dedicated persistent paths;
- application releases are read-only and selected by immutable release ID;
- private issuer keys never enter Slurm containers.

The rationale is least privilege and recoverability: a service should have
only the secrets and writable state required for its role, while state that
must survive replacement is stored outside the image on tested storage.

## Production versus simulation

Both profiles use the same runtime image, entrypoint and role model. They
differ where production needs host integration, stronger trust, or HA-oriented
inputs, while simulation needs portability and fast local execution.

| Area | Production | Simulation | Why they differ |
| --- | --- | --- | --- |
| Image | Digest-pinned `finsim-runtime`; build once, scan, sign and promote. | Local tagged runtime image. | Production must be reproducible and support rollback; local development optimizes iteration. |
| Host manager | System-level rootful Podman/Quadlet under systemd. | Rootless-friendly Podman Compose. | Production needs system boot integration, delegated cgroups and host service supervision; simulation must work without host administration. |
| Compute Podman profile | Rootful, `--privileged`, `--cgroupns=host`, writable host cgroup v2 mount. | No host cgroup namespace or privileged compute profile. | Production `slurmd` enforces job isolation; simulation intentionally does not claim that boundary. |
| Podman cgroups | `CgroupsMode=enabled` for every generated production unit. | Rootless-compatible local settings. | Production needs container grouping and service accounting; local operation must remain portable. |
| Slurm process tracking | `proctrack/cgroup`. | `proctrack/linuxproc`. | Production tracks and constrains complete job process trees; simulation avoids unavailable host cgroup control. |
| Slurm task plugin | `task/cgroup,task/affinity`. | `task/none`. | Production applies resource and CPU affinity policy; simulation tests workflows without host enforcement. |
| Job accounting | `job_acct_gather/cgroup`. | `job_acct_gather/linux`. | Production accounting must reflect cgroup-contained usage; simulation uses portable process-based accounting. |
| Slurm cgroup plugin | `cgroup/v2` with CPU, RAM, swap and device constraints. | `disabled`. | Rootless simulation cannot honestly promise production isolation. The entrypoint rejects a non-disabled plugin in simulation. |
| User namespace | Compute roles must not be remapped; payloads still run as fixed FinSim identity. | Rootless/remapped namespace is allowed. | Production `slurmd` needs host cgroup and identity control; simulation prioritizes safety and portability. |
| Privileges | Non-compute roles drop capabilities; compute roles are trusted host agents. | Rootless user namespace and local capability contract. | Production isolates ordinary services while granting only compute the host access it requires. |
| Authentication | MUNGE plus RS256 JWKS, fixed issuer-controlled identity claims. | MUNGE plus development HS256 JWT trust. | Production requires external issuer trust and stronger key separation; simulation needs disposable local credentials. |
| TLS | Required for Slurm and application service identities. | Optional or local-profile behavior. | Production traffic crosses hosts and failure domains; simulation is a controlled local topology. |
| Controllers and accounting | Primary/backup endpoints and shared/replicated state requirements. | Single-host service names and local state. | Production removes single-host failure assumptions. |
| Node registration | Explicit dynamic, future or static registration with validated inventory. | Local dynamic registration and bounded node/job defaults. | Production node identity and elasticity must be auditable; simulation needs convenient disposable nodes. |
| Networking | Operator-managed routable named network; host networking forbidden. | Local Compose network. | Production spans hosts and needs deliberate addressing and DNS/service discovery. |
| Storage | Dedicated database, controller, compute-spool and shared application/workflow storage with restore tests. | Git-ignored `.runtime/` state and local mounts. | Production state must survive host replacement and be recoverable. |
| Health checks | Role-local checks plus systemd/Quadlet supervision and restart policy. | Same common checks where useful, plus Compose dependencies. | Production needs host-integrated lifecycle management; simulation needs fast local orchestration. |

The most important difference is not that production runs a different binary.
It is that production makes stronger claims: jobs are contained, identities
are externally trusted, secrets are role-filtered, state is recoverable, and
compute nodes are deliberately integrated with the host kernel. Simulation
must not be described as proving those claims.

## Production configuration flow

Use this order when preparing a deployment:

1. Build and promote a digest-pinned `finsim-runtime` image.
2. Render the production Slurm bundle with
   `scripts/prepare-config.sh --profile production`.
3. Run `scripts/validate-production-config.sh` against the generated bundle.
4. Render one Quadlet unit per role with
   `scripts/render-production-unit.sh`.
5. Review the generated unit, environment example, mounts, capabilities,
   network and health command for the specific role.
6. Install the unit under systemd/Quadlet on the intended host.
7. Start the service and confirm the entrypoint validation, health status,
   cgroup delegation, Slurm registration and role-specific readiness.
8. For compute changes, drain nodes and use the target-host acceptance gate
   before admitting jobs.

The renderer and entrypoint deliberately fail closed when a production input
is incomplete or when a role receives an unsafe combination of settings.

## Required production inputs

Render configuration with:

```bash
FINSIM_CONTROLLER_PRIMARY=controller-primary \
FINSIM_CONTROLLER_BACKUP=controller-backup \
FINSIM_SLURMDBD_PRIMARY=slurmdbd-primary \
FINSIM_SLURMDBD_BACKUP=slurmdbd-backup \
FINSIM_MAX_NODES=4096 \
scripts/prepare-config.sh --profile production --output-dir /etc/finsim/config
scripts/validate-production-config.sh /etc/finsim/config
```

Production startup additionally requires role certificates/keys, CA trust and
RS256 JWKS trust. Private issuer keys never enter the Slurm containers. MUNGE,
TLS and storage credentials are projected at the documented paths with the
fixed numeric ownership required by their consumers.

With `use_jwt_client_ids_only`, the issuer must fix the complete `finsim`
identity: `preferred_username`, UID/GID 64032 and a matching nested `id` object
with `name`, `gecos`, `dir`, `shell`, and exactly one trusted `groups` mapping
or numeric `gids` array. The issuer is a cluster-root trust boundary; callers
must never select these claims. Use a Slurm-specific RS256 key/JWKS and
short-lived, audience-constrained tokens.

Proxy and demonstration cache roles serve HTTPS directly with role-filtered
PEM identities. The public service CA bundle is also projected to compute
nodes and propagated through Slurm job environments; server private keys never
enter a job allocation.

The exact host layout, ownership modes, role variables and render commands are
in [USER_GUIDE.md](USER_GUIDE.md).

## Compute isolation and elasticity

Production compute startup rejects a remapped user namespace, non-cgroup-v2
host, private cgroup namespace or non-writable delegated hierarchy. It creates
and removes a probe child cgroup before starting. `slurmd` is root;
`DisableRootJobs` and accounting ensure FinSim payloads are UID 64032.

Provisioning assigns a stable node name, mounts config/secrets/app/data paths,
and starts one `compute-master` or `compute-worker` role. Dynamic registration
uses `slurmd -Z`; predeclared FUTURE and cloud inventories use the explicit
`future` and `static` modes. Removal drains the node, waits for allocations and
reservations, stops the container, then deletes its dynamic record. Cloud API
calls belong behind the external mTLS lifecycle service.

Optional `FINSIM_LIFECYCLE_ENABLE=1` rendering connects the installed bounded
resume/suspend clients to Slurm. It requires a validated `State=CLOUD`
inventory so queued jobs have nodes that can trigger resume. It does not
implement the lifecycle service or cloud adapter.

## Storage

Mount the same paths used locally. `/finsim-app` is a distributed read-only
release tree or verified node-local cache; `/finsim-data` is shared workflow
storage or the implementation behind the same application interface. MariaDB
uses dedicated block storage, Slurmd spool is node-local, and controller state
is HA shared storage. Test restore procedures, not only backups.

The repository database role enables server TLS in the production profile but
remains one MariaDB process, not an HA database. Large clusters should use a
separately operated replicated MariaDB service.

## Promotion gates

Build once, record SBOM/provenance, scan and sign the runtime digest, test that
digest in the local production-parity profile, then promote without rebuilding.
Roll control-plane services according to Slurm upgrade guidance and drain
compute nodes in batches. Retain the prior runtime digest, configuration,
controller state and database backup for rollback.

Primary upstream references:

- <https://slurm.schedmd.com/quickstart_admin.html>
- <https://slurm.schedmd.com/configless_slurm.html>
- <https://slurm.schedmd.com/cgroup_v2.html>
- <https://slurm.schedmd.com/rest.html>
- <https://slurm.schedmd.com/jwt.html>
- <https://slurm.schedmd.com/big_sys.html>
- <https://slurm.schedmd.com/dynamic_nodes.html>
- <https://slurm.schedmd.com/power_save.html>
- <https://docs.podman.io/en/latest/markdown/podman-systemd.unit.5.html>

Complete every launch gate in [REVIEW.md](REVIEW.md). Local acceptance does not
validate production networking, PKI, identity, storage, lifecycle, monitoring
or disaster recovery.
