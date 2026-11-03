# Windows FinSimProxy and Shared Slurm REST Client Implementation Plan

## Purpose

This plan implements the approved design in
`2026-11-03-windows-proxy-slurm-client-design.md`. It preserves a fast
Linux-only developer loop while adding an end-to-end test using the actual
.NET Framework 4.8 FinSimProxy on Windows and the real Slurm workflow in WSL.

The plan spans two source repositories:

- **FinSim repository:** shared packages, Linux FinSim job, local cache,
  container cluster, test harness, WSL integration scripts and documentation;
- **legacy proxy repository:** .NET Framework 4.8 service integration, client
  API behavior, Windows credential provider and production Couchbase adapter.

The legacy repository path and CI build identifiers are deployment inputs and
must not be hard-coded in this repository.

## Delivery principles

1. Keep the existing acceptance test green at every merge boundary.
2. Introduce shared contracts before changing runtime behavior.
3. Make the new cache report authoritative before removing file-based proxy
   reads.
4. Run old and new paths in compatibility mode for one migration phase.
5. Never make a Windows integration shortcut part of production configuration.
6. Publish no control-plane port except the explicitly bound REST test
   endpoints, and bind those to loopback only.
7. Commit generated secrets, tokens, private keys and `.runtime` data nowhere.
8. Pin package versions, Slurm API version, image digest and application
   release in every integration result.

## Workstream ownership

| Area | Primary owner | Stable contract |
|---|---|---|
| Slurm cluster and `slurmrestd` | platform/HPC team | private TLS endpoint, API version, `finsim` identity |
| shared Slurm package | platform and FinSim teams | typed client API and error semantics |
| workflow contracts | FinSim and proxy teams | DTO schema, cache keys and state machine |
| Windows FinSimProxy | legacy proxy team | external business API and master orchestration |
| FinSim master/workers | FinSim team | chunking, worker array and report creation |
| Couchbase | data-platform team | CAS operations, durability, retention and access policy |
| cross-environment CI | build/platform team | Windows + WSL acceptance gate |

Normal FinSim job changes should require only a new FinSim release and job
tests. They should not require a proxy, cluster image or shared package change
unless a versioned contract changes.

## Phase 0: Baseline and contract capture

### FinSim repository

1. Run and retain results for:

   ```bash
   scripts/ci.sh verify
   scripts/ci.sh integration
   ```

2. Record the current Slurm version, `v0.0.45` data parser, runtime image
   digest, application release manifest and current REST request fixtures.
3. Add `scripts/export-slurm-openapi.sh` to generate the OpenAPI document from
   the exact built runtime image. Normalize only nondeterministic metadata,
   calculate SHA-256 and publish both as CI artifacts.
4. Add minimal checked-in fixtures for successful submit, rejected submit,
   job-array status, cancellation and malformed/oversized responses. Do not
   check in the complete generated API document unless its reviewed size and
   update process are acceptable.
5. Add a compatibility manifest such as
   `contracts/compatibility/slurm-26.05.5.json` containing the Slurm version,
   parser, OpenAPI hash and required endpoints.

### Gate

- Existing live workflow still returns the expected report and all jobs run as
  UID 64032.
- The OpenAPI artifact is reproducible from the promoted image.
- The required endpoints exist in the generated document.

### Suggested commit

`test: capture Slurm REST compatibility baseline`

## Phase 1: Create the shared workflow contract package

### Files

- create `src/FinSim.Workflow.Contracts/`;
- migrate transport DTOs and cache-key grammar from
  `src/FinSim.Contracts/`;
- add package metadata, source link, deterministic build and XML docs;
- add `tests/FinSim.Workflow.Contracts.Tests/`;
- update `FinSim.slnx`, lock files and `Directory.Build.props` as required.

### Requirements

1. Target `netstandard2.0;net8.0` or target `netstandard2.0` alone if the
   modern target provides no measured benefit.
2. Keep the public API consumable by an existing .NET Framework 4.8 project.
3. Replace APIs unavailable to `netstandard2.0`, including source-generated
   regular expressions and modern convenience guards, with explicit portable
   implementations.
4. Use simple transport DTOs. Do not expose modern runtime-specific JSON DOM
   types.
5. Add `workflow/<id>/report` and explicit workflow state values.
6. Version serialized contracts independently from assembly versions when a
   breaking wire change is possible.
7. Validate workflow IDs, chunk indexes, key length and terminal-state
   transitions in tests.
8. Pack the project and prove it can be referenced by a minimal `net48`
   consumer compiled on the Windows CI agent.

### Gate

- Serialization fixtures are identical on `net48` and `net8.0`.
- Package compatibility validation reports no target-specific public API drift.
- Existing FinSim tests pass after consuming the new package.

### Suggested commits

- `feat: add portable workflow contracts package`
- `refactor: consume shared workflow contracts`

## Phase 2: Extract the shared Slurm REST client

### Files

- create `src/FinSim.Slurm.Client/`;
- move and redesign
  `src/FinSim.Infrastructure/Slurm/SlurmRestClient.cs` and exception types;
- create `tests/FinSim.Slurm.Client.Tests/`;
- create a Windows `net48` consumer/test project in the legacy proxy repository;
- add packaging and API-compatibility checks to both pipelines.

### Public contract

Define typed models and interfaces resembling:

```text
ISlurmRestClient
  SubmitJobAsync(request, correlationId, cancellationToken)
  GetJobAsync(jobId, correlationId, cancellationToken)
  FindJobsByCommentAsync(comment, correlationId, cancellationToken)
  CancelJobAsync(jobId, correlationId, cancellationToken)
  PingAsync(cancellationToken)

ISlurmTokenProvider
  GetCredentialAsync(cancellationToken) -> user name + token
```

Final names are decided by API review, not copied mechanically from this
outline.

### Requirements

1. Multi-target `netstandard2.0;net8.0` and validate that both targets expose
   the same public API.
2. Inject `HttpClient`; never create one per request.
3. Read a fresh credential per request through `ISlurmTokenProvider`.
4. Enforce HTTPS unless an explicit internal-test option is enabled.
5. Allow only reviewed REST data-parser versions; start with `v0.0.45`.
6. Use one serializer behavior and fixture suite across targets. If the legacy
   proxy already standardizes on Newtonsoft.Json, prefer one reviewed
   Newtonsoft.Json version for this small package instead of maintaining two
   wire implementations.
7. Set exact `application/json` media types expected by Slurm.
8. Bound response bytes, parsing depth, connections, per-request timeout and
   total operation deadline.
9. Validate user name, job ID, correlation ID, partition, feature and comment
   marker inputs.
10. Map failures into stable categories:
    - definitive client rejection;
    - read-side unavailability;
    - ambiguous mutation outcome;
    - invalid/unsupported response.
11. Never automatically retry a submission or cancellation with an ambiguous
    outcome.
12. Redact tokens, headers and submitted environment values from all messages.
13. Contract-test required paths and fields against the captured OpenAPI
    artifact.

### Gate

- Linux `net8.0` tests and Windows `net48` tests execute the same fixtures.
- An intentionally lost submit response produces an ambiguous result and does
  not submit twice.
- Expired, malformed and oversized tokens/responses fail without disclosure.
- Existing master and harness behavior passes after adopting the package.

### Suggested commits

- `feat: add portable Slurm REST client package`
- `refactor: migrate FinSim master to shared Slurm client`

## Phase 3: Make the workflow store authoritative

### FinSim repository

1. Extend the cache interface and FinSimCache to support the report key and
   explicit terminal workflow state with conditional writes.
2. Change the master success order:
   - validate every worker result;
   - create the complete report;
   - conditionally store `workflow/<id>/report`;
   - transition workflow state to `SUCCEEDED`;
   - optionally write a diagnostic file.
3. Change failure handling to store a bounded failure report and terminal
   state when the cache remains reachable.
4. Change the harness to read the report through the cache interface, not
   `/finsim-data/reports`.
5. Keep file output temporarily behind a compatibility option while tests and
   operations move to the cache contract.
6. Add adapter contract tests covering insert, get, replay, conflict, stale CAS,
   unavailable store and malformed content.

### Legacy proxy repository

1. Add or update its Couchbase workflow-store adapter.
2. Poll Slurm for scheduler state and Couchbase for workflow/report state.
3. Return success only when a valid terminal report exists.
4. Keep client-facing API compatibility through an adapter if existing response
   objects differ from the shared DTOs.

### Gate

- Windows does not require access to `.runtime/data` or any Linux filesystem.
- Removing the report volume from the harness does not break successful status
  or report retrieval.
- Conflicting report writes fail closed.
- Slurm `COMPLETED` without a report is reported as finalization/incomplete, not
  success.

### Suggested commits

- `feat: store authoritative workflow reports in cache`
- `refactor: remove proxy report filesystem dependency`

## Phase 4: Introduce the immutable job launcher contract

### FinSim repository

1. Add a root-owned launcher under a stable image path such as
   `/usr/local/libexec/finsim/run-release`.
2. Accept only an allow-listed release ID, job role and required identifiers.
3. Reject separators, traversal, symbolic-link releases, writable manifests,
   wrong ownership, manifest hash mismatch and non-regular executables.
4. Resolve the executable exclusively below
   `/finsim-app/releases/<release-id>/`.
5. Execute it as the `finsim` user with a minimal environment.
6. Change master and worker Slurm scripts to invoke the launcher instead of an
   arbitrary submitted executable path.
7. Change FinSimProxy/harness configuration from
   `Paths:ApplicationExecutable` to an allow-listed release ID.
8. Add negative launcher tests and release-tamper acceptance checks.

### Legacy proxy repository

Store an allow-listed release ID/job profile in service configuration. Never
accept a Linux path or command from a client request.

### Gate

- Windows performs no Linux path conversion or file existence check.
- A release ID is sufficient to run the same immutable artifact locally and in
  staging.
- Path traversal and writable/tampered releases cannot execute.

### Suggested commit

`feat: submit jobs through immutable release launcher`

## Phase 5: Separate proxy-facing and job-facing configuration

### Configuration model

Define explicit values equivalent to:

```text
ProxySlurmEndpoint       Windows proxy -> local/cloud slurmrestd
ProxyCacheEndpoint       Windows proxy -> local/cloud cache
JobSlurmEndpoint         master -> cluster-internal slurmrestd
JobCacheEndpoint         master/workers -> cluster-internal cache
ApplicationReleaseId     allow-listed immutable release
MasterSlurmTokenPath     Linux path projected on master nodes
JobCacheTokenPath        Linux path projected on compute nodes
ServiceCaPath            Linux path projected on compute nodes
```

Names in the legacy proxy should follow its established configuration style,
but their meanings must remain separate.

### FinSim repository

1. Update harness settings and job request construction so only job-facing
   values enter the Slurm environment.
2. Validate every endpoint scheme, host and port. Production requires HTTPS.
3. Ensure a Windows `localhost` value can never be injected into a Linux job.
4. Split local Slurm credentials into separately rotated proxy and master files.
5. Keep credentials off worker nodes.
6. Add configuration contract tests and reject unknown/deprecated settings
   after the compatibility window.

### Gate

- Tests prove proxy and master can use different routes to the same service.
- Jobs contain only cluster-internal URLs and Linux credential paths.
- Proxy credentials do not appear in job requests.

### Suggested commit

`refactor: separate proxy and job endpoint contracts`

## Phase 6: Make the Linux proxy an explicit test harness

### FinSim repository

1. Rename the project and executable to `FinSimProxyHarness`, or retain the
   source directory temporarily while changing the assembly, service and
   documentation names in one compatibility phase.
2. State clearly that it is not the production proxy.
3. Put the harness service behind an explicit Compose profile such as
   `linux-harness`.
4. Update `cluster-up.sh` with explicit modes:
   - `--mode linux-harness` for normal Linux-only integration;
   - `--mode windows-proxy` for the actual Windows service.
5. Make readiness and acceptance expectations mode-specific.
6. Keep a common core topology for database, accounting, controller, REST,
   cache, token agents and compute nodes.

### Gate

- The default CI integration mode still exercises the complete workflow.
- Windows mode starts no Linux proxy/harness process.
- Documentation never represents the harness as the production service.

### Suggested commit

`refactor: distinguish proxy harness from Windows service`

## Phase 7: Add the Windows-to-WSL integration overlay

### FinSim repository

1. Add `compose.windows-proxy.yaml` that publishes only:
   - `127.0.0.1:16820` to `restd:6820`;
   - `127.0.0.1:18090` to `cache:8090`.
2. Do not publish MariaDB, SlurmDBD, controller or Slurmd ports.
3. Extend local certificate generation with SANs required for `localhost` and
   the cluster-internal service names.
4. Require TLS for both externally published integration endpoints.
5. Add `scripts/windows-integration-prepare.sh` to validate the WSL side and
   write only non-secret endpoint/release metadata under `.runtime/windows/`.
6. Add `tools/windows/Install-FinSimDevCa.ps1` for an explicit current-user CA
   trust action with fingerprint confirmation and removal instructions.
7. Add `tools/windows/Sync-FinSimDevCredential.ps1` that:
   - invokes a WSL-side atomic copy into a configured Windows credential path;
   - never emits the credential to stdout or command arguments;
   - grants access only to the selected Windows user/service identity;
   - preserves last-known-good credentials if rotation fails; and
   - fails before expiration rather than silently using an expired token.
8. Add `tools/windows/Test-FinSimLocalPrerequisites.ps1` for WSL distribution,
   endpoint, CA, token-file ACL and proxy readiness checks.
9. Validate the rendered Compose model so loopback publication cannot become
   `0.0.0.0` through an unset variable.

Windows-to-WSL localhost forwarding follows Microsoft's documented WSL
networking behavior: <https://learn.microsoft.com/windows/wsl/networking>.

### Gate

- Windows can reach both HTTPS readiness endpoints on localhost.
- Another LAN host cannot reach the endpoints.
- No private key or token appears in process listings, logs or Git status.
- The Linux harness is absent in Windows mode.

### Suggested commits

- `feat: add Windows proxy WSL integration topology`
- `feat: add secure Windows local credential preparation`

## Phase 8: Integrate the actual .NET Framework proxy

### Legacy proxy repository

1. Reference pinned `FinSim.Slurm.Client` and
   `FinSim.Workflow.Contracts` packages from the internal feed.
2. Replace its local Slurm HTTP implementation with the shared client.
3. Implement a Windows rotating-file token provider for local development and
   the approved enterprise issuer/credential provider for production.
4. Preserve existing client authentication and public API behavior.
5. Add explicit proxy-facing and job-facing endpoint settings.
6. Use the Couchbase workflow adapter for input, state, job IDs and report.
7. Submit only an allow-listed release ID/job profile.
8. Preserve the intent-before-submit and exact-marker reconciliation protocol.
9. Apply bounded concurrency, deadlines and cancellation propagation.
10. Add structured logs containing correlation/workflow/job/release IDs but no
    payloads or credentials.
11. Add a startup/readiness check for configuration, token provider, cache and
    Slurm REST reachability without submitting a job.

### Windows tests

- package load and assembly-binding test under .NET Framework 4.8;
- shared serialization fixtures;
- token rotation without service restart;
- TLS chain and hostname failure;
- idempotent replay and conflicting key;
- ambiguous master submission reconciliation;
- cache/Slurm unavailable mappings;
- cancellation and report retrieval;
- backwards-compatible public client responses.

### Gate

- No duplicated Slurm HTTP code remains in the legacy proxy.
- The actual service submits to the WSL cluster and returns a report to its
  original client API.
- Production configuration contains no WSL paths, localhost endpoints or
  development CA.

## Phase 9: Cross-environment acceptance automation

### FinSim repository

Add `tools/windows/Invoke-FinSimEndToEnd.ps1` or an equivalent TeamCity entry
point. It should:

1. select explicit image and application release versions;
2. prepare and validate WSL runtime state;
3. start the cluster in Windows-proxy mode;
4. synchronize a fresh proxy credential;
5. start or verify the actual Windows service through the legacy repository's
   supported mechanism;
6. submit through the public FinSimProxy API with an idempotency key;
7. poll only through FinSimProxy;
8. retrieve and validate the final report through FinSimProxy;
9. inspect Slurm accounting for one master and the expected worker array, all
   owned by `finsim`;
10. test cancellation and at least one controlled worker failure;
11. verify no forbidden ports are published; and
12. collect redacted diagnostics and shut down only resources created by the
    test.

Do not make the test responsible for deleting persistent state automatically.
Use a unique workflow namespace and retain failed artifacts according to CI
retention policy.

### Required negative scenarios

- expired proxy Slurm token;
- token rotation during polling;
- duplicate client request;
- conflicting idempotency request;
- dropped master-submit response;
- dropped worker-array-submit response;
- cache unavailable before and after submission;
- Slurm REST unavailable;
- one worker failure and timeout;
- malformed or missing worker result;
- master completes without a report;
- cancellation race with completion; and
- dynamic node drain during a worker array.

### Gate

The same FinSim release that passes Linux-only integration passes through the
actual Windows proxy without rebuilding or source changes.

### Suggested commit

`test: add Windows proxy WSL end-to-end acceptance`

## Phase 10: CI and package promotion

### Linux pipeline

- locked restore and build of modern targets;
- unit, fixture and OpenAPI contract tests;
- package creation without publication;
- container build and provenance checks;
- Linux harness acceptance;
- SBOM and image/package vulnerability policy.

### Windows pipeline

- locked restore of the exact package versions;
- .NET Framework 4.8 build;
- package consumer/API compatibility tests;
- proxy unit and component tests;
- Authenticode/package-signing policy where required.

### Windows + WSL pipeline

- actual proxy plus WSL cluster acceptance;
- versions and digests recorded as one test manifest;
- redacted logs retained;
- no shared mutable package feed snapshot.

### Promotion policy

Publish immutable prerelease packages first. Promote a package version only
after Linux and Windows consumer tests pass. A production compatibility matrix
must bind:

```text
proxy build
FinSim.Slurm.Client version
FinSim.Workflow.Contracts version
Slurm version / REST parser
runtime image digest
FinSim release digest
```

Breaking package or wire changes require a major version and a documented
dual-read/dual-write migration where state persists across versions.

## Phase 11: Cloud staging and production rollout

1. Deploy multiple `slurmrestd` instances on a private cluster network behind
   a TLS load balancer with bounded connections and health checks.
2. Permit the Windows proxy network and master compute security group to reach
   only the private REST endpoint and Couchbase endpoints.
3. Keep ports 3306, 6817, 6818 and 6819 inaccessible from the proxy/client
   network.
4. Configure the reviewed RS256 issuer/JWKS, complete UID/GID claims,
   `use_jwt_client_ids_only`, short token life and key rotation procedure.
5. Issue separate proxy and master credentials. Project the master credential
   only to master-capable allocations; workers receive none.
6. Deploy the immutable FinSim release and verify its launcher manifest on all
   eligible nodes before enabling submission.
7. Run the cloud-staging suite with real Couchbase and the production network
   path.
8. Canary a bounded workload, verify Slurm accounting, Couchbase durability,
   logs, metrics and report retrieval, then expand gradually.
9. Retain the previous proxy package set, application release and runtime image
   for rollback. Do not roll back workflow schema without a compatible reader.

SchedMD's REST and JWT security guidance is normative for this rollout:

- <https://slurm.schedmd.com/rest.html>
- <https://slurm.schedmd.com/jwt.html>

## Documentation updates during implementation

Update operational documents in the same commit as their behavior:

- `README.md`: identify the Linux proxy as a harness and link Windows mode;
- `docs/ARCHITECTURE.md`: replace shared-file result flow with Couchbase;
- `docs/USER_GUIDE.md`: add Linux and Windows test workflows;
- `docs/PRODUCTION.md`: document Windows proxy connectivity and credentials;
- `docs/CI.md`: document the three pipelines and compatibility manifest;
- `docs/SCRIPTS.md`: document every new Bash and PowerShell script;
- `docs/DEPENDENCIES.md`: record shared-package and serializer provenance;
- `docs/REVIEW.md`: track migration limitations until all gates pass.

The implementation plan and design are historical decision records. Current
runbooks remain the operational source of truth after implementation.

## Definition of done

The work is complete only when:

- both .NET Framework 4.8 FinSimProxy and .NET 8+ FinSim use the same versioned
  Slurm REST client package;
- the Windows proxy submits only the master and the master submits workers;
- clients communicate only with FinSimProxy;
- the final report is retrievable from the authoritative workflow store without
  a Windows-to-Linux filesystem dependency;
- local Linux-only and actual Windows-proxy acceptance suites pass;
- the same FinSim release passes cloud staging;
- tokens are short-lived, separate, redacted and role-filtered;
- workers have no Slurm token;
- only reviewed HTTPS endpoints are reachable across trust boundaries;
- package, API, image, proxy and release versions are traceable; and
- production rollout and rollback procedures have been exercised.
