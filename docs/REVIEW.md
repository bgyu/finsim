# Implementation review and production cautions

Review date: 2026-11-01

This document records what was reviewed, what was corrected and what remains a
deployment responsibility. “Production-oriented” means that the repository
enforces explicit security and operational contracts. It does not mean that a
new organization can deploy it unchanged without infrastructure integration,
capacity testing and security approval.

## Corrections made by this review

| Finding | Correction |
|---|---|
| Generated `cluster.env` was sourced as shell | `scripts/lib/runtime-env.sh` now parses an exact three-key format without evaluation; writers and readers validate alphabets and port ranges |
| Release manifest was not completely checked | `validate-deployment.sh` now checks schema, release identity, complete file set, hashes, symlinks, ownership and write permissions inside Podman's user namespace |
| Production compute always used `slurmd -F` without declaring FUTURE nodes | registration is now explicit: `dynamic`, `future` or `static`; the default production example uses dynamic `-Z` registration |
| Generic Quadlet mounted every secret into every role | strict renderer emits role-specific mounts, commands, environment and capabilities; complete secret-directory mounts are rejected |
| Production unit placeholders had no renderer or validator | `render-production-unit.sh` and `validate-production-unit.sh` provide an executable, offline-checked contract |
| Production lifecycle clients were installed but not configured | optional render settings now add bounded resume/suspend configuration and `lifecycle-client.conf` |
| MariaDB client required TLS while generated server config did not | production MariaDB config now enables CA/certificate/key transport and requires secure transport |
| Local topology could not be extended without editing Compose | `local-node.sh` safely adds dynamic nodes and drains/deletes them while retaining spool state |
| Production render had no standalone validation | `validate-production-config.sh` checks manifest completeness, hashes, TLS, JWT, cgroup and lifecycle invariants |
| Production proxy/cache listeners were loopback HTTP with no projected identity | application roles now require a validated PEM certificate/key, listen on routable HTTPS, and propagate a public service CA bundle to master and worker jobs |
| Lifecycle hooks had no scheduler-visible nodes to resume | lifecycle rendering now requires a strictly parsed, manifested `State=CLOUD` inventory; dynamic external scale-out remains independent |
| Restricted compute units could not honestly guarantee cgroup-device and mount-namespace operations | compute-only Quadlets now declare the dedicated-host trusted-agent boundary, host cgroup namespace and privileged Podman contract; every other role remains least-privilege |
| Production application health proved only that a process existed | generated proxy/cache units now call their HTTPS readiness endpoints with the public service CA |

## Security boundaries to preserve

- The runtime image starts with a small root supervisor. It stages validated
  inputs and drops to fixed daemon identities. `slurmd` stays root because it
  must manage cgroups and launch jobs as their submitting identity.
- Never add compute privileges to database, accounting, controller, REST,
  proxy or cache roles. Generated compute units alone use privileged Podman and
  the host cgroup namespace because root `slurmd`, cgroup device policy and
  `job_container/tmpfs` are host-trusted operations. Run one on a dedicated,
  replaceable compute host and treat container compromise as host compromise.
- Only the master node and proxy receive a Slurm bearer-token path. Workers
  receive only the cache credential required for their data key.
- The development HS256 Slurm key and development RSA issuer are simulation
  assets. Production accepts public RS256 JWKS and external short-lived tokens;
  private issuer keys never enter cluster containers.
- Application service keys are role-filtered root:finsim 0440 mounts and the
  process replaces its bootstrap identity with UID 64032. The public service
  CA bundle is the only TLS material propagated to
  jobs; scheduled payloads never receive a server private key.
- Application release checksums detect accidental or unauthorized mutation on
  disk. They are not a supply-chain signature. Sign runtime images and release
  manifests with the organization's registry/artifact signing system.

## Availability and scaling boundaries

- Compose is a single-host simulation, not an HA control plane.
- Production needs controller and slurmdbd failover testing, fencing, shared
  controller state, a replicated MariaDB service, redundant REST/proxy
  endpoints and tested DNS/load-balancer behavior.
- Dynamic self-registration is appropriate when an external platform starts
  nodes. Slurm-driven scaling additionally requires precreated cloud inventory
  and an implemented lifecycle API. The repository validates and manifests the
  inventory and contains only the lifecycle service's mTLS client contract.
- Node removal must drain, wait for allocations and reservations, stop the
  workload and only then delete the dynamic Slurm record.
- `MaxNodeCount` is a controller bound, not a provisioner and not a capacity
  guarantee. Benchmark scheduling, accounting and REST behavior at expected
  job and node counts.

## Data and application boundaries

- FinSimCache is a deliberately small file-backed Couchbase substitute. It is
  unsuitable as a multi-instance production cache without external consistency
  and durability. Replace it behind the same key/value interface.
- Shared data paths require atomic rename, stable numeric ownership and
  semantics tested under controller and compute failure. Object/database-backed
  implementations need equivalent application-level behavior.
- The master alone writes the final report. Workers write idempotent keyed
  results. Result retention, encryption, tenancy and deletion policy belong to
  the production data platform.
- The demonstration job validates architecture, not finance-model correctness,
  performance or regulatory controls.

## Build and support boundaries

- MUNGE and Slurm source archives are checksum-pinned. RPM repository content
  is signed, but repository snapshots are not pinned byte-for-byte. A regulated
  build should mirror and snapshot every repository, record SBOM/provenance and
  rebuild from that controlled mirror.
- The image starts from UBI but also uses public EPEL and CentOS Stream packages
  where UBI lacks dependencies. The resulting image is not a pure Red Hat-only
  support surface. Review redistribution and vendor-support policy before
  publication.
- “Latest source” is implemented as an explicit reviewed update to
  `build/versions.conf` and matching Containerfile arguments. Never download an
  unpinned latest archive during a release build.
- One universal runtime simplifies scanning and promotion but installs MariaDB
  and all Slurm daemons on every node. Role, network, mount and capability
  controls prevent execution; package-level attack surface remains larger than
  minimal per-role images and must be accepted explicitly.

## Mandatory production launch gates

Before admitting real workloads, complete all of the following:

1. Threat model and security review of network, secrets, identity and the
   privileged, rootful dedicated-compute-host boundary.
2. Image SBOM, vulnerability policy, signature and admission verification.
3. PKI/JWT issuance, rotation, expiry, revocation and outage exercises.
4. MariaDB backup, point-in-time recovery and failover exercise.
5. Controller state backup, fencing and failover exercise.
6. Dynamic add, drain, delete, cloud resume/suspend, lifecycle timeout and
   partial-failure exercise.
7. Load and soak tests at representative node, job-array and REST request scale.
8. Monitoring, alerting, audit-log retention and incident runbooks.
9. Application release rollback and persistent-schema compatibility test.
10. Disaster recovery rehearsal in a separate environment.

The commands and file contracts for these gates are described in
[USER_GUIDE.md](USER_GUIDE.md) and [PRODUCTION.md](PRODUCTION.md).
