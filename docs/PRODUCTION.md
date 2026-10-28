# Production deployment and security

## Scope and non-goals

`compose.yaml` is a rootless, single-host integration topology. It validates
application behavior and identity boundaries but cannot provide host failure
domains, HA, strong cgroup delegation, or production networking. Production
deploys the same immutable images as role-specific services on dedicated
hosts or an orchestrator capable of the required host integration. No
container launches Podman/Docker and no job nests a container engine.

This design is cloud-neutral at the scheduler/application layer. Provisioning
is intentionally an adapter: AWS, Azure, on-premises, and another provider
implement the same node lifecycle contract without entering FinSim code.

## Reference topology

- Two `slurmctld` instances in separate failure domains with shared/reliably
  replicated state and identical root-owned rendered configuration.
- Two `slurmdbd` instances and an external HA MariaDB-compatible service with
  TLS, backups, point-in-time recovery, and credentials supplied at runtime.
- At least two `slurmrestd` instances on a private network behind a TLS 1.3
  ingress/load balancer. Never expose raw HTTP or a controller port publicly.
- Multiple FinSimProxy replicas behind the public API ingress. Proxy state is
  external, so replicas are replaceable.
- A durable Couchbase cluster (or adapter-compatible store) for workflow,
  chunk, and result keys.
- A small `orchestrator` compute pool that alone mounts a narrowly scoped,
  short-lived Slurm credential and runs masters.
- A scalable `batch` pool that runs workers and does **not** receive the Slurm
  credential.
- A shared durable report/log namespace or an object/database adapter. Local
  POSIX storage must support atomic create/rename and consistent UID 64032.

Network policy permits only required flows: client -> proxy; proxy -> cache and
slurmrestd; orchestrator -> cache and slurmrestd; worker -> cache; Slurm daemon
RPC; slurmdbd -> database. Worker -> slurmrestd should be denied as defense in
depth. Database, cache, controller, MUNGE, and Slurm RPC ports are private.

## Process users and host privilege

Fixed numeric identities are part of the image contract:

| Process/data | Owner | Mode/privilege rationale |
|---|---|---|
| immutable binaries/config templates | `root:root` or `root:slurm` | 0755 dirs, 0644 public config; never writable by daemon/job users |
| `slurmctld`, `slurmdbd` | `slurm` 64030 | unprivileged daemon after root staging |
| `slurmrestd` | `slurmrestd` 64031 | unique unprivileged account as recommended by SchedMD |
| proxy/master/workers/report/cache client | `finsim` 64032 | dedicated non-login service account |
| MUNGE key/state | `munge` 64029 | key 0400, state 0700 |
| Slurm state | `slurm` | controller state 0700; back up consistently |
| job credential files | `finsim` only on proxy/orchestrator | 0400; parent not writable by other users |
| final reports | `finsim` | file 0600, directory 0700 |

Production `slurmd` runs as root on a dedicated compute host/container with the
host cgroup v2 hierarchy and required namespace/device controls. Root is needed
to create allocations and switch to UID 64032. Do not run production compute
rootless, map root through a user namespace, or weaken cgroups to make a
container start. The entrypoint rejects those conditions. `DisableRootJobs`,
association enforcement, cgroup controls, namespace/tmpfs containment, and
the `finsim` account keep payloads unprivileged.

An interactive `podman exec` defaults to the container's configured/staging
user and does not prove daemon identity. Inspect `ps -eo user,uid,pid,args`.
Use `podman exec --user finsim ...` when intentionally debugging from the
workload identity, and disable routine production exec access through RBAC.

## JWT and secret handling

There are three separate trust domains:

1. **Client -> FinSimProxy.** Use an organizational OIDC/OAuth issuer. Proxy
   validates RS256 signature, exact issuer/audience, lifetime, and
   `finsim.submit` scope. Use access tokens, not ID tokens. Prefer short
   lifetimes and workload/delegated identity; never implement production token
   minting with `dev-jwt.py`.
2. **Proxy/master -> slurmrestd.** Configure Slurm with public JWKS and
   `disable_token_creation`; the issuer retains the private signing key.
   Claims identify only `finsim`. Token issuance must not grant arbitrary root
   impersonation. Mount tokens as files, rotate atomically, and keep worker
   nodes outside this secret distribution.
3. **Proxy/jobs -> cache.** Use a different audience/scope and credential from
   Slurm. In production, prefer the cache platform's workload identity and
   per-workflow/tenant authorization. The demo static JWT represents that
   boundary, not the final credential lifecycle.

Secret stores may be cloud-specific internally, but deployment supplies a
portable file contract. Files must be regular non-symlinks, bounded, single
line where applicable, correct owner, and no group/other access. Rotate by
writing a new private file and atomically replacing the mount projection;
clients reload token files per request. Do not log tokens, put them in command
arguments, bake them into images, use Compose environment values, or store
them in git/TeamCity parameters visible to untrusted builds.

SchedMD notes that the ability to create Slurm JWTs is effectively cluster-root
trust. See [JWT authentication](https://slurm.schedmd.com/jwt.html). The proxy
pattern and unprivileged `slurmrestd` follow
[Slurm REST guidance](https://slurm.schedmd.com/rest.html). ASP.NET uses the
standard bearer middleware and production tokens should come from OAuth/OIDC,
consistent with [Microsoft JWT bearer guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0).

## TLS and configuration ownership

Render production `slurm.conf` from versioned configuration plus environment
inventory, validate it, and mount it root-owned/read-only. Do not mutate it
inside a running container. The production entrypoint requires explicit paths
and rejects `Include`, weak owners/modes, missing TLS/JWKS, HS256 token creation,
rootless compute, or non-cgroup-v2 isolation.

`config/slurm.production.conf` enables `tls/s2n`, disables clear-text HTTP on
Slurm RPC listeners, uses cgroup/namespace isolation, private accounting data,
bounded job/node/array counts, and explicit master/worker pools. Every role
gets the common CA and only its own certificate/private key. Automate renewal,
overlap trust during rotation, and alert well before expiry. See
[Slurm TLS](https://slurm.schedmd.com/tls.html) and
[cgroup v2](https://slurm.schedmd.com/cgroup_v2.html).

All application HTTP is HTTPS in production. Terminate client TLS at a hardened
ingress and use TLS again to proxy/cache/restd unless the chosen platform
provides an equivalent authenticated service mesh boundary. Configure request,
header/body, connection, and rate limits. The local `AllowInsecureHttp=true`
values must never appear in production.

## Elastic nodes and cloud-neutral lifecycle

The controller declares bounded FUTURE identities. A compute service starts
with configless Slurm and claims a compatible feature (`-Ffinsim-master` or
`-Ffinsim-worker`). Configless nodes retrieve the controller's authoritative
configuration; see [Configless Slurm](https://slurm.schedmd.com/configless_slurm.html).

The lifecycle adapter invoked by the controller must implement:

```text
resume(hostlist, operation-id): idempotently provision approved nodes;
  attach network policy, immutable signed image digest, role feature,
  certificates, MUNGE trust, cache credential, and (master pool only) Slurm JWT.

suspend(hostlist, operation-id): verify nodes are drained/idle and identity
  matches; terminate exactly those nodes; report partial failure without
  claiming success.
```

It runs as a separate tightly authorized service, not with broad cloud keys in
`slurmctld`. The included lifecycle scripts validate inputs, serialize state,
and call an operator-supplied adapter; they do not contain AWS/Azure APIs.
Provisioning should use provider workload identity, allowlisted shapes/images,
idempotency tokens, quotas, timeouts, bounded concurrency, and audit logs.
Nodes register or Slurm marks them down after `ResumeTimeout`. Removal is
drain -> wait for zero jobs -> power down -> remove/return identity. Never
delete a node merely because its container disappeared. See the
[Slurm power saving guide](https://slurm.schedmd.com/power_save.html).

Adding capacity does not require rebuilding the image or editing application
code. Render/activate additional bounded node identities if the existing pool
is exhausted, reconfigure the controller under change control, then start
matching compute units. Removing capacity follows the drain workflow. Keep
master capacity separate so a worker surge cannot eliminate orchestration.

## Availability, data, and recovery

- Back up controller state and accounting DB with a tested consistency and
  restore procedure; encrypt backups and test recovery regularly.
- Cache workflow state and results need TTL/retention, replication, capacity
  alarms, and conflict visibility. Input/result keys must survive master retry.
- Final reports need durable retention and an immutable business identifier.
  For object storage, replace the POSIX report adapter rather than pretending
  a non-POSIX mount has atomic rename behavior.
- Use Podman Quadlet/systemd, another audited supervisor, or a suitable
  orchestrator for daemon restart/order/resource policy. Podman documents
  declarative systemd integration in [Quadlet](https://docs.podman.io/en/latest/markdown/podman-quadlet.1.html).
- Run controller/DB schema upgrades according to the Slurm compatibility
  matrix. Canary the application image, drain nodes before replacement, and
  roll worker nodes before/after control services as the chosen upgrade path
  requires.

## Observability and audit

Collect structured proxy/master/worker logs with correlation ID, workflow ID,
job ID, role, state transition, duration, and error class—but never token,
input payload, or report contents by default. Export controller, queue,
node-state, REST latency/error, cache latency/conflict, workflow age/failure,
DB replication, disk, certificate-expiry, and lifecycle metrics. Alert on
unknown/duplicate submission reconciliation, stuck masters, missing results,
node flapping, authentication spikes, and root-job attempts.

Keep Slurm accounting as the authoritative job/UID/resource audit and API
gateway logs as the authenticated caller audit. Define retention and access by
the financial data classification.

## Go-live gates

- Signed immutable image digest; SBOM and vulnerability/license policy pass.
- No development target, self-signed key, local token issuer, cache simulator,
  embedded database, HTTP override, or floating tag deployed.
- HA/failure-domain and backup/restore tests pass.
- OIDC issuer/audience/scope and Slurm JWKS rotation tested.
- Network policy proves worker cannot reach slurmrestd and no internal daemon
  port is public.
- Compute cgroup v2, namespace containment, device filtering, UID switching,
  and root-job rejection verified on the real host OS.
- Master-only secret placement proven; worker node filesystem lacks Slurm JWT.
- Load, retry, ambiguous-submit, controller failover, cache outage, node loss,
  cancellation, and scaling tests pass.
- Operational ownership, incident response, change/rollback, key rotation, and
  data-retention procedures are approved.
