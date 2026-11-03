# Windows FinSimProxy and Shared Slurm REST Client Design

## Status

Approved on 2026-11-03. This document defines the target architecture and
local-test model. It does not authorize exposing Slurm control-plane ports to
untrusted networks.

## Context

The production FinSimProxy is an existing .NET Framework 4.8 Windows service.
It is the only endpoint used by external clients. FinSim is a .NET 8-or-later
Linux batch application deployed to a Slurm cluster. Its master job divides
work, submits a worker array, waits for the workers and creates the final
report. Production uses Couchbase for workflow inputs, chunks, results and
state; the repository's FinSimCache is only a local substitute.

FinSimProxy and the FinSim master both submit and inspect jobs with the Slurm
REST API. They have different orchestration responsibilities but require the
same transport, authentication headers, API-version handling, validation and
error semantics.

The current proof of concept differs from the target in four important ways:

1. its demo FinSimProxy is a modern .NET Linux application in the runtime
   container rather than the legacy Windows service;
2. proxy-local and job-local URLs and filesystem paths are conflated;
3. the proxy validates Linux release paths and reads the final report from a
   shared filesystem; and
4. the Slurm REST client is embedded in the modern .NET infrastructure project
   and cannot be consumed directly by .NET Framework 4.8.

## Architectural invariants

- External clients communicate only with FinSimProxy.
- FinSimProxy and the FinSim master communicate with `slurmrestd`, never
  directly with the `slurmctld` RPC port.
- `slurmrestd` may be colocated with a controller for a small environment, but
  remains a separate service and security boundary.
- Production places redundant `slurmrestd` instances behind a private TLS load
  balancer. It is not internet-facing.
- FinSimProxy submits only the master. The master is the only component that
  submits workers.
- Workers never receive a Slurm token and cannot submit jobs.
- All jobs run as the dedicated `finsim` POSIX identity.
- Couchbase, not a cross-OS shared filesystem, is the authoritative workflow
  data and result exchange.
- Local and production use the same workflow, REST client, cache key grammar,
  application release identity and failure semantics.

SchedMD documents `slurmrestd` as the REST interface to Slurm and warns that it
is not intended to be directly internet-facing. It recommends a trusted
network, TLS protection, bounded connections and short-lived credentials:
<https://slurm.schedmd.com/rest.html>.

## Target workflow

```text
External client
      |
      | FinSim business API and client authentication
      v
Windows FinSimProxy (.NET Framework 4.8)
      |                                  |
      | HTTPS + short-lived Slurm JWT    | Couchbase API
      v                                  v
Private TLS endpoint                 Workflow store
      |
      v
slurmrestd pool
      |
      v
slurmctld
      |
      +--> schedule FinSim master (.NET 8+)
              |                            |
              | HTTPS + separate JWT       | Couchbase API
              v                            v
          slurmrestd                    chunks/results
              |
              +--> schedule throttled worker array
              +--> poll worker-array state
              +--> aggregate result keys
              +--> write report and terminal state
```

FinSimProxy authenticates the client, validates the business request, creates
an idempotent workflow record, stores input, submits one master, supports
status and cancellation, and returns the authoritative report from Couchbase.
It does not require a Linux mount or arbitrary executable path.

The master reads input, writes chunk records, creates an idempotent worker
submission intent, submits one throttled job array, reconciles ambiguous
submissions, waits for terminal state, validates each result, aggregates the
report and stores the report and terminal workflow state. A worker reads one
chunk and conditionally writes one result.

## Shared Slurm client package

Create an internally versioned NuGet package named `FinSim.Slurm.Client` with
targets `netstandard2.0;net8.0`. .NET Standard 2.0 is the compatibility target
supported by .NET Framework 4.8 and modern .NET; the modern target permits
runtime-specific implementation improvements while package API compatibility
is validated. Microsoft recommends .NET Standard 2.0 when sharing libraries
between .NET Framework and modern .NET:
<https://learn.microsoft.com/dotnet/standard/net-standard>.

The public surface is typed and deliberately small:

- submit a job;
- query one job or an array;
- list jobs for reconciliation by an exact comment marker;
- cancel a job;
- health/ping diagnostics needed by readiness;
- stable response DTOs and exception types; and
- an injected `ISlurmTokenProvider` returning the fixed user identity and a
  current short-lived token.

The package owns:

- the allow-listed Slurm REST API version, initially `v0.0.45`;
- URL construction and encoding;
- `X-SLURM-USER-NAME`, `X-SLURM-USER-TOKEN` and correlation headers;
- request/response size bounds;
- timeouts and cancellation;
- schema-tolerant parsing of only fields FinSim consumes;
- validation of job identifiers and scheduler names;
- separation of definitive 4xx rejection, read unavailability and ambiguous
  write transport failure; and
- redaction so credentials and payload data never enter exception messages or
  logs.

The package does not own workflow state, Couchbase access, retry policy for an
ambiguous mutation, partitions, feature constraints, release selection or
token issuance. Both orchestrators persist an intent before mutation and use
the shared exact-comment query to reconcile a response that may have been lost
after Slurm accepted the job.

The client uses typed request models rather than exposing
`System.Text.Json.Nodes` or framework-specific JSON types in its public API.
One serializer and one fixture suite must produce byte-equivalent JSON on both
targets. The generated OpenAPI specification from the promoted Slurm image is
captured as a build artifact and contract-tested. SchedMD identifies the
runtime-generated OpenAPI specification as the primary client contract:
<https://slurm.schedmd.com/rest_clients.html>.

### Alternatives rejected

Generating the entire client separately for each runtime gives broad endpoint
coverage but produces a large, volatile model surface. Maintaining independent
Windows and Linux clients avoids a package migration initially but guarantees
drift in authentication, errors and ambiguous-submit handling. A small shared
package matches the stable subset actually used by FinSim.

## Shared workflow contracts

Create a second package, `FinSim.Workflow.Contracts`, compatible with
`netstandard2.0` and modern .NET. It contains transport-only DTOs, workflow
states and the cache-key grammar. It contains no scheduler or business policy.
Where language compatibility makes modern records or source-generated regular
expressions unsuitable, use conventional immutable DTO classes and explicit
validation.

The authoritative cache keys are:

```text
workflow/<workflow-id>/input
workflow/<workflow-id>/state
workflow/<workflow-id>/intent/master
workflow/<workflow-id>/job/master
workflow/<workflow-id>/intent/workers
workflow/<workflow-id>/job/workers
workflow/<workflow-id>/chunk/<index>
workflow/<workflow-id>/result/<index>
workflow/<workflow-id>/report
```

Writes use Couchbase CAS/insert semantics so retries are idempotent and a
conflicting payload fails closed. The final report key is authoritative for
FinSimProxy. A cluster-local report file may exist for diagnostics, but is not
part of the Windows-to-cluster contract.

Production may use native Couchbase SDK adapters in each runtime. The local
FinSimCache continues to implement the same logical operations over HTTP. The
contract packages do not couple callers to either adapter.

## Application release contract

The external client cannot provide a command or filesystem path. FinSimProxy
selects an allow-listed release ID and job type from deployment configuration.
The Slurm job environment contains a cluster-side executable path resolved from
that release ID, for example:

```text
/finsim-app/releases/<release-id>/FinSim/FinSim
```

Only the cluster validates that the manifest and executable exist. The Windows
proxy neither converts nor probes Linux paths. Production deployment ensures
the selected immutable release is visible on master and worker nodes before
the release becomes eligible for submission.

## Endpoint separation

Proxy-facing endpoints and job-facing endpoints are separate configuration
contracts even when they reach the same backing services:

| Purpose | Windows local integration | Linux job environment |
|---|---|---|
| Slurm REST | `https://localhost:16820/` | `https://restd:6820/` |
| Cache | `https://localhost:18090/` | `https://cache:8090/` |
| Data exchange | cache keys | cache keys |
| Application | allow-listed release ID | immutable Linux path |

FinSimProxy uses the first set for its own calls and injects only the second set
into the master job. This prevents Windows URLs and paths from leaking into a
Linux allocation.

## Authentication and secrets

Authentication domains remain independent:

| Connection | Credential |
|---|---|
| client to FinSimProxy | existing application authentication and authorization |
| FinSimProxy to Slurm | short-lived `finsim` Slurm JWT dedicated to the proxy |
| master to Slurm | separate short-lived `finsim` Slurm JWT projected only to master-capable nodes |
| worker to Slurm | none |
| proxy/master/worker to cache | distinct least-privilege cache credentials |

`ISlurmTokenProvider` has environment-specific implementations. Windows uses
the enterprise issuer/credential agent in production and a Windows-ACL
protected rotating file for local integration. Linux reads a projected
read-only credential file. Tests use an in-memory provider. The shared library
reads credentials per request and never logs or caches them beyond one request.

The local credential synchronization script copies atomically from WSL to a
configured Windows user/service credential directory without writing the token
to stdout, command arguments, source control or environment variables. It then
restricts the file ACL. This mechanism is development-only.

Production uses a controlled RS256 issuer/JWKS configuration, complete identity
claims, `use_jwt_client_ids_only`, fixed UID/GID 64032 and short expiration.
Signing authority is cluster-root trust and must be operated accordingly. See
<https://slurm.schedmd.com/jwt.html>.

## Local development and test topologies

### Level 1: FinSim unit and component tests

Run in `finsim-dev` without a live cluster. Test chunking, aggregation, cache
CAS behavior, worker retry, request serialization, Slurm response fixtures,
timeouts and error mapping. This is the normal seconds-long developer loop.

### Level 2: Linux-only cluster integration

Run MariaDB, SlurmDBD, controller, `slurmrestd`, token agent, FinSimCache and
compute containers in WSL Podman. Use an explicitly named
`FinSimProxyHarness`, not a component represented as the production proxy. The
harness consumes the same workflow and Slurm packages and exercises the real
master/worker path. This remains the normal CI integration test.

### Level 3: Actual Windows FinSimProxy integration

Run the actual .NET Framework 4.8 service on Windows. Run the cluster,
FinSimCache and FinSim releases in WSL Podman without the harness. A Compose
override publishes only the TLS-protected local integration endpoints:

```yaml
restd:
  ports:
    - "127.0.0.1:16820:6820"
cache:
  ports:
    - "127.0.0.1:18090:8090"
```

Windows reaches WSL-published services through localhost. See
<https://learn.microsoft.com/windows/wsl/networking>. The generated local CA is
installed only in the Windows current-user trust store through an explicit
PowerShell step. Private keys remain in WSL role-filtered files. No controller,
Slurmd, SlurmDBD or MariaDB port is published.

A real client or test client calls the Windows service. The acceptance test
verifies master placement, worker-array completion, cache results, final report,
accounting, cancellation, identities and the absence of unintended host port
publication.

### Level 4: cloud staging

Use the production private network, TLS path, issuer, Couchbase service,
immutable image digest and release digest on a small cluster. WSL success is
necessary but never sufficient for production promotion.

## CI model

The Linux pipeline builds FinSim and the modern package targets, runs unit and
contract tests, builds images and executes the Linux-only cluster acceptance
test. The Windows pipeline builds the .NET Framework proxy, consumes the exact
internal package versions and runs proxy/package tests. A dedicated Windows
agent with WSL and rootless Podman performs the cross-environment acceptance
test. Production staging is a separate promotion gate.

Every promoted combination records:

- shared Slurm client package version;
- workflow contract package version;
- Slurm version and REST data-parser version;
- runtime image digest;
- FinSim release digest; and
- proxy build version.

## Failure and observability contract

The workflow ID, correlation ID, master job ID, worker-array job ID and release
ID appear in structured logs and workflow state, never in credentials. Required
negative tests cover expired credentials, client replay, conflicting
idempotency keys, lost submission responses, cache outage, REST outage, worker
failure, timeout, cancellation, malformed results and partial aggregation.

FinSimProxy reports scheduler state separately from workflow state. Slurm
completion alone is not success: success requires a valid terminal report in
Couchbase. The master reports failure to the workflow store even when it cannot
complete aggregation; proxy polling therefore does not depend solely on
Slurm's retention window.

## Non-goals

- FinSimProxy is not moved into Linux or a container.
- External clients do not receive Slurm credentials.
- The Windows proxy does not mount the cluster filesystem.
- The design does not expose ports 3306, 6817, 6818 or 6819 to Windows.
- The shared client does not become a general administrative Slurm SDK.
- Local FinSimCache is not represented as a production Couchbase replacement.

## Acceptance criteria

The design is complete when the actual Windows proxy can submit through the
shared package to the local WSL cluster, the master can use the same package to
submit a worker array, workers can exchange data through the cache contract,
the master can store a final report, and the original client can retrieve that
report only through FinSimProxy. The same FinSim artifact and workflow must then
pass the cloud-staging gate without source changes.
