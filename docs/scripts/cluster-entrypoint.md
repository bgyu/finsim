# `cluster-entrypoint.sh`

**Tags:** `production`, `simulation`, `runtime`, `security`, `configuration`

Source: [scripts/cluster-entrypoint.sh](../../scripts/cluster-entrypoint.sh)

## Purpose

`cluster-entrypoint.sh` is the universal runtime image entrypoint. One
immutable `finsim-runtime` image serves every role, but this script creates a
different runtime contract for each role selected by `CONTAINER_ROLE` and
`CLUSTER_DEPLOYMENT_PROFILE`.

It is responsible for:

1. starting as container root;
2. validating profile, role, mounts, files, identities and configuration;
3. staging configuration and secrets into `/run` with narrow ownership;
4. waiting for required dependencies;
5. starting exactly one selected role daemon;
6. dropping privileges for non-compute daemons;
7. keeping the essential child alive and forwarding shutdown signals.

Operators normally do not invoke this source file directly. The image
`ENTRYPOINT`, Compose or a generated Quadlet unit invokes the installed copy at
`/usr/local/sbin/cluster-entrypoint`.

## Top-level call chain

```text
container starts
    ↓
require_root_supervisor
    ↓
validate CLUSTER_DEPLOYMENT_PROFILE
    ↓
validate CONTAINER_ROLE
    ↓
unset inherited SLURM_JWT and set umask 077
    ↓
prepare_runtime_directories (except application role)
    ↓
role dispatch
    ├── database       → run_database
    ├── slurmdbd       → run_slurmdbd
    ├── controller     → run_controller
    ├── compute-*      → run_compute
    ├── restd          → run_restd
    ├── token-agent    → run_token_agent
    └── application    → run_application
    ↓
wait_for_essential_process
    ↓
child exits → entrypoint exits with child status
```

The script uses `set -Eeuo pipefail`. An unset variable, failed command or
failed pipeline is normally fatal. Validation functions use status 64 for an
invalid caller value and status 78 for a deployment/configuration failure.

## Startup and shutdown workflow

```text
PID 1 starts as root
    ↓
install signal traps
    ↓
validate role/profile and input files
    ↓
copy trusted configuration/secrets into /run
    ↓
start dependencies or wait for remote dependencies
    ↓
start one role daemon in the correct identity
    ↓
wait -n for an essential child
    ↓
daemon exits → return its status

TERM/INT
    ↓
stop_children
    ├── gracefully ask MariaDB to shut down over its socket
    ├── TERM every tracked child
    └── wait for children
```

`EXIT` also invokes `stop_children`, so normal exit and signal-driven exit
clean up tracked children. The database branch gets a bounded graceful socket
shutdown before generic termination because database maintenance should not
need a forced kill during a normal container stop.

## Global definitions

| Definition | What it does | Why it exists |
| --- | --- | --- |
| `RUNTIME_DIR` and related constants | Define `/run/slurm`, staged config and trust paths. | Keeps mutable runtime material in tmpfs rather than the image or source mounts. |
| `role`, `profile` | Read `CONTAINER_ROLE` and `CLUSTER_DEPLOYMENT_PROFILE`. | Select the runtime contract explicitly; profile defaults to production. |
| `child_pids` | Tracks essential background child PIDs. | Enables signal forwarding and `wait -n` supervision. |
| `log` | Emits `[role/profile]`-prefixed diagnostics to stderr. | Makes multi-container journald/Podman logs attributable. |
| `die` | Logs an operational error and exits 78. | Fails closed for unsafe runtime inputs. |
| `usage_error` | Logs an invalid caller value and exits 64. | Distinguishes bad invocation from invalid deployment state. |
| signal traps | Connect `TERM`, `INT` and `EXIT` to cleanup. | Prevents orphaned daemons and supports graceful container shutdown. |

## Validation and staging functions

### `require_root_supervisor`

Requires UID 0 at startup. Root is needed only for bootstrap tasks such as
validating root-owned mounts, copying credentials into `/run`, creating
directories and making controlled identity transitions. It is not a statement
that every long-running daemon should remain root.

### `validate_uint`, `validate_host`, `validate_name`

Validate bounded integers, DNS/IPv4-style hosts and configuration names. These
functions reject shell metacharacters and malformed values before they reach
network probes, Slurm configuration or database rendering.

### `validate_input_file`

Validates an input as an absolute, readable, regular, non-symlink file. It
rejects group/world-writable public inputs and any group/world-readable secret.
Production additionally requires root ownership. This defends against a
bind-mounted file being swapped between validation and use.

### `stage_file`

Copies a validated file to a destination with explicit owner, group and mode.
It is the common primitive for moving configuration and secret material from
role-filtered mounts into the root-owned runtime tree.

### `config_values`, `config_value`

Parse key/value configuration while ignoring comments and whitespace.
`config_value` requires exactly one occurrence, preventing ambiguous duplicate
settings from silently changing the runtime contract.

### `require_config_equals`, `require_config_contains`

Assert exact or list-style Slurm settings. These are used to ensure a
production container cannot start with a simulation or weakened configuration.

### `validate_production_slurm_config`

Requires a fully rendered production `slurm.conf` without `Include` directives
or CRLF line endings. It verifies MUNGE, JWT, Slurm users, `DisableRootJobs`,
TLS, HTTP restrictions, consumable resources and—on compute roles—cgroup
process tracking, task/affinity, accounting and `namespace/tmpfs` containment.

### `validate_cgroup_config`

Requires production to select `autodetect` or `cgroup/v2` and enable core,
RAM and swap constraints. Simulation must explicitly select
`CgroupPlugin=disabled`; this prevents a rootless local profile from claiming
production isolation.

### `prepare_runtime_directories`

Creates `/run/slurm`, staged configuration, PID and log directories with fixed
ownership. The parent configuration directory remains root-owned to prevent a
service identity from replacing staged trust or policy files.

### `prepare_slurm_config`

Selects configuration paths from environment variables, validates them,
stages `slurm.conf` and `cgroup.conf`, stages production `job_container.conf`,
validates production invariants and exports `SLURM_CONF` to the child daemons.

## Trust and dependency functions

### `validate_jwks` and `prepare_jwt_trust`

`validate_jwks` parses JSON and accepts only a non-empty set of usable RS256
public keys without RSA private fields. `prepare_jwt_trust` selects the profile:

```text
simulation controller/slurmdbd → local HS256 key, mode 0400
production controller/slurmdbd/restd → external RS256 JWKS, role-owned 0400
```

The production containers receive public trust material, not the private issuer
key. The token-agent role is simulation-only.

### `prepare_tls_identity`

Production-only validation of Slurm TLS CA, certificate and key. It checks
file security, certificate validity and certificate/key public-key matching,
then stages role-specific names and ownership:

```text
controller → ctld_cert.pem
slurmdbd   → dbd_cert.pem
compute    → slurmd_cert.pem
restd      → restd_cert.pem
```

### `prepare_munge`

Supports either embedded MUNGE in simulation or an externally mounted MUNGE
socket in production. Embedded mode validates a secret key, starts `munged` as
the `munge` identity and waits for `munge -n` to succeed. The function retries
for a bounded period and fails if MUNGE never becomes ready.

### `wait_for_port`

Validates a host, port and attempt count, then performs bounded TCP probes with
a one-second per-connect timeout. It is used for single dependency endpoints.

### `collect_host_candidates`

Builds an ordered endpoint list from an operator override or repeated Slurm
configuration keys. It supports singular compatibility variables and
comma-separated plural overrides.

### `normalize_probe_host`

Extracts the address from `HostName(address)` syntax, rejects ambiguous
parentheses and validates the resulting DNS/IPv4 host. IPv6 is intentionally
not accepted by this image contract.

### `wait_for_any_port`

Probes every unique endpoint on each bounded attempt round and succeeds when
any member accepts a TCP connection. This makes controller/accounting startup
HA-aware: an unavailable primary does not block a healthy backup.

### `wait_for_any_controller`, `wait_for_any_slurmdbd`

Read the configured Slurm port and candidate host settings, then call
`wait_for_any_port` for the controller or accounting service.

### `read_password_file`

Validates a secret file and safe password alphabet/length, then returns the
value on stdout. The value is designed to travel through stdin or a renderer,
not process arguments or an environment variable.

## Database and configuration functions

### `run_database`

Requires the Slurm database password file, initializes MariaDB data state when
needed, validates the MariaDB configuration, starts `mariadbd` as UID 27,
waits for the Unix socket, creates or updates the Slurm accounting database and
user, then returns while supervising the MariaDB child.

The database role owns MariaDB only. `slurmdbd` runs in its own container and
connects over the configured database endpoint.

### `render_slurmdbd_config`

Selects the production-provided template or simulation default, validates
storage host/port/database/user and exactly one password-file or pass-script
credential source, optionally stages the production storage CA, substitutes
the storage markers with Python, fsyncs and atomically replaces the rendered
configuration, then records the selected host and port in `/run`.

### `run_slurmdbd`

Prepares MUNGE, JWT trust, TLS identity and the storage configuration; waits
for the database; then starts `slurmdbd` as UID 64030. It uses `SLURM_CONF` to
select the adjacent `slurmdbd.conf`, avoiding an ambiguous daemon-specific
`-f` option.

### `register_simulation_accounting`

Simulation-only bootstrap that creates the local cluster, demo account and
`finsim` association if missing. Production deliberately does not call this:
production accounting, QOS and associations are controlled operator state.

### `run_controller`

Prepares the Slurm bundle, MUNGE, JWT trust and TLS; waits for any accounting
endpoint; performs simulation accounting registration when appropriate; creates
controller state/log paths; and starts `slurmctld` as UID 64030.

## Compute and REST functions

### `require_production_compute_isolation`

This is the strongest production gate. It requires:

1. an identity map proving the container is not remapped/rootless;
2. a unified cgroup v2 filesystem at `/sys/fs/cgroup`;
3. the host cgroup namespace rather than a private namespace;
4. a writable delegated cgroup directory;
5. the ability to create and remove a probe child cgroup.

It prevents a production `slurmd` from starting in a configuration that could
appear healthy while failing to enforce job limits.

### `run_compute`

Prepares Slurm configuration, MUNGE and production TLS, runs the production
isolation gate, waits for any controller, creates the Slurmd spool and
production job-container directory, validates registration settings, then
starts `slurmd` as root.

Registration modes are:

```text
dynamic → slurmd -Z self-registers; simulation uses a deterministic shape
future  → production-only FUTURE node registration
static  → predeclared node registration
```

The production dynamic path reports physical resources and a feature. The
simulation path uses a deterministic two-CPU/1 GiB shape because rootless
virtualized topology is not a production capacity contract.

### `run_restd`

Prepares Slurm configuration, JWT trust and optional embedded MUNGE, validates
the pinned REST API version and bounded thread count, requires an explicit
HTTPS listener in production, waits for a controller and starts
`slurmrestd` as UID 64031 in JWT-only mode.

### `run_token_agent`

Simulation-only. It prepares configuration and MUNGE, waits for a controller,
then starts the installed development token renewer. It refuses to run in
production because production tokens come from the external issuer/JWKS trust
contract.

### `prepare_application_tls`

Production-only application validation. It requires explicit HTTPS listener
configuration, validates CA/certificate/key files and matching public keys, and
requires the private key to already be a root-owned `finsim` group-readable
0440 mount. It exports the ASP.NET Core certificate paths without copying the
key into the image.

### `run_application`

Requires an executable argument under a versioned `/finsim-app/releases/`
directory. It rejects symlinks, non-root-owned files and group/world-writable
executables, prepares application TLS and uses `exec setpriv` to replace PID 1
with UID 64032 and `no-new-privs`.

## Supervision

### `wait_for_essential_process`

Waits for the first tracked child to exit, logs its status and exits with that
status. A service that exits is therefore visible to Podman/systemd instead of
leaving a healthy-looking shell behind.

## Role matrix

| `CONTAINER_ROLE` | Main function | Long-running process identity | Special behavior |
| --- | --- | --- | --- |
| `database` | `run_database` | MariaDB UID 27 | Initializes database and creates Slurm account DB. |
| `slurmdbd` | `run_slurmdbd` | Slurm UID 64030 | Renders storage config and connects to MariaDB. |
| `controller` | `run_controller` | Slurm UID 64030 | Waits for accounting and optionally bootstraps simulation accounting. |
| `compute-master` / `compute-worker` | `run_compute` | `slurmd` root | Production cgroup gate and node registration. |
| `restd` | `run_restd` | slurmrestd UID 64031 | JWT-only REST with pinned API and TLS policy. |
| `token-agent` | `run_token_agent` | development renewer | Simulation-only short-lived token helper. |
| `application` | `run_application` | FinSim UID 64032 | Executes an immutable versioned application release. |

## Why this design exists

The script lets one image serve many roles without giving every role the same
authority. Root is a temporary bootstrap identity for validation and staging;
service identities own long-running daemons; `slurmd` remains root only because
Slurm's compute isolation contract requires host cgroup and identity control.

The entrypoint is therefore both a launcher and a security boundary. It fails
closed when the inputs, profile, credentials, TLS material, cgroup hierarchy
or role-specific invariants do not match the declared deployment.
