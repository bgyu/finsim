# FinSim repository user guide

This guide is the task-oriented manual for compiling, testing, deploying and
operating FinSim. Read [ARCHITECTURE.md](ARCHITECTURE.md) for component design,
[DEPENDENCIES.md](DEPENDENCIES.md) for package provenance and
[REVIEW.md](REVIEW.md) for reviewed limitations and production cautions. The
[exhaustive script reference](SCRIPTS.md) documents every script, its examples,
dependencies, sourced functions and shared variables.

## 1. What this repository provides

The repository provides:

- an internal RPM build stage that compiles checksum-pinned MUNGE and Slurm;
- one `finsim-runtime` image containing every supported cluster role;
- one `finsim-dev` image containing the SDK, source and CI tools;
- three small .NET applications: FinSimProxy, FinSimCache and FinSim;
- a five-node rootless Podman Compose simulation;
- production configuration, Quadlet renderers and lifecycle clients;
- a REST-only master/worker example and acceptance tests.

It does not provide a cloud account, VM provisioner, routable production
container network, certificate authority, OIDC issuer, load balancer, HA
database, shared filesystem, registry, monitoring system or backup service.
Those are deployment-platform dependencies with documented contracts below.

## 2. Repository layout

The tracked source tree is organized as follows. Wildcards below group files
with the same contract; they do not represent omitted repository areas.

```text
.
├── .containerignore              Container build-context exclusions
├── .gitignore                    Generated and workstation-file exclusions
├── Containerfile                 RPM builder, runtime and development stages
├── compose.yaml                  Single-host simulation topology
├── Directory.Build.props         Repository-wide .NET build policy
├── FinSim.slnx                   Managed solution definition
├── global.json                   Required .NET SDK version and roll-forward policy
├── NuGet.Config                  NuGet sources and restore policy
├── README.md                     Entry point and quick start
├── build/
│   └── versions.conf             Reviewed upstream versions and SHA-256 pins
├── config/
│   ├── cgroup.conf.in            Profile-rendered Slurm cgroup configuration
│   ├── job_container.conf        Per-job temporary-filesystem configuration
│   ├── mariadb-server.cnf.in     MariaDB listener and TLS template
│   ├── slurm.conf.in             Controller, node and partition template
│   └── slurmdbd.conf.template    Accounting daemon and storage template
├── deploy/
│   ├── cloud-nodes.conf.example  Validated State=CLOUD inventory example
│   ├── lifecycle/                Cloud-neutral resume/suspend API clients
│   └── quadlet/                  Production Podman Quadlet templates
├── docs/
│   ├── ARCHITECTURE.md           Component boundaries and request flow
│   ├── CI.md                     Developer image and TeamCity operation
│   ├── DEPENDENCIES.md           Package and source provenance
│   ├── OPERATIONS.md             Local operational runbook
│   ├── PRODUCTION.md             Production deployment and security controls
│   ├── REVIEW.md                 Reviewed limitations and release gates
│   ├── SCRIPTS.md                Exhaustive script reference
│   ├── USER_GUIDE.md             End-to-end task-oriented guide
│   └── plans/                    Approved design and implementation history
├── scripts/
│   ├── build-images.sh           Build both image targets
│   ├── build-rpms.sh             Build verified MUNGE and Slurm RPMs
│   ├── install-runtime.sh        Install the runtime RPM allow-list
│   ├── configure-runtime.sh      Install users, paths and immutable helpers
│   ├── prepare-storage.sh        Create and ownership-map mutable storage
│   ├── prepare-config.sh         Render simulation or production config
│   ├── prepare-cluster.sh        Orchestrate local preparation
│   ├── bootstrap-dev-secrets.sh  Create local-only keys and credentials
│   ├── publish-app.sh            Test and publish immutable app releases
│   ├── cluster-up.sh             Start and health-gate the simulation
│   ├── cluster-down.sh           Stop the simulation without deleting state
│   ├── cluster-entrypoint.sh     Start exactly one selected container role
│   ├── cluster-healthcheck.sh    Role-aware container health checks
│   ├── local-node.sh             Add, drain or remove simulation nodes
│   ├── submit-simulation.sh      Shell interface for workflow submission
│   ├── submit-simulation.py      REST submission and polling implementation
│   ├── dev-jwt.py                Local JWT generation helper
│   ├── dev-token-renewer.sh      Rotate the local short-lived Slurm token
│   ├── render-production-unit.sh Render role-filtered production Quadlets
│   ├── validate-deployment.sh    Validate a prepared local deployment
│   ├── validate-production-*.sh  Validate production config and units
│   ├── acceptance-test.sh        Full live simulation acceptance suite
│   ├── ci.sh                     TeamCity/CI verify and integration entrypoint
│   ├── lib/                      Shared build and runtime-environment functions
│   └── tests/                    Shell/build/configuration contract tests
├── src/
│   ├── FinSim.Contracts/         Shared cache keys and workflow DTOs
│   ├── FinSim.Infrastructure/    Cache, JWT and Slurm REST clients
│   ├── FinSim/                   Master and worker batch executable
│   ├── FinSimCache/              Couchbase-like demo key/value HTTP service
│   └── FinSimProxy/              External workflow API and Slurm submitter
└── tests/
    └── FinSim.Tests/             Managed contract and component test runner
```

### 2.1 Root and build contracts

| Path | Purpose |
|---|---|
| `.containerignore` | Prevents Git metadata, generated state, credentials and build outputs from entering the image build context. |
| `.gitignore` | Keeps `.runtime`, agent-local metadata, build outputs and caches out of version control. |
| `Containerfile` | Builds checksum-verified RPMs, the universal `finsim-runtime` image and the developer/CI `finsim-dev` image. |
| `compose.yaml` | Assigns roles to the common runtime image for the local MariaDB, Slurm and FinSim simulation. It is not the production orchestrator. |
| `Directory.Build.props` | Applies common .NET target, compiler, warning and deterministic-build settings. |
| `FinSim.slnx` | Declares all production and test projects built together. |
| `global.json` | Pins the supported .NET SDK family for reproducible developer and CI builds. |
| `NuGet.Config` | Defines package sources and restore behavior used with the committed lock files. |
| `build/versions.conf` | Pins MUNGE, Slurm and external repository bootstrap artifacts by version and SHA-256. |

### 2.2 Configuration and deployment assets

| Path | Purpose |
|---|---|
| `config/` | Source-controlled, non-secret templates rendered by `prepare-config.sh`; generated copies belong under the deployment root. |
| `deploy/cloud-nodes.conf.example` | Example allow-listed production inventory for predeclared elastic `State=CLOUD` nodes. |
| `deploy/lifecycle/` | Provider-neutral `ResumeProgram` and `SuspendProgram` clients plus their shared mTLS HTTP implementation. |
| `deploy/quadlet/` | Universal production role template and non-secret environment example consumed by `render-production-unit.sh`. |

There is intentionally no `infra/` directory. The former `infra/slurm` path
held only an obsolete POC provenance note; current runtime configuration,
deployment assets and automation are owned by `config/`, `deploy/` and
`scripts/` respectively.

### 2.3 Application and test projects

Every .NET project commits its project file and `packages.lock.json`. The lock
files are part of the reproducible restore contract and must be updated through
an intentional dependency review.

| Path | Important contents |
|---|---|
| `src/FinSim.Contracts/` | `CacheKeys.cs` and `Models.cs`: shared wire and storage contracts. |
| `src/FinSim.Infrastructure/Cache/` | File and HTTP implementations of `IKeyValueStore`. |
| `src/FinSim.Infrastructure/Security/` | JWT authentication setup and safe private-token file reading. |
| `src/FinSim.Infrastructure/Slurm/` | Pinned Slurm REST client and typed exception mapping. |
| `src/FinSim/Batch/` | Master/worker dispatch, settings and the demo calculation. |
| `src/FinSim/Program.cs` | Batch executable entrypoint selected by master or worker arguments. |
| `src/FinSimCache/Program.cs` | Minimal authenticated cache-simulation HTTP API. |
| `src/FinSimProxy/Program.cs` | External workflow API host and authentication boundary. |
| `src/FinSimProxy/Services/WorkflowService.cs` | Validates requests, writes input keys and submits the master through Slurm REST. |
| `tests/FinSim.Tests/` | Dependency-free managed test runner for contracts, cache behavior and REST request construction. |

### 2.4 Automation and documentation ownership

The tree above lists every automation entrypoint. See
[SCRIPTS.md](SCRIPTS.md) for each script's arguments, security behavior,
dependencies, sourced functions and examples. Shared shell code is restricted
to `scripts/lib/build-common.sh` and `scripts/lib/runtime-env.sh`; script-level
contract tests live in `scripts/tests/test_build_scripts.py`.

The documents have distinct ownership: this guide is task-oriented,
[ARCHITECTURE.md](ARCHITECTURE.md) explains design,
[PRODUCTION.md](PRODUCTION.md) defines production controls,
[OPERATIONS.md](OPERATIONS.md) is the local runbook,
[CI.md](CI.md) covers developer and TeamCity use,
[DEPENDENCIES.md](DEPENDENCIES.md) records provenance, and
[REVIEW.md](REVIEW.md) carries known limitations and release gates.
`docs/plans/` preserves approved design decisions; it is not the operational
source of truth when a current guide or runbook exists.

### 2.5 Generated local deployment tree

`.runtime/` is deliberately ignored by Git and excluded from image build
contexts. It is generated by the preparation and publication scripts and is
never a source directory:

| Generated path | Purpose |
|---|---|
| `.runtime/cluster.env` | Non-secret selection of image tag, application release and local proxy port. |
| `.runtime/apps/releases/` | Immutable, manifested application releases. |
| `.runtime/apps/current` | Atomic symbolic link to the selected local release. |
| `.runtime/config/` | Rendered Slurm, SlurmDBD, cgroup and MariaDB configuration. |
| `.runtime/secrets/` | Long-lived development-only private keys and bootstrap credentials. |
| `.runtime/public/` | Public verification material such as application JWKS. |
| `.runtime/container-secrets/` | Container-consumable client credentials with restricted ownership and modes. |
| `.runtime/credentials/` | Rotating short-lived Slurm bearer token. |
| `.runtime/data/` | Shared workflow inputs, results, reports, logs and cache data. |
| `.runtime/state/mariadb/` | MariaDB database files. |
| `.runtime/state/slurmctld/` | Controller state required for restart recovery. |
| `.runtime/state/slurmdbd/` | Accounting daemon state. |
| `.runtime/state/slurmd/` | Per-node local Slurmd spool directories, including dynamically added nodes. |
| `.runtime/logs/` | Role-separated local service log directories. |

Do not copy `.runtime/` into an image or commit it. Production uses the same
configuration and credential contracts, but deployment roots, secret delivery,
persistent volumes and backup policies are supplied by the production
platform.

## 3. Development host prerequisites

The tested local host is Fedora Linux under WSL with rootless Podman. A native
Linux host also works. Install:

- Podman with cgroup v2;
- a Compose provider such as `podman-compose`;
- Git, Bash, Python 3, curl and `rg`;
- enough storage for UBI layers, RPM build trees and two images;
- outbound HTTPS access to the UBI, EPEL, CentOS Stream, Microsoft package and
  pinned upstream source endpoints listed in [DEPENDENCIES.md](DEPENDENCIES.md).

Check the host before building:

```bash
podman version
podman info --format '{{.Host.CgroupsVersion}}'
podman compose version
git status --short
```

Do not run nested Podman and do not mount a privileged Podman socket inside the
development image. Image and cluster lifecycle commands run on the host.

## 4. Build Slurm and MUNGE RPMs

### Normal image build

From the repository root:

```bash
FINSIM_IMAGE_TAG=review scripts/build-images.sh
```

This builds:

```text
localhost/finsim-runtime:review
localhost/finsim-dev:review
```

The internal `rpm-builder` stage writes binary RPMs, source RPMs, pinned source
metadata and `BUILD-MANIFEST` under `/var/packages`. The runtime copies that
directory, verifies it and installs only the allow-listed runtime RPMs.

Inspect the retained artifacts:

```bash
podman run --rm --entrypoint /usr/bin/find \
  localhost/finsim-runtime:review /var/packages -maxdepth 3 -type f
```

### Export RPMs for an RPM repository

Build and copy the internal stage without publishing it as a production image:

```bash
podman build --target rpm-builder -t localhost/finsim-rpm-builder:review .
container_id=$(podman create localhost/finsim-rpm-builder:review)
podman cp "$container_id:/var/packages" ./packages-review
podman rm "$container_id"
bash -c 'source scripts/lib/build-common.sh; build_verify_manifest "$1"' \
  _ "$PWD/packages-review"
```

The manifest begins with source metadata as well as file checksum records; use
`scripts/lib/build-common.sh` for the repository's full verification rather
than treating `sha256sum` alone as the release gate.

### Run the three phases manually

On an isolated EL10 build host or container, as root:

```bash
scripts/build-rpms.sh \
  --versions-file build/versions.conf \
  --output-dir /var/packages \
  --work-dir /var/tmp/slurm-rpm-build

scripts/install-runtime.sh \
  --versions-file build/versions.conf \
  --packages-dir /var/packages

scripts/configure-runtime.sh --source-root "$PWD"
```

These commands mutate that EL10 environment and are primarily for package
engineering. Normal developers should use the Containerfile stages.

## 5. Compile and publish the applications

`finsim-dev` contains a source snapshot from image build time. Rebuild it after
changing source code. Then publish a new immutable release identifier:

```bash
FINSIM_IMAGE_TAG=review scripts/build-images.sh
FINSIM_IMAGE_TAG=review scripts/publish-app.sh --release 2026.10.31.1
```

Publication performs locked restore, Release build, managed tests and three
framework-dependent publishes. It generates `release-manifest.json`, maps
ownership to container root, removes write permissions and atomically moves the
release under:

```text
.runtime/apps/releases/2026.10.31.1/
```

Never replace an existing release. Publish a new ID, validate it, update the
deployment reference and retain the previous release for rollback. The local
`prepare-cluster.sh` deliberately reuses an existing selected release; it does
not infer whether source changed.

## 6. Prepare and run the development cluster

For the default `latest`/`dev` workflow:

```bash
scripts/build-images.sh
scripts/prepare-cluster.sh
scripts/cluster-up.sh
scripts/submit-simulation.sh --wait
scripts/acceptance-test.sh
```

For explicit image and application versions:

```bash
export FINSIM_IMAGE_TAG=review
export FINSIM_APP_RELEASE=2026.10.31.1
export FINSIM_PROXY_PORT=18088

scripts/prepare-storage.sh
scripts/prepare-config.sh --profile simulation
scripts/bootstrap-dev-secrets.sh
scripts/publish-app.sh --release "$FINSIM_APP_RELEASE"
scripts/validate-deployment.sh
scripts/cluster-up.sh
scripts/submit-simulation.sh --port "$FINSIM_PROXY_PORT" --wait
```

`prepare-cluster.sh` combines the preparation steps but never starts a service.
`cluster-up.sh` validates prepared files and never creates credentials. This
separation prevents service startup from silently rotating secrets or changing
an application release.

The local topology contains MariaDB, slurmdbd, one controller, slurmrestd, a
development token agent, proxy/cache, one master-capable node and four
worker-only nodes. All services use `finsim-runtime`; `finsim-dev` is not a
daemon image.

## 7. Submit and inspect jobs

The safe client reads the API bearer token from its mode-0600 file and does not
put it in command arguments:

```bash
scripts/submit-simulation.sh \
  --count 100 \
  --chunk-size 10 \
  --parallel 4 \
  --wait
```

The proxy writes input through the cache interface and submits a master through
Slurm REST. The scheduled master creates chunk keys and submits a throttled
worker array through Slurm REST. Workers write result keys. The master reads the
results and atomically writes the final report.

Administrative diagnostics may use Slurm CLI inside the controller container;
application code must not:

```bash
podman exec finsim-controller sinfo --Node --long
podman exec finsim-controller squeue
podman exec finsim-controller sacct --starttime today
podman exec finsim-controller ps -eo uid,user,pid,comm,args
podman compose --env-file .runtime/cluster.env -f compose.yaml logs restd
```

`podman exec` takes a container name. `podman compose exec` takes a service name
such as `controller`.

## 8. Add or remove local compute nodes

Compose defines the baseline five nodes. Add extra dynamic nodes without
editing Compose:

```bash
scripts/local-node.sh add compute-host-006 worker
scripts/local-node.sh add compute-host-007 master
podman exec finsim-controller sinfo -Nel
```

Removal drains the node, waits for allocations, stops its container, deletes
the dynamic Slurm record and retains its spool:

```bash
scripts/local-node.sh remove compute-host-006 900
```

Do not stop or delete an allocated node directly. Dynamic node deletion is
valid only when it has no running job and is not part of a reservation.

## 9. Stop, restart and reset development state

Stop containers while retaining state and releases:

```bash
scripts/cluster-down.sh
```

Restart with `scripts/cluster-up.sh`. To reset disposable state, first stop the
cluster, then move the specific `.runtime` directory to a dated quarantine
location. Inspect it before eventual deletion. Never recursively delete a
deployment root while containers are running.

Important local locations are:

| Host path | Container purpose |
|---|---|
| `.runtime/config` | rendered Slurm, cgroup and MariaDB configuration |
| `.runtime/secrets` | long-lived development-only keys and tokens |
| `.runtime/credentials` | rotating short-lived Slurm bearer token |
| `.runtime/apps/releases` | immutable application releases |
| `.runtime/data` | workflow logs, reports and shared data |
| `.runtime/state/mariadb` | MariaDB database files |
| `.runtime/state/slurmctld` | controller state |
| `.runtime/state/slurmd/*` | node-local Slurmd state |

## 10. CI and TeamCity

Run on a dedicated Linux agent with host Podman:

```bash
BUILD_TAG=ci-123 scripts/ci.sh verify
BUILD_TAG=ci-123 scripts/ci.sh integration
```

`verify` runs static and unit checks, renders and validates production config,
then builds both images. `integration` additionally publishes a versioned
release, starts the local cluster, submits the REST workflow and runs security,
identity, placement, accounting and image-content acceptance checks.

Use a unique `BUILD_TAG` per immutable build. Publish the runtime image and
application release separately. Promote the tested runtime digest without
rebuilding it.

## 11. Prepare production configuration

Production requires two controller names and two slurmdbd names:

```bash
sudo env \
  FINSIM_CLUSTER_NAME=finsim-prod \
  FINSIM_CONTROLLER_PRIMARY=controller-primary \
  FINSIM_CONTROLLER_BACKUP=controller-backup \
  FINSIM_SLURMDBD_PRIMARY=slurmdbd-primary \
  FINSIM_SLURMDBD_BACKUP=slurmdbd-backup \
  FINSIM_MAX_NODES=4096 \
  FINSIM_MAX_JOBS=200000 \
  scripts/prepare-config.sh \
    --profile production \
    --output-dir /etc/finsim/config

sudo scripts/validate-production-config.sh /etc/finsim/config
```

Distribute one identical, root-owned configuration revision to every role.
Record the generated manifest digest in deployment inventory. Never edit a
generated file in place; render, review and deploy a new revision.

### Optional Slurm-driven lifecycle automation

Without lifecycle integration, an external platform starts dynamic compute
nodes and they self-register. To let Slurm request capacity, render the hooks:

```bash
sudo install -o root -g root -m 0600 \
  deploy/cloud-nodes.conf.example /tmp/finsim-cloud-nodes.conf
# Edit the two bounded ranges and resources for the approved capacity plan.
```

```bash
sudo env \
  FINSIM_CONTROLLER_PRIMARY=controller-primary \
  FINSIM_CONTROLLER_BACKUP=controller-backup \
  FINSIM_SLURMDBD_PRIMARY=slurmdbd-primary \
  FINSIM_SLURMDBD_BACKUP=slurmdbd-backup \
  FINSIM_LIFECYCLE_ENABLE=1 \
  FINSIM_LIFECYCLE_API_URL=https://lifecycle.service.internal \
  FINSIM_RESUME_TIMEOUT=900 \
  FINSIM_SUSPEND_TIMEOUT=300 \
  FINSIM_SUSPEND_TIME=1800 \
  scripts/prepare-config.sh --profile production \
    --cloud-inventory /tmp/finsim-cloud-nodes.conf \
    --output-dir /etc/finsim/config
```

The inventory accepts only `NodeName` records with explicit positive `CPUs`,
`RealMemory`, `State=CLOUD`, and exactly one of `finsim-master` or
`finsim-worker`. It is parsed as data, copied into the manifest and inserted in
`slurm.conf`; lifecycle rendering without it fails. Keep its expanded total
within `MaxNodeCount`.

This repository supplies the inventory contract and bounded mTLS client. The
external lifecycle service must authenticate the cluster, enforce idempotency,
reconcile desired state, start or stop the exact requested hosts and report
failures. Test controller failover while lifecycle requests are active.

## 12. Production host contract

Use rootful Podman and system Quadlet. Do not use Compose across thousands of
hosts. Pre-create a named Podman network that provides routable addresses
across hosts, such as an operator-managed macvlan/ipvlan network or an approved
network plugin. A normal Podman bridge is host-local and is not sufficient;
provide cross-host DNS/service discovery separately. Generated units reject
host networking. If a platform needs
host networking, treat that as a separate security exception and review port
collisions, firewall rules and namespace exposure.

Expected host paths:

| Path | Owner/mode and purpose |
|---|---|
| `/etc/finsim/config` | root, non-writable rendered configuration |
| `/etc/finsim/roles` | root, non-secret per-instance environment files |
| `/etc/finsim/public` | root, public JWKS and CA material |
| `/etc/finsim/secrets` | role-filtered private material; never mount the parent wholesale |
| `/run/finsim/credentials` | tmpfs delivery for rotating FinSim Slurm token |
| `/srv/finsim/apps` | root-owned read-only versioned releases |
| `/srv/finsim/data` | shared data or compatible cache/report implementation |
| `/var/lib/finsim/*` | role-specific persistent or node-local state |

Fixed container IDs are MUNGE 64029, Slurm 64030, slurmrestd 64031, FinSim
64032, MariaDB 27 and developer 1000. Align shared-storage ownership or use an
identity mapping proven by an integration test. Important private-file modes:

- Slurm and application issuer private keys never enter these containers.
- Slurm role TLS input keys are root-owned 0600 and staged to the daemon user.
- application role TLS input keys are root:finsim (GID 64032) mode 0440;
  their certificates are root-owned and non-writable. The application role
  deliberately reads this one role-filtered key directly so it needs no file
  ownership or signal capability after dropping to UID 64032.
- MariaDB server TLS key is UID 27 and 0400 on its database host.
- lifecycle client key is UID 64030 and 0600 on controller hosts.
- cache and Slurm bearer-token files consumed by FinSim are UID 64032 and 0400.
- public JWKS and CA bundles are root-owned and not group/world writable.

`/etc/finsim/public/service-ca-bundle.pem` contains only the public roots and
intermediates needed for application, cache and Slurm REST HTTPS. It is mounted
at `/run/secrets/service-ca-bundle.pem` on proxy and compute roles. The proxy
passes that fixed public path to the master, and the master passes it to
workers. No TLS private key enters a Slurm job.

Run a host MUNGE daemon with the cluster key and mount only `/run/munge` into
Slurm roles. Protect and rotate the cluster-wide MUNGE key outside containers.

## 13. Render and install production Quadlets

Use the runtime digest produced by the registry, not a mutable tag:

```bash
image='registry.example.com/hpc/finsim-runtime@sha256:REPLACE_WITH_64_HEX'

scripts/render-production-unit.sh \
  --role compute-worker \
  --instance compute-0001 \
  --image "$image" \
  --network finsim-routable \
  --output-dir ./deployment/compute-0001

scripts/validate-production-unit.sh \
  ./deployment/compute-0001/finsim-compute-0001.container
```

For `proxy` and `cache`, also pass `--app-release`. For controllers with
enabled lifecycle configuration, pass `--enable-lifecycle`.

Copy the `.container` file to `/etc/containers/systemd/`. Replace values in the
generated `.env.example`, store the result at the exact
`/etc/finsim/roles/<instance>.env` path and keep secrets out of that file.

Before activation, validate the target host's installed Quadlet version:

```bash
sudo env QUADLET_UNIT_DIRS=/etc/containers/systemd \
  /usr/lib/systemd/system-generators/podman-system-generator --dryrun
sudo systemctl daemon-reload
sudo systemctl enable --now finsim-compute-0001.service
sudo systemctl status finsim-compute-0001.service
sudo podman healthcheck run finsim-compute-0001
```

The generated compute unit uses `Delegate=yes`, privileged rootful Podman, the
host cgroup namespace and a read-write host cgroup v2 mount. This is deliberate:
root `slurmd`, device policy and `job_container/tmpfs` are host-trusted
operations. The image and application mounts remain read-only and compute gets
no database, JWKS-signing, lifecycle or controller-state secret. Run it only on
a dedicated compute host. Before admitting the node, prove a UID-64032 job,
CPU/memory/device constraints, private `/tmp`, drain and removal on the exact
EL10/Podman/Slurm combination.

Generated proxy/cache units bind Kestrel to `https://0.0.0.0` on their role
port and require the instance-specific `application-role.pem` and
`application-role-key.pem`. Put an internal load balancer in front of proxy
replicas as needed; do not change the backend to cleartext merely because TLS
also terminates at the load balancer.

## 14. Production deployment order

1. Provision DNS, routable container networking, firewall policy, time sync,
   registry trust, certificates, JWKS, shared storage and database backups.
2. Deploy HA MariaDB or the repository database role with server TLS. For a
   2,000-node environment, use a separately operated HA database service.
3. Start host MUNGE daemons on Slurm hosts.
4. Deploy primary and backup slurmdbd roles.
5. From a controlled administrator context, create the production Slurm
   cluster, account, QOS/limits and `finsim` association with `sacctmgr`.
6. Deploy primary and backup controllers with shared, strongly consistent
   controller state and fencing tested.
7. Deploy multiple slurmrestd instances behind an internal TLS load balancer.
8. Deploy FinSimCache's production replacement and proxy instances behind TLS.
9. Project short-lived Slurm and cache credentials to master/proxy hosts.
10. Add a small canary worker pool, run acceptance workloads, then increase
    capacity in bounded batches.

Do not copy the simulation's automatic accounting bootstrap into production.
Production associations, limits and QOS are controlled configuration.

## 15. Production node registration and removal

The compute entrypoint supports three explicit modes:

| Mode | Use |
|---|---|
| `dynamic` | `slurmd -Z` self-registers a new node with its physical resources and FinSim feature |
| `future` | matches a predeclared `State=FUTURE` node with the requested feature |
| `static` | registers the host name against a precreated static or `State=CLOUD` record |

The generated environment example defaults to `dynamic`. Automatic Slurm
power saving uses the rendered cloud inventory and `static` mode: set
`SLURMD_REGISTRATION_MODE=static`, keep `SLURM_CONF_SERVER`, remove
`SLURMD_FEATURE`, and make the host name equal the requested `NodeName`.

Safe manual removal is:

```bash
scontrol update NodeName=compute-0001 State=DRAIN Reason=planned-removal
squeue --nodes compute-0001
# Wait for no jobs and verify the node is not reserved.
systemctl stop finsim-compute-0001.service
scontrol delete NodeName=compute-0001
```

Only dynamic nodes can be deleted. Never terminate the host before drain and
allocation checks unless an incident policy explicitly accepts job loss.
Predeclared `State=CLOUD` records remain in configuration after scale-in; stop
their hosts after drain and let Slurm return them to the powered-down state.

## 16. JWT, TLS and credential rules

Production uses RS256 public JWKS trust. Private issuer keys stay in the
organization's issuer or HSM. Tokens must have a short expiration, stable
subject, exact audience and least-privilege scope:

- external callers: `finsim.submit` for FinSimProxy;
- proxy/master/workers: `finsim.cache` for the cache implementation;
- proxy/master: non-administrative `finsim` Slurm identity;
- workers do not receive a Slurm token.

Because production sets `use_jwt_client_ids_only`, a Slurm token must carry a
complete immutable POSIX identity. For this repository that means
`preferred_username=finsim`, numeric `uid=64032`, numeric `gid=64032`, and an
`id` object whose `name`, `gecos`, `dir` and `shell` describe the same account,
plus exactly one of a trusted name-to-GID `groups` mapping or numeric `gids`
array. The issuer must fix these values; never accept caller-selected UID, GID
or supplemental groups. Use a Slurm-specific signing key/JWKS and constrain
issuer, audience and lifetime at issuance and every upstream authentication
boundary.

The Slurm token is authority for the shared `finsim` account, not a
per-workflow capability. A master can act on other jobs owned by that account.
FinSimProxy enforces external workflow ownership, but deployments needing
hostile-tenant isolation require separate Slurm identities and a different
reviewed account model.

Rotate tokens by atomically replacing mode-0400 files in tmpfs. Consumers read
the file for each request and do not log the value. Test old-key overlap,
revocation, clock skew and issuer outage. Use HTTPS for every production REST
hop. Production proxy/cache units bind routable `https://0.0.0.0` listeners,
present role-specific certificates and run HTTPS readiness checks. A load
balancer may terminate another TLS layer but must keep the backend encrypted.

## 17. Storage, backup and recovery

- MariaDB: use database-native consistent backups and restore tests.
- Controller state: use strongly consistent HA storage supported by the chosen
  Slurm controller design; test fencing and failover.
- Slurmd spool: node-local and disposable after the node is drained.
- Releases: immutable, signed externally and replicated before activation.
- Workflow/cache data: shared POSIX semantics or a production cache such as
  Couchbase behind the same key/value interface.
- Reports: durable object/database storage according to retention policy.

Back up configuration manifests, registry digests, accounting data, controller
state and application release metadata together. A backup without a rehearsed
restore procedure is not a recovery plan.

## 18. Upgrade and rollback

1. Build once from reviewed pins.
2. Generate SBOM/provenance, scan and sign the runtime digest and release.
3. Run CI integration and a production-profile canary.
4. Back up database/controller state and record current config/release digests.
5. Drain and replace compute nodes in bounded batches.
6. Upgrade control-plane services in the order supported by the selected Slurm
   release and verify accounting after every step.
7. Roll back by restoring the prior digest, configuration and application
   release. Database or controller-state rollback requires the matching tested
   backup; never assume an image rollback reverses persistent schema changes.

## 19. Troubleshooting checklist

Local commands:

```bash
podman ps --format '{{.Names}} {{.Status}}'
podman logs finsim-controller
podman exec finsim-controller scontrol ping
podman exec finsim-controller sinfo -Nel
podman exec finsim-controller sacct --starttime today
scripts/validate-deployment.sh
```

Production commands:

```bash
systemctl status finsim-INSTANCE.service
journalctl -u finsim-INSTANCE.service
podman inspect finsim-INSTANCE
podman healthcheck run finsim-INSTANCE
scontrol show node NODE --oneliner
sacct --starttime today --state FAILED,NODE_FAIL,OUT_OF_MEMORY,TIMEOUT
```

An interactive `podman exec ... id` normally reports the root bootstrap user.
Inspect the daemon process and Slurm accounting to prove workload identity.
`slurmd` remains root by design; FinSim jobs must appear as UID/user 64032 or
`finsim` in accounting.

## 20. Primary references

- Slurm administration: <https://slurm.schedmd.com/quickstart_admin.html>
- Slurm REST: <https://slurm.schedmd.com/rest.html>
- Slurm JWT: <https://slurm.schedmd.com/jwt.html>
- Configless Slurm: <https://slurm.schedmd.com/configless_slurm.html>
- Dynamic nodes: <https://slurm.schedmd.com/dynamic_nodes.html>
- Power saving: <https://slurm.schedmd.com/power_save.html>
- cgroup v2: <https://slurm.schedmd.com/cgroup_v2.html>
- Large clusters: <https://slurm.schedmd.com/big_sys.html>
- Podman Quadlet: <https://docs.podman.io/en/latest/markdown/podman-systemd.unit.5.html>
- ASP.NET Core Kestrel HTTPS: <https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints>
- .NET Linux certificate trust: <https://learn.microsoft.com/dotnet/standard/security/cross-platform-cryptography>
- RHEL 10 UBI images: <https://docs.redhat.com/en/documentation/red_hat_enterprise_linux/10/html/building_running_and_managing_containers/types-of-container-images>
- Adding software to UBI: <https://docs.redhat.com/en/documentation/red_hat_enterprise_linux/10/html/building_running_and_managing_containers/adding-software-to-a-ubi-container>
