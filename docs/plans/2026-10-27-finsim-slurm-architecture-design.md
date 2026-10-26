# FinSim cloud-neutral Slurm batch architecture

Status: approved on 2026-10-27

## 1. Purpose

This repository is a production-oriented reference architecture for running a
containerized batch application on Slurm without depending on AWS, Azure, or
another cloud-specific scheduling API. The demo calculation is intentionally
small. The reusable deliverable is the image layering, REST-only scheduling,
identity, credential, data-exchange, reliability, local simulation, and CI
contract.

The design evolves the proven Slurm-on-UBI 10.2 POC in
`/home/ben/repos/projects/slurmpoc`. It does not copy the POC's Python workflow
broker: FinSimProxy and the FinSim master both call `slurmrestd` directly.

## 2. Goals and non-goals

Goals:

1. Run the same immutable FinSim application image on local Podman, AWS,
   Microsoft HPC environments, or another Linux container host.
2. Keep production and development images close by deriving both from the
   same UBI 10.2 Slurm runtime stage.
3. Keep compilers, the .NET SDK, test tools, Compose assets, and source out of
   the production application image.
4. Exercise a local controller, accounting service, REST service, cache,
   proxy, and five compute nodes through manually operated Podman Compose.
5. Use only Slurm REST endpoints from application code. Application code must
   not execute `sbatch`, `squeue`, `sacct`, `scancel`, or `scontrol`.
6. Run every batch application process as the fixed, non-login `finsim`
   service identity, never root.
7. Make the application-specific portion easy to replace with another runtime
   and toolchain.

Non-goals:

- implementing a finance model representative of the real product;
- emulating Couchbase query semantics beyond the key/value operations needed
  by the workflow;
- providing account-specific AWS or Azure infrastructure as code;
- running a nested container engine inside a Slurm compute container; or
- providing per-human-user Slurm identities in this service-account model.

## 3. Image architecture

One `Containerfile` exposes named, reproducible targets:

```text
registry.access.redhat.com/ubi10/ubi:10.2
                  |
                  v
          slurm-rpm-builder
        MUNGE + Slurm RPM build
                  |
                  v
        finsim-slurm-runtime
  Slurm/MUNGE + .NET/ASP.NET 10 runtime
  CA certificates + fixed service identities
                  |
          +-------+--------+
          |                |
          v                v
     finsim-app        finsim-dev
  published apps       .NET 10 SDK
  no SDK/source         diagnostics/tests
                       source/scripts/Compose
```

The controller, `slurmdbd`, and `slurmrestd` use
`finsim-slurm-runtime`. Compute nodes and FinSimProxy use `finsim-app`.
FinSimCache is a local/CI integration service and is not deployed when a real
Couchbase-backed adapter is configured.

The application image is also the compute-node image. `slurmd` launches
FinSim directly inside that container. There is no Docker-in-Docker,
Podman-in-Podman, nested OCI runtime, or per-job container launch.

The development target can use the host's rootless Podman API socket for
manual integration testing. It never starts a nested Podman daemon. Access to
that socket grants control of the invoking user's containers and is restricted
to trusted developers and dedicated CI agents.

## 4. Applications

The solution contains three deliberately small C# applications:

| Application | Responsibility | Deployment |
|---|---|---|
| FinSim | Console application with `master` and `worker` modes | Production and local compute nodes |
| FinSimProxy | JWT-protected API for submit, status, report, and cancellation | Production and local edge/control tier |
| FinSimCache | JWT-protected key/value API simulating the future Couchbase boundary | Local and CI only |

Shared libraries provide validated contracts, JWT configuration, a cache
client, and the pinned Slurm REST client. The demo worker calculates
count/sum/minimum/maximum for a bounded numeric chunk. The master combines the
partial values and writes a single JSON report.

## 5. Control and data flow

```text
Client
  |
  | POST /v1/simulations + client JWT
  v
FinSimProxy
  |-- PUT workflow input/state --------> FinSimCache
  |-- POST master job -----------------> slurmrestd
  |-- GET master status ---------------> slurmrestd
  |
  v
FinSim master job
  |-- GET input by key ----------------> FinSimCache
  |-- split input and PUT chunk keys --> FinSimCache
  |-- POST bounded worker array -------> slurmrestd
  |-- GET worker states ---------------> slurmrestd
  |-- GET result keys -----------------> FinSimCache
  `-- atomically write final report ---> /finsim-data/reports/<workflow>.json
            ^
            |
FinSim worker array
  |-- GET chunk by derived key --------> FinSimCache
  |-- calculate a partial result
  `-- PUT JSON result by result key ----> FinSimCache
```

The master uses one bounded Slurm array rather than one submission per chunk.
Each task derives its input and output key from the server-generated workflow
UUID and `SLURM_ARRAY_TASK_ID`. Only opaque keys cross the scheduling boundary;
the cache adapter owns actual data storage.

FinSimProxy exposes:

- `POST /v1/simulations`;
- `GET /v1/simulations/{id}`;
- `GET /v1/simulations/{id}/report`;
- `DELETE /v1/simulations/{id}`; and
- `/health/live` and `/health/ready`.

FinSimCache exposes bounded `PUT`, `GET`, and `DELETE` operations beneath
`/v1/kv/{validated-key}`. It deliberately exposes no arbitrary filesystem or
unbounded list operation.

## 6. Identity and credential model

All batch processes run as a fixed non-login `finsim` POSIX account with the
same numeric UID/GID on controllers, compute nodes, and shared storage. Slurm
services retain their role-specific identities; `slurmd` remains root so it
can establish cgroups and change the job to the submitted UID/GID.

Three credential audiences are separate:

1. Client JWTs authenticate callers to FinSimProxy. Production configuration
   pins issuer, audience, algorithms, and JWKS. Local development uses a
   generated asymmetric key and file-backed public JWKS.
2. Slurm JWTs authenticate FinSimProxy and the master to `slurmrestd` as the
   restricted `finsim` identity. They are short-lived and file-backed.
3. Cache JWTs authenticate FinSim components to FinSimCache and use a distinct
   issuer/audience/key from Slurm.

Private keys and bearer tokens are never baked into an image, committed,
placed in job arguments, written to the cache, or logged. A master credential
is delivered as a mode-0400 file in a job-private runtime location. The client
reloads it for every REST request so rotation does not require a restart.

Because all trusted jobs share one POSIX identity, a compromised master token
can act as `finsim` until it expires. Short lifetimes, private networking,
immutable signed images, Slurm association/QOS limits, bounded partitions,
and job-private delivery constrain but do not eliminate this risk. Strong
per-workflow Slurm authorization requires distinct identities or a scheduling
broker and is outside this explicitly selected model.

## 7. Reliability model

FinSimProxy requires an idempotency key when creating a workflow. Before a
mutating Slurm REST call, the caller persists a submission intent and unique
comment marker. If the HTTP response is lost, it queries jobs by that exact
marker and reconciles instead of blindly submitting again.

Master and worker operations are retry-safe. Cache writes are create-only or
accept a replay only when the content digest is identical. A retried worker
cannot replace a different result. The master succeeds only after every array
task is terminal and every expected result is present and valid.

Polling uses deadlines, bounded exponential backoff, and jitter. Requests,
payloads, chunk count, parallelism, CPU, memory, runtime, and report size are
bounded. Cancellation is recorded before REST cancellation calls and covers
the master plus every known worker job.

Logs contain workflow, Slurm job, chunk, request, and correlation identifiers,
but never credentials or complete untrusted response bodies.

## 8. Storage abstraction

Application code uses a narrow asynchronous key/value contract:

```text
PutIfAbsent(key, JSON, digest)
Get(key)
Delete(key)
```

Keys use a generated grammar such as
`workflow/<uuid>/chunk/000001`; callers cannot provide a filesystem path.
FinSimCache persists JSON atomically for local testing. A production Couchbase
adapter can implement the same contract without changing Slurm scheduling or
master/worker behavior.

Only the master writes the final report. It writes a temporary file, flushes
and synchronizes it, then atomically renames it beneath the configured report
root. Production can replace this final sink with a database adapter.

## 9. Local simulation and production separation

The local Compose topology contains MariaDB, `slurmdbd`, one controller,
private `slurmrestd`, FinSimCache, FinSimProxy, and five compute containers.
Configuration and development secrets are generated beneath ignored
`.runtime/` paths. Only FinSimProxy binds to host loopback.

Production does not use the Compose topology. It uses role-specific systemd
Quadlets or equivalent reviewed deployment units, external HA accounting,
TLS, production JWKS, a real data service, cgroup v2, dedicated compute hosts,
read-only roots, private networks, and external secret delivery. No cloud SDK
is required by application or scheduler code.

## 10. Testing and acceptance

Automated tests cover contracts, input validation, JWT validation, cache
semantics, Slurm request/response handling, ambiguous submission recovery,
idempotency, cancellation, master aggregation, and worker retries.

Container tests prove that the production image has only runtime components
and that the developer image contains the SDK and diagnostics. The Compose
acceptance suite submits through FinSimProxy, confirms master and workers run
as non-root `finsim`, validates cache-key-only exchange, checks the report,
tests cancellation, and rejects any Slurm CLI invocation from application
source.

Production acceptance additionally validates secret modes, private network
exposure, root-job rejection, cgroup limits, read-only filesystems, matching
UID/GID values, TLS/JWKS validation, and supported rollback.

## 11. Adaptation contract

To adapt this architecture to another batch workload:

1. derive its application image from the Slurm runtime target;
2. copy only published runtime artifacts;
3. supply master and worker entrypoints;
4. exchange bounded opaque keys through a data-store interface;
5. keep scheduling and monitoring on pinned `slurmrestd` endpoints;
6. use a dedicated non-root POSIX service identity;
7. place workload SDKs and compilers only in the developer target;
8. configure application-specific resource, QOS, and partition bounds; and
9. reuse the local acceptance and production security gates.

## 12. Primary references

- [Red Hat UBI content and repositories](https://access.redhat.com/articles/4238681)
- [Red Hat .NET 10 on RHEL 10](https://docs.redhat.com/en/documentation/net/10.0/html/getting_started_with_.net_10.0_on_rhel_10/index)
- [SchedMD REST API](https://slurm.schedmd.com/rest_api.html)
- [SchedMD REST quick start](https://slurm.schedmd.com/rest_quickstart.html)
- [SchedMD JWT authentication](https://slurm.schedmd.com/jwt.html)
- [SchedMD job launch design](https://slurm.schedmd.com/job_launch.html)
- [Podman Compose documentation](https://docs.podman.io/en/latest/markdown/podman-compose.1.html)
