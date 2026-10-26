# FinSim Slurm reference implementation plan

Status: implementation plan derived from the approved design

## 1. Delivery rules

- Work in small commits with a runnable or statically verifiable state.
- Adapt security-reviewed Slurm assets from the POC and record their source
  commit instead of silently copying them.
- Keep application code independent of Slurm CLI executables.
- Keep secrets out of Git, image layers, process arguments, and logs.
- Pin source versions, image inputs, .NET SDK feature band, NuGet packages,
  and the Slurm OpenAPI version.
- Do not claim production readiness solely from the rootless local topology;
  keep production deployment prerequisites explicit.

## 2. Phase A: Slurm foundation

Files:

- `build/versions.conf`
- `infra/slurm/scripts/`
- `config/slurm/`
- `scripts/cluster-entrypoint.sh`
- `scripts/cluster-healthcheck.sh`
- `Containerfile`

Tasks:

1. Import the POC's pinned MUNGE/Slurm RPM build and manifest verification.
2. Adapt runtime configuration to create `finsim` UID/GID 64032 instead of
   the old workflow identity.
3. Retain distinct `munge`, `slurm`, `slurmrestd`, and `mysql` identities and
   root `slurmd` with non-root job launch.
4. Install .NET/ASP.NET 10 runtime in the shared runtime stage.
5. Preserve simulation versus production configuration separation.
6. Verify shell syntax, pinned values, runtime plugins, and file modes.

Commit: `build: establish FinSim Slurm runtime foundation`

## 3. Phase B: managed solution and image layers

Files:

- `global.json`
- `Directory.Build.props`
- `FinSim.slnx`
- `src/FinSim.Contracts/`
- `src/FinSim.Infrastructure/`
- application projects under `src/`
- named `finsim-app` and `finsim-dev` Containerfile targets

Tasks:

1. Target .NET 10 with nullable references, warnings as errors, deterministic
   builds, invariant globalization disabled, and locked dependency restore.
2. Define bounded request/result records and server-generated cache keys.
3. Publish framework-dependent applications into immutable `/opt/finsim` role
   directories.
4. Copy only publish output to `finsim-app`; install SDK and diagnostics only
   in `finsim-dev`.
5. Add image-content checks proving SDK/source exclusion from production.

Commit: `build: add shared FinSim application and development layers`

## 4. Phase C: cache boundary

Files:

- `src/FinSim.Infrastructure/Cache/`
- `src/FinSimCache/`
- cache-focused tests

Tasks:

1. Implement `IKeyValueStore` and an HTTP client using bounded JSON bodies.
2. Implement FinSimCache with validated keys, create-only writes, matching
   digest replay, atomic persistence, and no arbitrary list/path endpoint.
3. Validate cache JWT issuer, audience, signature, expiry, and allowed
   algorithms.
4. Add readiness and liveness endpoints that do not disclose configuration.
5. Test traversal rejection, collisions, replay, size limits, and malformed
   JSON.

Commit: `feat: add authenticated FinSim cache boundary`

## 5. Phase D: Slurm REST and proxy

Files:

- `src/FinSim.Infrastructure/Slurm/`
- `src/FinSimProxy/`
- proxy and Slurm-client tests

Tasks:

1. Implement the pinned `/slurm/v0.0.45` submit, query, and cancel calls.
2. Read Slurm JWTs only from private regular files and reload per request.
3. Classify definitive rejection, transient unavailability, and ambiguous
   mutation outcomes without logging bodies or tokens.
4. Require an idempotency key and persist workflow/submission intent before
   submission.
5. Reconcile an ambiguous response by exact unique job comment.
6. Validate external client JWT and enforce a submit/status/cancel role.
7. Bound requested scenarios, chunk size, concurrency, resources, and time.

Commit: `feat: submit FinSim workflows through Slurm REST`

## 6. Phase E: master and worker

Files:

- `src/FinSim/`
- master/worker tests

Tasks:

1. Implement `FinSim master` to fetch input, create chunk keys, and persist
   durable array-submission intent.
2. Submit one throttled worker array through Slurm REST.
3. Implement `FinSim worker` to derive its validated key from
   `SLURM_ARRAY_TASK_ID`, calculate count/sum/min/max, and create its result.
4. Poll array state with bounded backoff and a hard deadline.
5. Validate all results, aggregate them, and atomically publish one report.
6. Make retries idempotent and emit correlation-only structured logs.

Commit: `feat: run FinSim master and worker jobs`

## 7. Phase F: local topology and developer workflow

Files:

- `compose.yaml`
- `scripts/bootstrap-dev-secrets.sh`
- `scripts/cluster-up.sh`
- `scripts/cluster-down.sh`
- `scripts/submit-simulation.sh`
- `scripts/acceptance-test.sh`
- `teamcity/README.md`

Tasks:

1. Run MariaDB, `slurmdbd`, controller, token issuer, private `slurmrestd`,
   FinSimCache, FinSimProxy, and five application compute nodes.
2. Publish only FinSimProxy to host loopback.
3. Generate local-only MUNGE, Slurm JWT, cache JWT, and client JWT material
   beneath ignored `.runtime/` with private modes.
4. Keep manual `podman compose up/down` available and wrap it with checked,
   repeatable helper commands.
5. Exercise submission, polling, report correctness, cancellation, service
   identities, root-job rejection, and REST-only application scheduling.

Commit: `test: add local FinSim Slurm simulation`

## 8. Phase G: production contract and final verification

Files:

- `deploy/quadlet/`
- `docs/operations/`
- `README.md`

Tasks:

1. Provide role-specific Quadlet examples with read-only roots, capability
   allowlists, secret mounts, TLS/JWKS inputs, and direct non-root proxy start.
2. Document generic OIDC/JWKS and secret-delivery interfaces without a cloud
   SDK.
3. Document Couchbase adapter replacement, stable UID/GID, cgroup v2,
   external HA accounting, backup/restore, upgrades, and rollback.
4. Add SBOM, vulnerability, signature, and promotion expectations for CI.
5. Run unit/static/container/Compose acceptance checks and record exact
   limitations not exercised by the local rootless topology.

Commit: `docs: complete FinSim production deployment contract`

## 9. Acceptance criteria

The reference is complete when:

1. named runtime, application, and developer image targets build from UBI
   10.2;
2. five compute containers register and run FinSim jobs as UID/GID 64032;
3. a JWT-authenticated proxy request submits a master exclusively through
   `slurmrestd`;
4. the master submits a bounded worker array exclusively through
   `slurmrestd`;
5. master and workers exchange only validated cache keys;
6. the final report has the expected deterministic aggregate;
7. retry, ambiguity, cancellation, and invalid-token tests pass;
8. production images contain no SDK, compiler, source, local keys, or Compose
   control socket; and
9. documentation distinguishes verified behavior from production operator
   responsibilities.
