# Implementation details

## Repository boundaries

`FinSim.Contracts` contains transport records and deterministic cache keys.
`FinSim.Infrastructure` contains security, cache, and Slurm REST adapters.
`FinSimProxy` is the authenticated external API. `FinSim` is the same executable
for master and worker modes. `FinSimCache` is a local Couchbase substitute and
is excluded from the production application image. `FinSim.Tests` is an
executable contract suite with no external test framework dependency.

Shell code is split by responsibility:

| Script | Responsibility |
|---|---|
| `build-rpms.sh` | Download pinned upstream sources, build MUNGE/Slurm RPMs and SRPMs, publish an exact SHA-256 manifest |
| `install-runtime.sh` | Verify the manifest and install only the required runtime RPM subset and libraries |
| `configure-runtime.sh` | Create fixed identities/directories and install immutable non-secret assets |
| `cluster-entrypoint.sh` | Validate runtime mounts/profile and start exactly one role |
| `bootstrap-dev-secrets.sh` | Generate disposable local keys/tokens without displaying them |
| `cluster-up.sh` / `cluster-down.sh` | Manual local lifecycle |
| `submit-simulation.py` | Minimal JWT REST client for the local proxy |
| `acceptance-test.sh` | End-to-end and privilege-boundary verification |

## API contract

All `/v1/simulations` routes require a validated RS256 bearer token with issuer,
audience, expiration, and scope `finsim.submit`. Request bodies are capped at
256 KiB.

| Method | Path | Result |
|---|---|---|
| `POST` | `/v1/simulations/` | Validate 1-10,000 integers, require `Idempotency-Key`, return HTTP 202 |
| `GET` | `/v1/simulations/{id}` | Owner-scoped scheduler/report status |
| `GET` | `/v1/simulations/{id}/report` | Owner-scoped terminal JSON report |
| `DELETE` | `/v1/simulations/{id}` | Cancel matching master/worker jobs, HTTP 202 |
| `GET` | `/health/live` | Process liveness only |
| `GET` | `/health/ready` | Required token files and data root are usable |

The authenticated `sub` is never used as a filesystem name or Slurm argument.
Its SHA-256 digest is the ownership record. Status and report calls compare the
digest in constant time and return not-found to a different subject.

## Idempotent submission

The workflow UUID is UUIDv5-shaped deterministic data derived from SHA-256 of
`subject + newline + Idempotency-Key`. Before any mutation, the proxy stores:

1. owner digest;
2. immutable input;
3. master submission intent with a unique Slurm comment marker.

It then calls `POST /slurm/v0.0.45/job/submit`. A definitive 4xx is rejected.
A network error or 5xx is ambiguous because the controller may have accepted
the job; it is never retried blindly. A repeated request searches Slurm for the
exact marker. Exactly one match is reconciled, none remains unknown, and more
than one requires operator attention. The master uses the same intent/marker
algorithm for the worker array.

This is an application-level outbox/reconciliation pattern. It avoids duplicate
financial work without depending on a cloud queue or transactional Slurm API.

## Cache contract

Logical keys are grammar-validated and mapped to SHA-256 filenames, so user
data cannot select a filesystem path. Values are JSON objects capped at 128
KiB. Writes are create-only and atomic:

- identical replay -> success;
- same key, different bytes -> conflict;
- symlinks/non-regular files -> rejected;
- partial temporary files -> never become the key.

Important keys include:

```text
workflow/<uuid>/state
workflow/<uuid>/input
workflow/<uuid>/submission/master/intent
workflow/<uuid>/submission/master/job
workflow/<uuid>/submission/workers/intent
workflow/<uuid>/submission/workers/job
workflow/<uuid>/chunk/<six-digit-index>
workflow/<uuid>/result/<six-digit-index>
```

Production replaces the HTTP cache simulator with Couchbase or another durable
store while preserving `IKeyValueStore` create-if-absent/get/delete semantics,
key grammar, value bounds, TLS, workload identity, and expiry policy.

## Master and worker execution

The proxy submits the master to the configured orchestrator partition/feature.
The master receives token **file paths**, never token values, in its environment.
It reads input, writes deterministic chunks, and submits one array expression
`0-(N-1)%maxParallel` to the worker partition/feature. One array avoids N
submission RPCs and the percent limit bounds instantaneous fanout.

Workers receive only workflow ID, cache URL/token path, data root, and the
Slurm array task ID. Each worker verifies that the loaded chunk's workflow and
index match its allocation, calculates count/sum/min/max, and create-writes one
result. Worker retries are safe when the result is identical.

The master polls Slurm with exponential delay capped at ten seconds. A terminal
worker failure fails the workflow. On success it validates every keyed result,
aggregates with checked arithmetic, writes a mode-0600 temporary report with
write-through semantics, flushes it, and atomically renames it. Only reports
whose workflow, terminal status, size, type, and private mode validate are
returned by the proxy.

## Configuration

ASP.NET double underscores map environment variables to configuration sections.
Production-relevant proxy settings are:

| Key | Meaning |
|---|---|
| `Jwt__Authority` or `Jwt__JwksFile` | Exactly one trust source |
| `Jwt__Issuer`, `Jwt__Audience` | Required token claims |
| `Cache__BaseUrl`, `Cache__TokenFile` | HTTPS cache boundary and proxy credential |
| `Cache__JobTokenFile` | Path visible on scheduled nodes |
| `Slurm__BaseUrl`, `Slurm__TokenFile`, `Slurm__User` | HTTPS Slurm REST boundary |
| `Slurm__MasterPartition`, `Slurm__MasterFeature` | Credential-bearing orchestrator pool |
| `Slurm__WorkerPartition`, `Slurm__WorkerFeature` | Credential-free worker pool |
| `Paths__DataRoot` | Shared private report/log root |

`AllowInsecureHttp` flags are false by default and exist only for the internal
single-host simulation. Scheduler names accept a narrow literal grammar.
Correlation IDs must be UUIDs; a missing/invalid client value is replaced and
the selected value is returned.

## Identity and permissions

| Identity | UID/GID | Process |
|---|---:|---|
| `munge` | 64029 | `munged` |
| `slurm` | 64030 | `slurmctld`, `slurmdbd` |
| `slurmrestd` | 64031 | `slurmrestd` |
| `finsim` | 64032 | proxy, cache simulator, master and workers |

Controller/REST images keep PID 1 as a root staging supervisor so mounts can be
validated/copied to tmpfs and ownership can be established; the actual daemons
are dropped to their service users. `slurmd` remains root in production because
Slurm must create cgroups/namespaces and switch to allocated users. That is the
upstream execution model, not permission for jobs to run as root:
`DisableRootJobs=YES`, accounting associations, and UID 64032 enforce workload
identity. Use `ps`, not the default identity of an interactive `podman exec`,
to determine a daemon's real UID.

## Test layers

The container build performs locked restore, Release compilation with warnings
as errors, and managed contract tests. Python tests exercise the shell artifact
trust logic. `acceptance-test.sh` additionally proves:

- five nodes are healthy;
- unauthenticated proxy access returns 401;
- root job submission is rejected;
- the REST-only workflow returns the expected report;
- accounting attributes every master/worker job to `finsim`;
- daemons use their intended UIDs;
- the production app image has no SDK, source, cache simulator, or MariaDB.
