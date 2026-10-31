# Repository hardening and operator-guide design

Date: 2026-10-31
Status: approved

## Objective

Turn the working local FinSim reference cluster into an accurately documented,
cloud-neutral deployment contract. Correct implementation gaps discovered by
review, make production artifacts renderable and verifiable, and provide one
complete guide for compiling, publishing, deploying, operating, scaling and
troubleshooting the system.

This repository supplies the workload image, application release format,
configuration renderer, role entrypoint, role deployment definitions and
lifecycle client contract. It deliberately does not implement an enterprise
identity provider, certificate authority, replicated database, shared storage,
load balancer, monitoring platform or cloud-provider node provisioner.

## Findings to correct

1. The generic production Quadlet template has unresolved security placeholders,
   no renderer and no application command. It mounts a whole secret directory
   into every role, violating the role-filtered credential model.
2. Lifecycle clients are installed but the production configuration renderer
   cannot enable Slurm `ResumeProgram` and `SuspendProgram` settings.
3. Local `cluster.env` values are sourced by shell scripts without first being
   constrained to their documented alphabets and ranges.
4. Application releases include a checksum manifest, but deployment validation
   checks only executable presence rather than the complete release contents.
5. Production rendering validates too few operator inputs and has no offline
   deployment-bundle validator.
6. Existing documents describe architecture but do not form a start-to-finish
   build and operations manual. Important boundaries and external prerequisites
   are implicit.

## Selected approach

Correct the repository-owned gaps and document every remaining external
responsibility. Do not embed AWS, Azure or another vendor SDK, do not build an
identity provider, and do not claim that local Compose is a production
orchestrator.

## Implementation design

### Local input and release integrity

Add a strict helper for reading the generated local environment file. Only the
three expected keys are accepted, duplicates and shell syntax are rejected, and
tag, release and TCP-port values are validated before export. Cluster scripts
use this parser instead of `source`.

Deployment validation will parse and validate both manifests, require the
selected release directory and files to be root-owned in the rootless container
namespace, reject writable release content, reject unlisted or missing files,
and compare every file checksum. The manifest itself remains outside its file
map to avoid recursion.

### Production configuration and elasticity

The configuration renderer will validate DNS endpoint inputs and optionally
render Slurm power-saving settings. Enabling lifecycle integration requires an
HTTPS lifecycle endpoint contract, controller-side mTLS client files, bounded
resume/suspend timeouts and rates, and the installed cloud-neutral lifecycle
programs. Manual dynamic registration remains supported without lifecycle
automation.

Dynamic nodes use Slurm's supported runtime model. A new node starts the same
runtime digest with `compute-master` or `compute-worker`, configless controller
addresses and a feature. Removal drains the node, waits for jobs to leave,
stops the workload, and deletes the dynamic node record. Cloud API work remains
behind the external lifecycle service.

### Production role bundle

Replace the non-executable all-role template with explicit role templates or a
strict renderer that emits one Quadlet per role. Each role receives only its
required configuration, secrets, credentials, state mounts, capabilities,
networks and command. Runtime images are referenced by digest. Generated files
contain no secret values; secret paths refer to files provisioned by the target
host's secret-management system.

The renderer fails on invalid role names, image references, paths and endpoints.
An offline validator checks permissions, unresolved markers, required inputs and
role-specific invariants before systemd sees a unit.

### Documentation structure

Add `docs/USER_GUIDE.md` as the task-oriented entry point:

- supported host and Podman prerequisites;
- repository layout and trust boundaries;
- RPM and image compilation;
- application compilation and immutable publication;
- complete local cluster lifecycle and REST job usage;
- CI/TeamCity usage;
- production preparation, registry promotion and host layout;
- PKI, JWT, MUNGE and database prerequisites;
- role-by-role deployment order and verification;
- manual and automatic node scaling;
- updates, rollback, backup and disaster recovery;
- observability, troubleshooting and security cautions.

Keep `README.md` concise. `ARCHITECTURE.md` explains design, `DEPENDENCIES.md`
records provenance, `OPERATIONS.md` covers local runbooks, and `PRODUCTION.md`
covers production policy and external integrations. Cross-links replace copied
instructions where possible.

### Verification

Required gates are:

- shell syntax and Python tests;
- strict environment parser negative tests;
- simulation and production configuration renders;
- production role-bundle render and offline validation;
- Compose rendering;
- managed application build and tests;
- complete release-manifest verification, including tamper rejection;
- live local startup, REST master/worker workflow and acceptance suite;
- clean Git worktree after separate design, implementation and documentation
  commits.

## Operational boundary

Passing repository tests proves that the deployment contract is internally
consistent and that the local simulation works. A real production launch still
requires environment-specific capacity testing, security review, failure and
restore exercises, certificate/token rotation testing, database HA validation,
network policy enforcement, monitoring integration and a tested lifecycle
service implementation.
