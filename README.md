# FinSim on Slurm

FinSim is a cloud-neutral reference implementation for containerized batch
workloads on Slurm.  The demo calculation is intentionally small; the
important artifact is the production-shaped boundary between an authenticated
submission API, Slurm REST, a master job, worker arrays, and an external-style
key/value data service.

The local topology has one controller, one accounting database, one
`slurmdbd`, one `slurmrestd`, one proxy, one cache simulator, and five compute
containers.  Compute node 1 is an orchestrator node that can run master jobs;
nodes 2-5 run workers and do not receive a Slurm REST credential.  All master
and worker payloads run as the fixed `finsim` identity (UID/GID 64032), never
as root.

## Working flow

```text
client
  |  OAuth/JWT, finsim.submit
  v
FinSimProxy ---- Slurm JWT ----> slurmrestd ----> slurmctld
  |                                      |             |
  | cache JWT                            |             +--> master on orchestrator node
  v                                      |                    |
FinSimCache <----------------------------+                    +--> worker array via REST
  ^                                                           |
  +------------- chunk/result keys ---------------------------+
                                                              |
                                     /finsim-data/reports/<workflow>.json
```

The proxy creates a deterministic workflow ID from the authenticated subject
and `Idempotency-Key`, stores input and submission intent, and submits the
master through Slurm REST v0.0.45.  The master creates keyed chunks, submits
one throttled worker array through REST, polls it, reads keyed results, and
atomically writes the final private report.  Workers cannot write reports.

## Image layers

| Target | Purpose | Production? |
|---|---|---|
| `rpm-builder` | Build verified MUNGE and Slurm RPM/SRPM artifacts into `/var/packages` | Build only |
| `slurm-runtime` | UBI 10.2, Slurm/MUNGE, ASP.NET 10 runtime; no database server | Yes, base |
| `finsim-app` | `slurm-runtime` plus published FinSim and FinSimProxy binaries; no SDK, source, cache simulator, or MariaDB | Yes |
| `finsim-sim` | `finsim-app` plus MariaDB and FinSimCache | Local integration only |
| `finsim-dev` | `finsim-sim` plus .NET SDK, git, jq, source, tests, and Compose inputs | Development/TeamCity only |

`Containerfile` delegates Slurm build, installation, and configuration to
three independently callable scripts:

```bash
sudo scripts/build-rpms.sh --output-dir /var/packages
sudo scripts/install-runtime.sh --packages-dir /var/packages
sudo scripts/configure-runtime.sh --source-root "$PWD"
```

The first command needs an EL10 build host and network access.  The latter two
verify the package manifest before installation and do not generate secrets.
See [dependency provenance](docs/DEPENDENCIES.md) before selecting the public
repository mode for a production build.

## Run the local cluster

Prerequisites are Fedora/WSL with rootless Podman, `podman-compose`, Python 3,
and curl.  Host .NET and OpenSSL are not required.

```bash
scripts/cluster-up.sh
scripts/submit-simulation.sh --wait
scripts/acceptance-test.sh
scripts/cluster-down.sh
```

`cluster-up.sh` builds the production base/application targets plus the local
simulation target, generates disposable development credentials under the
git-ignored `.runtime/`, starts Compose, and waits for the cache, all five
nodes, and the proxy.  It does not print credentials.

Useful diagnostics:

```bash
podman exec finsim-controller sinfo --Node --long
podman exec finsim-controller squeue
podman exec finsim-controller sacct --starttime today --format=JobID,JobName,User,State
podman logs finsim_proxy_1
podman logs finsim_restd_1
```

`podman compose exec` takes a Compose **service** name:

```bash
podman compose --env-file .runtime/cluster.env -f compose.yaml exec controller sinfo
```

`podman exec` takes a **container** name, hence `finsim-controller` above.
`podman compose exec finsim-controller ...` is invalid because that is not a
service key.

The local API is bound only to `127.0.0.1:8088`.  A sample request contains
1..20, chunk size 4, and maximum worker parallelism 3; the expected report has
five chunks and sum 210.  The helper reads its JWT from a mode-0600 file and
never places it on the command line.

## Security model in one page

- Container startup is not the same as workload identity.  Controller and
  REST containers use a small root staging process to validate mounts and drop
  to `slurm` (64030) or `slurmrestd` (64031).  Production `slurmd` must run as
  host root so it can create cgroups and switch jobs to the allocated UID;
  FinSim payloads still run as UID 64032.
- Proxy and cache containers start directly as UID/GID 64032, with a read-only
  root filesystem, all capabilities dropped, `no-new-privileges`, bounded
  PIDs, and explicit writable mounts/tmpfs.
- The local Compose profile uses HTTP only on internal Podman networks and
  disposable HS256 Slurm tokens.  Production fails closed unless internal
  Slurm TLS, HTTPS REST endpoints, external RS256/JWKS trust, root-owned
  configuration, and cgroup v2 isolation are supplied.
- API, cache, and Slurm tokens have different audiences and scopes.  Worker
  nodes do not mount the Slurm token.  Private key generation in
  `bootstrap-dev-secrets.sh` is test-only; production keys come from an
  organization-controlled identity/key management service.
- `.runtime/` is ignored by git.  Configuration is immutable and root-owned;
  secrets are mounted at runtime, never copied into an image or committed.

The detailed rationale and production controls are in:

- [Architecture design](docs/plans/2026-10-27-finsim-slurm-architecture-design.md)
- [Implementation details](docs/IMPLEMENTATION.md)
- [Production deployment and security](docs/PRODUCTION.md)
- [Dependency and repository provenance](docs/DEPENDENCIES.md)
- [Operations runbook](docs/OPERATIONS.md)
- [Development and TeamCity pipeline](docs/CI.md)

## Production status

The application paths and production daemon profile are implemented and tested,
but `compose.yaml` is deliberately **not** a production deployment descriptor.
Production uses role-specific deployment units on separate failure domains,
external HA database/data services, real OIDC/JWKS, managed certificates,
rootful dedicated compute hosts, and a site-specific lifecycle adapter.  The
repository supplies the contracts and fail-closed configuration for those
integrations; it does not pretend one cloud provider's API is portable.

Primary references include the [Slurm REST guidance](https://slurm.schedmd.com/rest.html),
[Slurm JWT guidance](https://slurm.schedmd.com/jwt.html),
[configless Slurm](https://slurm.schedmd.com/configless_slurm.html),
[Slurm cgroup v2](https://slurm.schedmd.com/cgroup_v2.html), and
[Red Hat UBI documentation](https://docs.redhat.com/en/documentation/red_hat_enterprise_linux/10/html/building_running_and_managing_containers/types-of-container-images).
