# Script reference

This is the exhaustive reference for every tracked script under `scripts/` and
`deploy/lifecycle/`. It explains where each script runs, why it exists, how to
use it, what it changes, and which repository scripts, functions and variables
it depends on.

Run operator commands from the repository root unless an entry says otherwise.
Scripts use strict error handling and normally stop at the first failed
precondition. Source remains authoritative; update this document whenever a
script, cross-script call, sourced function, shared variable or generated path
changes.

## 1. Which script should I run?

| Task | Command |
|---|---|
| Build runtime and development images | `scripts/build-images.sh` |
| Build RPMs manually on EL10 | `scripts/build-rpms.sh` |
| Install verified RPMs manually | `scripts/install-runtime.sh` |
| Configure a manually assembled runtime | `scripts/configure-runtime.sh` |
| Prepare the complete local deployment | `scripts/prepare-cluster.sh` |
| Prepare only storage | `scripts/prepare-storage.sh` |
| Render simulation or production config | `scripts/prepare-config.sh` |
| Publish an immutable application release | `scripts/publish-app.sh --release ID` |
| Validate prepared local inputs | `scripts/validate-deployment.sh` |
| Start or stop the simulation | `scripts/cluster-up.sh`, `scripts/cluster-down.sh` |
| Submit a REST workflow | `scripts/submit-simulation.sh --wait` |
| Add or remove a local dynamic node | `scripts/local-node.sh add` or `scripts/local-node.sh remove` |
| Run the complete local acceptance suite | `scripts/acceptance-test.sh` |
| Run TeamCity/CI verification | `scripts/ci.sh verify` |
| Run TeamCity/CI integration | `scripts/ci.sh integration` |
| Render a production Quadlet | `scripts/render-production-unit.sh ...` |
| Validate production config or a Quadlet | `scripts/validate-production-config.sh`, `scripts/validate-production-unit.sh` |

Do not normally invoke `cluster-entrypoint.sh`, `cluster-healthcheck.sh`,
`dev-token-renewer.sh`, library files, lifecycle hooks or test modules by hand.
They have dedicated callers described below.

## 2. Execution contexts and lifecycle

| Context | Scripts |
|---|---|
| Developer/CI host | image build, local preparation, cluster lifecycle, submission, acceptance and production render/validate commands |
| Disposable EL10 RPM builder | `build-rpms.sh` and `lib/build-common.sh` |
| Runtime image build stage | `install-runtime.sh`, `configure-runtime.sh` and `lib/build-common.sh` |
| Running runtime container | `cluster-entrypoint.sh`, `cluster-healthcheck.sh`, `dev-token-renewer.sh` |
| Slurm controller hook | `slurm-resume-program`, `slurm-suspend-program`, `lifecycle-common.sh` |
| Host Python process | `submit-simulation.py`, `dev-jwt.py`, `tests/test_build_scripts.py` |

The normal development flow is:

```text
build-images.sh
    -> Containerfile
       -> build-rpms.sh
       -> install-runtime.sh
       -> configure-runtime.sh

prepare-cluster.sh
    -> prepare-storage.sh
    -> prepare-config.sh
    -> bootstrap-dev-secrets.sh -> dev-jwt.py
    -> publish-app.sh (only when release is absent)
    -> validate-deployment.sh

cluster-up.sh -> validate-deployment.sh -> Podman Compose
submit-simulation.sh -> submit-simulation.py -> FinSimProxy REST
acceptance-test.sh -> test_build_scripts.py + submit-simulation.sh
cluster-down.sh
```

The production artifact flow is:

```text
prepare-config.sh --profile production
    -> validate-production-config.sh

render-production-unit.sh
    -> deploy/quadlet/finsim-role.container.in
    -> validate-production-unit.sh
    -> target-host Podman systemd generator
```

## 3. Direct dependency matrix

“Functions/variables” lists only cross-file contracts. Internal functions are
documented with their defining script later.

| Script | Direct repository dependencies | Functions or shared variables used |
|---|---|---|
| `scripts/build-images.sh` | `Containerfile` | `FINSIM_IMAGE_TAG` |
| `scripts/build-rpms.sh` | sources `scripts/lib/build-common.sh`; reads `build/versions.conf` | `build_*` validation, repository, download and manifest functions; source pin variables |
| `scripts/install-runtime.sh` | sources `scripts/lib/build-common.sh`; reads `build/versions.conf` and `/var/packages` | `build_*` validation, repository and manifest functions |
| `scripts/configure-runtime.sh` | sources `scripts/lib/build-common.sh`; installs runtime and lifecycle scripts | `build_validate_input_*`, `build_require_*`, `build_die`, `build_log` |
| `scripts/lib/build-common.sh` | `build/versions.conf` data contract | exports source-pin variables and `STREAM_REPOSITORY_ARGS` |
| `scripts/prepare-storage.sh` | none | `FINSIM_DEPLOY_ROOT` |
| `scripts/prepare-config.sh` | templates under `config/` | production/simulation `FINSIM_*` settings |
| `scripts/bootstrap-dev-secrets.sh` | invokes `scripts/dev-jwt.py` | image, release and proxy settings; writes `cluster.env` |
| `scripts/dev-jwt.py` | none | CLI-only `jwks` and `token` contracts |
| `scripts/publish-app.sh` | dev image and solution source | `FINSIM_APP_RELEASE`, `FINSIM_DEV_IMAGE`, `FINSIM_IMAGE_TAG` |
| `scripts/prepare-cluster.sh` | invokes five preparation/validation scripts | passes `FINSIM_APP_RELEASE` |
| `scripts/validate-deployment.sh` | sources `scripts/lib/runtime-env.sh` | `runtime_env_load`, `FINSIM_DEPLOY_ROOT`, `FINSIM_APP_RELEASE` |
| `scripts/lib/runtime-env.sh` | `.runtime/cluster.env` data contract | defines/exports the three local `FINSIM_*` values |
| `scripts/cluster-up.sh` | invokes `validate-deployment.sh`; sources `runtime-env.sh` | `runtime_env_load`, image tag and proxy port |
| `scripts/cluster-down.sh` | Compose and `.runtime/cluster.env` | no sourced functions |
| `scripts/local-node.sh` | sources `runtime-env.sh` | `runtime_env_load`, image tag, Slurm registration variables |
| `scripts/cluster-entrypoint.sh` | invokes installed `dev-token-renewer`; consumes rendered config and role mounts | `CONTAINER_ROLE`, deployment profile and role-specific variables |
| `scripts/cluster-healthcheck.sh` | called by Compose/Quadlet | role, profile, REST listener and token path |
| `scripts/dev-token-renewer.sh` | invoked by the token-agent entrypoint role | development opt-in, token user/path/lifetime/refresh variables |
| `scripts/submit-simulation.sh` | execs `scripts/submit-simulation.py` | forwards all arguments |
| `scripts/submit-simulation.py` | local API-token file and FinSimProxy REST | proxy host/port variables |
| `scripts/acceptance-test.sh` | sources `runtime-env.sh`; runs script tests and `submit-simulation.sh` | `runtime_env_load`, image tag, proxy port |
| `scripts/ci.sh` | invokes build, prepare, cluster, acceptance and production-config scripts | `BUILD_TAG`, `KEEP_CLUSTER`, production endpoint variables |
| `scripts/render-production-unit.sh` | `deploy/quadlet/finsim-role.container.in` | role-specific environment/mount contract |
| `scripts/validate-production-config.sh` | rendered production bundle | no sourced functions |
| `scripts/validate-production-unit.sh` | rendered `.container` file | `CONTAINER_ROLE` line and mount/capability invariants |
| `scripts/tests/test_build_scripts.py` | exercises build libraries, renderers, validators and Containerfile contracts | calls scripts through subprocesses; never imported by runtime code |
| `deploy/lifecycle/slurm-resume-program` | sources `deploy/lifecycle/lifecycle-common.sh` | `lifecycle_init`, `lifecycle_request`; `SLURM_RESUME_FILE` |
| `deploy/lifecycle/slurm-suspend-program` | sources `deploy/lifecycle/lifecycle-common.sh` | `lifecycle_init`, `lifecycle_request` |
| `deploy/lifecycle/lifecycle-common.sh` | lifecycle client data file and mTLS files | defines all `lifecycle_*` functions and `LIFECYCLE_*` globals |

## 4. Build and image scripts

### `scripts/build-images.sh`

Purpose: build the only published image targets, `finsim-runtime` and
`finsim-dev`. Keeping this wrapper small ensures developers and CI select the
same Containerfile targets and image names.

- Context: developer or CI host with Podman.
- Input: `FINSIM_IMAGE_TAG` (default `latest`); the tag accepts only letters,
  digits, `_`, `.`, and `-`.
- Output: `localhost/finsim-runtime:<tag>` and
  `localhost/finsim-dev:<tag>`.
- State: updates local image storage; it does not start containers.
- Repository dependency: `Containerfile`. The Containerfile subsequently
  calls `build-rpms.sh`, `install-runtime.sh`, and `configure-runtime.sh`.

Example:

```bash
FINSIM_IMAGE_TAG=review-42 scripts/build-images.sh
podman images localhost/finsim-runtime
```

### `scripts/build-rpms.sh`

Purpose: compile checksum-pinned MUNGE and Slurm source archives into binary
RPMs and SRPMs. It exists separately from the Containerfile so package
engineers can reproduce the RPM phase on another isolated EL10 builder.

- Context: root inside a disposable UBI/RHEL-compatible EL10 environment.
- Options: `--versions-file`, `--output-dir`, `--work-dir`, paired Slurm
  version/SHA overrides, paired MUNGE version/SHA overrides, and `--help`.
- Defaults: `build/versions.conf`, `/var/packages`, and
  `/var/tmp/slurm-rpm-build`.
- Output: `RPMS/`, `SRPMS/`, and `BUILD-MANIFEST` under the output directory.
- Idempotency: a matching, fully verified existing artifact set is reused. A
  non-empty directory without a valid matching manifest is rejected.
- Safety: output and work paths must be absolute and cannot be `/`; source
  overrides require both version and digest; partial builds are staged and the
  manifest is published last.

Sourced dependency: `scripts/lib/build-common.sh`. Used functions are
`build_die`, `build_load_versions`, `build_validate_absolute_path`,
`build_validate_version`, `build_validate_sha256`, `build_require_root`,
`build_require_el10`, `build_require_commands`, `build_prepare_repositories`,
`build_set_stream_repository_args`, `build_download_verified`,
`build_verify_manifest`, `build_verify_manifest_sources`, and `build_log`.
It consumes the globals `MUNGE_VERSION`, `MUNGE_SHA256`, `SLURM_VERSION`,
`SLURM_SHA256`, and `STREAM_REPOSITORY_ARGS` created by that library.

Example:

```bash
sudo scripts/build-rpms.sh \
  --versions-file "$PWD/build/versions.conf" \
  --output-dir /var/packages \
  --work-dir /var/tmp/slurm-rpm-build
```

### `scripts/install-runtime.sh`

Purpose: verify an RPM artifact set and install only the allow-listed runtime
RPMs plus required UBI/EPEL/Stream dependencies. This separates artifact
production from runtime assembly and prevents build-only RPMs entering the
production image accidentally.

- Context: root on EL10, normally the Containerfile runtime stage.
- Options: `--versions-file`, `--packages-dir`, and `--help`.
- Required RPM names: `munge`, `munge-libs`, `slurm`, `slurm-slurmctld`,
  `slurm-slurmd`, `slurm-slurmdbd`, and `slurm-slurmrestd`.
- Output/state: installs OS packages, cleans DNF caches, and verifies daemon
  executables plus JWT, REST, cgroup, namespace, TLS and MySQL plugins.
- Failure behavior: duplicate/missing RPM names, source-pin mismatch, checksum
  mismatch, or unresolved plugin libraries fail the image build.

Sourced dependency: `scripts/lib/build-common.sh`. It uses
`build_load_versions`, `build_require_root`, `build_require_el10`,
`build_require_commands`, `build_verify_manifest`,
`build_verify_manifest_sources`, `build_prepare_repositories`,
`build_set_stream_repository_args`, `build_die`, and `build_log`.

Example:

```bash
sudo scripts/install-runtime.sh \
  --versions-file "$PWD/build/versions.conf" \
  --packages-dir /var/packages
```

### `scripts/configure-runtime.sh`

Purpose: create fixed service identities, directories and permissions, then
install immutable runtime helpers. It deliberately does not generate secrets
or start daemons, keeping image construction separate from deployment.

- Context: root on EL10, normally after `install-runtime.sh`.
- Option: `--source-root DIRECTORY` selects the repository asset root.
- Fixed identities: MUNGE 64029, Slurm 64030, slurmrestd 64031, FinSim 64032,
  developer 1000, and RPM-provided MariaDB 27.
- Installed helpers: `cluster-entrypoint.sh`, `cluster-healthcheck.sh`,
  `dev-token-renewer.sh`, and all three lifecycle files.
- Idempotency: existing users/groups are accepted only when their numeric IDs,
  group, home and shell match. Conflicts fail closed.

Sourced dependency: `scripts/lib/build-common.sh`. It uses
`build_validate_input_directory`, `build_validate_input_file`,
`build_require_root`, `build_require_el10`, `build_require_commands`,
`build_die`, and `build_log`.

Example:

```bash
sudo scripts/configure-runtime.sh --source-root "$PWD"
```

### `scripts/lib/build-common.sh`

Purpose: shared, side-effect-free package-build validation and repository
logic. Source it; do not execute it.

Function catalog:

| Function | Contract |
|---|---|
| `build_log`, `build_die` | Consistent diagnostics and fatal exit |
| `build_require_root` | Requires effective UID 0 |
| `build_require_el10` | Requires `PLATFORM_ID=platform:el10` |
| `build_require_commands` | Verifies named commands are available |
| `build_validate_sha256` | Requires 64 lowercase hexadecimal characters |
| `build_validate_version` | Accepts the restricted source-version grammar |
| `build_validate_absolute_path` | Rejects relative paths, `/`, CR and LF |
| `build_validate_input_directory` | Requires a real, non-symlink directory |
| `build_validate_input_file` | Requires a readable, non-symlink regular file |
| `build_load_versions` | Parses `versions.conf` as data and exports approved pin variables |
| `build_download_verified` | Downloads atomically and verifies SHA-256 before publication |
| `build_prepare_repositories` | Installs verified EPEL metadata and CentOS signing trust |
| `build_set_stream_repository_args` | Creates command-scoped `STREAM_REPOSITORY_ARGS` |
| `build_verify_manifest` | Verifies exact RPM/SRPM file set and every artifact digest |
| `build_verify_manifest_sources` | Matches manifest source metadata to the approved checkout pins |

Example library use:

```bash
source scripts/lib/build-common.sh
build_load_versions "$PWD/build/versions.conf"
build_verify_manifest /var/packages
build_verify_manifest_sources /var/packages
```

## 5. Local preparation and release scripts

### `scripts/prepare-storage.sh`

Purpose: create every mutable local bind-mount path and set ownership inside
Podman's rootless user namespace. Host ownership may appear as `nobody`; use
`podman unshare` when inspecting it.

- Context: developer host before cluster startup.
- Variable: `FINSIM_DEPLOY_ROOT`, default `.runtime`.
- Creates: application release roots, data/cache/log/report/workflow paths,
  credentials, MariaDB/controller/slurmdbd state, five Slurmd spools and logs.
- Ownership: FinSim 64032 for data/credentials, container root for releases and
  Slurmd spools, MariaDB 27 for database state, Slurm 64030 for control state.
- Idempotency: repeated directory creation and ownership normalization are
  safe. It does not delete state.
- Repository dependencies: none.

Example:

```bash
FINSIM_DEPLOY_ROOT="$PWD/.runtime" scripts/prepare-storage.sh
```

### `scripts/prepare-config.sh`

Purpose: render one canonical, hashed configuration bundle for either the
rootless simulation or production. It centralizes security differences rather
than allowing role containers to invent configuration at startup.

Syntax:

```bash
scripts/prepare-config.sh \
  [--profile simulation|production] [--output-dir DIRECTORY] \
  [--cloud-inventory FILE]
```

Simulation defaults include one controller/database endpoint, HS256 Slurm JWT,
non-cgroup containment and maximums of 100 nodes/10,000 jobs. Production
requires primary/backup controller and slurmdbd names, RS256 JWKS trust,
tls/s2n, cgroup v2 containment, MariaDB TLS and defaults of 4,096 nodes/200,000
jobs.

Important variables:

- common: `FINSIM_CLUSTER_NAME`, `FINSIM_MAX_NODES`, `FINSIM_MAX_JOBS`;
- production endpoints: `FINSIM_CONTROLLER_PRIMARY`,
  `FINSIM_CONTROLLER_BACKUP`, `FINSIM_SLURMDBD_PRIMARY`,
  `FINSIM_SLURMDBD_BACKUP`;
- lifecycle: `FINSIM_LIFECYCLE_ENABLE`, `FINSIM_LIFECYCLE_API_URL`,
  `FINSIM_RESUME_TIMEOUT`, `FINSIM_SUSPEND_TIMEOUT`, `FINSIM_SUSPEND_TIME`,
  `FINSIM_RESUME_RATE`, and `FINSIM_SUSPEND_RATE`;
- inventory: `FINSIM_CLOUD_INVENTORY_FILE` or `--cloud-inventory`, production
  only. The owner-controlled input accepts bounded `State=CLOUD` `NodeName`
  records for `finsim-master` and `finsim-worker`; lifecycle requires it.

Template dependencies are `config/slurm.conf.in`, `config/cgroup.conf.in`,
`config/slurmdbd.conf.template`, `config/job_container.conf`, and
`config/mariadb-server.cnf.in`. Outputs include the rendered files,
`generated-manifest.json`, optional `cloud-nodes.conf`, and optional
`lifecycle-client.conf`. No other script is called or sourced.

Example:

```bash
FINSIM_CONTROLLER_PRIMARY=controller-a.internal \
FINSIM_CONTROLLER_BACKUP=controller-b.internal \
FINSIM_SLURMDBD_PRIMARY=slurmdbd-a.internal \
FINSIM_SLURMDBD_BACKUP=slurmdbd-b.internal \
scripts/prepare-config.sh --profile production \
  --cloud-inventory deploy/cloud-nodes.conf.example \
  --output-dir /tmp/finsim-production-config
```

### `scripts/bootstrap-dev-secrets.sh`

Purpose: create disposable simulation credentials and the three-key
`.runtime/cluster.env`. Production must use external PKI/identity/secret
delivery and must never invoke this script.

- Variables: `FINSIM_IMAGE_TAG` (`latest`), `FINSIM_APP_RELEASE` (`dev`),
  `FINSIM_PROXY_PORT` (`8088`), and optional `FINSIM_CRYPTO_IMAGE`.
- Creates/reuses: MUNGE key, Slurm HS256 key, MariaDB password, RSA application
  signing key, public application JWKS, proxy/client JWTs and a UID-64032 cache
  token copy.
- Direct dependency: invokes `scripts/dev-jwt.py jwks` and
  `scripts/dev-jwt.py token`; if host OpenSSL is unavailable it invokes the
  same helper and OpenSSL inside `FINSIM_CRYPTO_IMAGE`.
- Idempotency: random long-lived development keys are created only when absent;
  JWT/JWKS outputs are regenerated atomically. Secret values are never printed.

Example:

```bash
FINSIM_IMAGE_TAG=review \
FINSIM_APP_RELEASE=2026.11.01.1 \
FINSIM_PROXY_PORT=18088 \
scripts/bootstrap-dev-secrets.sh
```

### `scripts/dev-jwt.py`

Purpose: local-only RS256 JWKS and JWT file generation without external Python
packages or secret output. `bootstrap-dev-secrets.sh` is its normal caller.

Commands:

```bash
python3 scripts/dev-jwt.py jwks \
  --private-key KEY --key-id KID --output JWKS

python3 scripts/dev-jwt.py token \
  --private-key KEY --key-id KID --issuer ISSUER --audience AUDIENCE \
  --subject SUBJECT --scope SCOPE [--lifetime 86400] --output TOKEN
```

Internal functions are `b64url`, strict DER parser `read_tlv`,
`rsa_public_numbers`, atomic writer `atomic_write`, `write_jwks`, `write_token`,
`parser`, and `main`. It calls OpenSSL for public-key extraction and signing.
JWT lifetime is restricted to 60–86,400 seconds. JWKS is mode 0644; tokens are
mode 0600. This is not the production token issuer.

### `scripts/publish-app.sh`

Purpose: build, test and atomically publish an immutable FinSim application
release using `finsim-dev` while keeping production runtime images SDK-free.

Syntax:

```bash
scripts/publish-app.sh [--release ID] [--image IMAGE]
```

- Variables: `FINSIM_APP_RELEASE`, `FINSIM_DEV_IMAGE`, `FINSIM_IMAGE_TAG`.
- Runs inside the development image: locked restore, Release build, managed
  tests and publishes for FinSim, FinSimProxy and FinSimCache.
- Output: `.runtime/apps/releases/<ID>/`, `release-manifest.json`, and an
  atomically updated `.runtime/apps/current` symlink.
- Ownership/mode: output is container-root-owned and hardened to 0555/0444.
- Safety: an existing release ID is never overwritten; use a new ID.
- Repository-script dependencies: none; it consumes the source snapshot baked
  into the selected development image.

Example:

```bash
scripts/publish-app.sh --release 2026.11.01.1 \
  --image localhost/finsim-dev:review
```

### `scripts/prepare-cluster.sh`

Purpose: idempotent orchestration of all local preparation without starting a
container. It is the normal one-command preparation path.

Call sequence:

1. `prepare-storage.sh`;
2. `prepare-config.sh --profile simulation`;
3. `bootstrap-dev-secrets.sh` with `FINSIM_APP_RELEASE`;
4. `publish-app.sh --release` only if that release directory is absent;
5. `validate-deployment.sh`.

Variable: `FINSIM_APP_RELEASE`, default `dev`. The selected image tag and proxy
port are consumed downstream by `bootstrap-dev-secrets.sh`.

Example:

```bash
FINSIM_IMAGE_TAG=review \
FINSIM_APP_RELEASE=2026.11.01.1 \
scripts/prepare-cluster.sh
```

### `scripts/validate-deployment.sh`

Purpose: fail startup before credentials, configuration or an application
release with unsafe type, mode, ownership or content reaches a container.

- Variable: `FINSIM_DEPLOY_ROOT`, default `.runtime`.
- Sourced dependency: `scripts/lib/runtime-env.sh`; calls `runtime_env_load`
  and consumes exported `FINSIM_APP_RELEASE`.
- Checks: required files and non-symlink types, secret modes, configuration
  manifest digests, release identity, exact file set, hashes, symlinks,
  ownership and group/world write bits.
- Podman dependency: uses `podman unshare` so ownership is evaluated as it will
  appear inside rootless containers.
- State: read-only validation; it never repairs or rotates inputs.

Example:

```bash
scripts/validate-deployment.sh
FINSIM_DEPLOY_ROOT=/srv/test-finsim scripts/validate-deployment.sh
```

### `scripts/lib/runtime-env.sh`

Purpose: parse `.runtime/cluster.env` as data instead of sourcing shell code.
Source this library; do not execute it.

Functions:

- `runtime_env_error MESSAGE`: prints a namespaced error and returns 78.
- `runtime_env_load FILE`: requires a mode-0600 regular file containing exactly
  one valid `FINSIM_IMAGE_TAG`, `FINSIM_APP_RELEASE`, and
  `FINSIM_PROXY_PORT`; exports all three values.

Consumers are `cluster-up.sh`, `local-node.sh`, `acceptance-test.sh`, and
`validate-deployment.sh`. Unknown, duplicate, malformed or shell-like lines are
rejected without evaluation.

## 6. Cluster lifecycle and runtime scripts

### `scripts/cluster-up.sh`

Purpose: start only an already-built and already-prepared simulation. Keeping
startup separate prevents it silently building images, publishing releases or
rotating secrets.

Call/dependency sequence:

1. calls `scripts/validate-deployment.sh`;
2. sources `scripts/lib/runtime-env.sh` and calls `runtime_env_load`;
3. checks `localhost/finsim-runtime:$FINSIM_IMAGE_TAG` exists;
4. runs Podman Compose with `.runtime/cluster.env` and no build;
5. waits up to 240 seconds for five idle nodes and proxy readiness;
6. prints controller `sinfo` output.

Example:

```bash
scripts/cluster-up.sh
podman exec finsim-controller sinfo -Nel
```

### `scripts/cluster-down.sh`

Purpose: stop and remove the local Compose containers and networks while
retaining `.runtime` state, credentials, data and releases.

- Input: optional extra arguments are forwarded to `podman compose down`.
- Dependency: `.runtime/cluster.env`; no sourced library.
- Idempotency: returns successfully when the local runtime was never prepared.

Example:

```bash
scripts/cluster-down.sh
scripts/cluster-down.sh --timeout 120
```

### `scripts/local-node.sh`

Purpose: add or safely remove extra dynamic simulation compute nodes without
editing the five-node Compose baseline.

Syntax and examples:

```bash
scripts/local-node.sh add compute-host-006 worker
scripts/local-node.sh add compute-host-007 master
scripts/local-node.sh remove compute-host-006 900
```

- Sourced dependency: `scripts/lib/runtime-env.sh`; calls `runtime_env_load`
  and uses `FINSIM_IMAGE_TAG`.
- Add: creates a retained spool, starts a read-only least-capability container
  on `finsim_control`, supplies dynamic `slurmd -Z` variables and waits for the
  node to reach IDLE.
- Master difference: mounts `/run/credentials`; workers never receive the
  Slurm bearer-token directory.
- Remove: drains the node, waits until `squeue --nodes` is empty, stops the
  container, deletes the dynamic Slurm record and removes the container.
- Safety: only containers named `finsim-dynamic-<node>` are removable; spool
  state is retained. Timeout is bounded to 0–86,400 seconds.

### `scripts/cluster-entrypoint.sh`

Purpose: the universal runtime image entrypoint. It validates role-filtered
mounts, stages credentials/configuration in tmpfs, drops privileges, launches
exactly one selected role and supervises it. Operators do not invoke it
directly; Compose or Quadlet sets `CONTAINER_ROLE`.

Common variables are `CONTAINER_ROLE` and
`CLUSTER_DEPLOYMENT_PROFILE=simulation|production`. Supported roles are
`database`, `slurmdbd`, `controller`, `compute-master`, `compute-worker`,
`restd`, `token-agent`, and `application`.

Role map:

| Role | Main function | Identity and important variables |
|---|---|---|
| database | `run_database` | MariaDB UID 27; `MARIADB_SLURM_PASSWORD_FILE`, optional `MARIADB_CONFIG_FILE` |
| slurmdbd | `run_slurmdbd` | Slurm UID 64030; storage host/port/database/user, password file or pass script, storage CA |
| controller | `run_controller` | Slurm UID 64030; config, MUNGE, JWT trust and TLS inputs |
| compute-master/worker | `run_compute` | `slurmd` remains root; `SLURMD_REGISTRATION_MODE`, `SLURMD_FEATURE`, `SLURM_CONF_SERVER` |
| restd | `run_restd` | slurmrestd UID 64031; `REST_AUTH_MODE`, `SLURM_API_VERSION`, listener/thread settings |
| token-agent | `run_token_agent` | simulation only; starts installed `dev-token-renewer` |
| application | `run_application` | validates release/TLS inputs then replaces PID 1 with UID 64032 |

Function groups:

- diagnostics/supervision: `log`, `die`, `usage_error`, `stop_children`,
  `handle_signal`, `wait_for_essential_process`;
- validation: `require_root_supervisor`, `validate_uint`, `validate_host`,
  `validate_name`, `validate_input_file`, `validate_jwks`,
  `validate_production_slurm_config`, `validate_cgroup_config`;
- config/file staging: `stage_file`, `config_values`, `config_value`,
  `require_config_equals`, `require_config_contains`,
  `prepare_runtime_directories`, `prepare_slurm_config`;
- trust/authentication: `prepare_jwt_trust`, `prepare_tls_identity`,
  `prepare_munge`, `prepare_application_tls`, `read_password_file`;
- readiness: `wait_for_port`, `collect_host_candidates`,
  `normalize_probe_host`, `wait_for_any_port`, `wait_for_any_controller`,
  `wait_for_any_slurmdbd`;
- accounting/database: `render_slurmdbd_config`,
  `register_simulation_accounting`;
- production compute isolation: `require_production_compute_isolation`.

The production isolation function requires non-remapped root, unified cgroup
v2, the host cgroup namespace and the current service's writable delegated
cgroup. It creates and removes a probe child cgroup before `slurmd` starts.

Important role variables include `SLURM_CONFIG_FILE`,
`SLURM_CGROUP_CONFIG_FILE`, `SLURM_JOB_CONTAINER_CONFIG_FILE`,
`SLURM_JWT_KEY_FILE`, `SLURM_JWKS_FILE`, `SLURM_TLS_*`,
`SLURMDBD_STORAGE_*`, `SLURM_CONTROLLER_HOSTS`, `SLURMDBD_HOSTS`,
`START_EMBEDDED_MUNGE`, `MUNGE_KEY_FILE`, `SLURMRESTD_*`, and
`APPLICATION_TLS_*`. Production validation is intentionally stricter than the
simulation and rejects rootless/remapped compute cgroups.

Installed-script dependency: token-agent calls
`/usr/local/sbin/dev-token-renewer`, installed from
`scripts/dev-token-renewer.sh` by `configure-runtime.sh`.

### `scripts/cluster-healthcheck.sh`

Purpose: bounded, read-only, role-local liveness checks for Podman/Quadlet. It
does not perform startup ordering; the entrypoint owns readiness dependencies.

The caller sets `CONTAINER_ROLE` and `CLUSTER_DEPLOYMENT_PROFILE`. Checks are:

- database: MariaDB process and socket ping;
- slurmdbd: Slurm UID process and local TCP 6819;
- controller: Slurm UID process and `scontrol ping`;
- compute: root `slurmd` plus a registered non-FUTURE/non-UNKNOWN host record;
- restd: slurmrestd UID process and its configured listener port;
- token-agent: renewer process and recent non-empty token file;
- application: a process owned by UID 64032. Generated production proxy/cache
  units replace this fallback with bounded HTTPS readiness using the public
  service CA.

Compose and generated Quadlets call the installed
`/usr/local/sbin/cluster-healthcheck`; operators normally use `podman
healthcheck run CONTAINER` rather than invoking the source file.

### `scripts/dev-token-renewer.sh`

Purpose: simulation-only delivery of short-lived ordinary-user Slurm JWTs. It
does not hold the HS256 signing key: it asks the controller over authenticated
Slurm RPC, then atomically publishes only the resulting FinSim token.

- Required opt-in: `ALLOW_DEVELOPMENT_TOKEN_ISSUER=1` and simulation profile.
- Variables: `SLURM_TOKEN_FILE` (default
  `/run/slurm-token/finsim.jwt`), `SLURM_TOKEN_USER` (`finsim`),
  `SLURM_TOKEN_LIFESPAN` (`600`), and `SLURM_TOKEN_REFRESH_SECONDS` (`300`).
- Limits: lifespan 60–900 seconds; refresh at least 30 and less than lifespan.
- Output: atomically replaced UID/GID-64032 mode-0400 token file.
- Caller: token-agent branch of `cluster-entrypoint.sh`.
- Production: forbidden; production uses an external issuer and public JWKS.

## 7. Submission and acceptance scripts

### `scripts/submit-simulation.sh`

Purpose: stable shell entrypoint for developers and CI. It resolves the
repository root and replaces itself with Python.

Direct dependency: executes `scripts/submit-simulation.py` and forwards every
argument unchanged.

Example:

```bash
scripts/submit-simulation.sh --count 100 --chunk-size 10 --parallel 4 --wait
```

### `scripts/submit-simulation.py`

Purpose: minimal host client for FinSimProxy. It reads the bearer token from a
protected file, submits through REST, optionally polls status, and prints the
final JSON report. The token never appears in process arguments.

Options are `--count` (1–10,000), `--chunk-size`, `--parallel`, `--timeout`,
`--host`, `--port`, `--idempotency-key`, and `--wait`. Host/port default from
`FINSIM_PROXY_HOST` and `FINSIM_PROXY_PORT`; token path is fixed at
`.runtime/secrets/api-client.jwt`.

Internal functions:

- `read_token`: validates regular-file type, mode, size and single-line data;
- `request`: bounded HTTP request/response handling with bearer,
  idempotency and correlation headers;
- `main`: validates arguments, submits, polls terminal state and fetches the
  report.

Example:

```bash
FINSIM_PROXY_PORT=18088 scripts/submit-simulation.sh \
  --count 50 --chunk-size 5 --parallel 5 --timeout 600 --wait
```

The local simulation intentionally uses loopback HTTP. Production callers use
the HTTPS service endpoint and their production identity client, not this
development-token file.

### `scripts/acceptance-test.sh`

Purpose: prove the complete local production-parity boundary rather than only
checking that processes started.

Dependencies:

- sources `scripts/lib/runtime-env.sh`, calls `runtime_env_load`, and consumes
  `FINSIM_IMAGE_TAG` and `FINSIM_PROXY_PORT`;
- runs `python3 -m unittest` for `scripts/tests/test_build_scripts.py`;
- invokes `scripts/submit-simulation.sh` for the REST workflow.

It verifies all 12 baseline containers, health status, five-node features,
worker token isolation, unauthenticated HTTP 401, root-job rejection, report
content, accounting user/placement, daemon service UIDs, and runtime-image
absence of SDK/source/application binaries.

Example:

```bash
scripts/cluster-up.sh
scripts/acceptance-test.sh
```

Administrative Slurm CLI calls are allowed here only as assertions; application
source is scanned to ensure it uses REST instead.

## 8. Production render and validation scripts

### `scripts/render-production-unit.sh`

Purpose: render one role-filtered, rootful production Quadlet plus a non-secret
environment example. The strict renderer prevents unrestricted `envsubst`,
whole-secret-directory mounts, mutable tags and accidental privilege sharing.

Required options are `--role`, `--instance`, digest-pinned `--image`, named
`--network`, and `--output-dir`. `proxy`/`cache` additionally require
`--app-release`; controller alone may use `--enable-lifecycle`.

Example:

```bash
scripts/render-production-unit.sh \
  --role compute-worker \
  --instance compute-0001 \
  --image registry.example.com/finsim/runtime@sha256:<64-hex> \
  --network finsim-routable \
  --output-dir ./deployment/compute-0001
```

Template dependency: `deploy/quadlet/finsim-role.container.in`. Internal
functions `add_slurm_config`, `add_munge_socket`, `add_control_tls`, `add_jwks`,
`add_application_tls`, and `add_service_caps` assemble allow-listed role
fragments; `join_lines` passes them to the strict Python marker replacement.

Compute roles deliberately render a trusted dedicated-host profile using
privileged Podman, the host cgroup namespace and the host cgroup v2 mount.
Every other role renders `NoNewPrivileges=true`, drops all defaults and adds
only its allow-listed capabilities. Proxy/cache health commands call the
role's HTTPS readiness endpoint with the public service CA.

Outputs are `finsim-<instance>.container` and
`finsim-<instance>.env.example`. Secrets are referenced as individual
target-host mounts, never copied to output. The renderer rejects host network,
non-digest images, invalid names, lifecycle on non-controller roles and
application releases on non-application roles.

### `scripts/validate-production-config.sh`

Purpose: offline validation of a rendered production configuration directory
before distribution.

```bash
scripts/validate-production-config.sh /etc/finsim/config
```

It verifies required files, production schema/profile, exact manifest file set
and hashes, safe modes, two controllers, complete JWT client identity policy,
Slurm TLS, cgroup constraints, private data, MariaDB secure transport,
lifecycle/inventory pairing and independently parsed cloud records. Storage
placeholders in the private
slurmdbd template are intentionally allowed because the entrypoint fills them
from role credentials at runtime. It calls no other repository script.

### `scripts/validate-production-unit.sh`

Purpose: offline structural validation of one rendered Quadlet. It complements
rather than replaces the target host's Podman generator, SELinux, PKI, network
and storage checks.

```bash
scripts/validate-production-unit.sh \
  ./deployment/compute-0001/finsim-compute-0001.container
```

Common checks enforce digest pinning, a named network, read-only rootfs,
cgroup delegation, resolved markers and no whole-secret-directory mount.
Non-compute roles require no-new-privileges, dropped defaults and no trusted
host settings. Compute roles require privileged Podman, host cgroup namespace
and mount while rejecting control-plane secrets. Application roles require an
HTTPS readiness command. It calls no repository script and changes no state.

## 9. CI and test scripts

### `scripts/ci.sh`

Purpose: TeamCity/general CI host entrypoint. Podman runs on the agent host; the
development container is not given a privileged socket and does not run nested
containers.

Modes:

```bash
BUILD_TAG=ci-123 scripts/ci.sh verify
BUILD_TAG=ci-123 scripts/ci.sh integration
```

`verify` runs shell syntax checks, Python compilation, script unit tests,
application-source REST-only scanning, temporary production config rendering
and validation, then calls `build-images.sh` with `FINSIM_IMAGE_TAG=BUILD_TAG`.

`integration` runs `verify`, then calls `prepare-cluster.sh`, `cluster-up.sh`
and `acceptance-test.sh`. Its EXIT trap calls `cluster-down.sh` unless
`KEEP_CLUSTER=1`. It passes `FINSIM_APP_RELEASE=ci-<BUILD_TAG>` into local
preparation. `BUILD_TAG` is restricted to a safe image/release alphabet.

### `scripts/tests/test_build_scripts.py`

Purpose: fast unit/contract tests for build and deployment glue. It is invoked
by `ci.sh` and `acceptance-test.sh`, not production runtime code.

The 15 tests cover:

1. valid local runtime-environment parsing;
2. rejection of shell syntax without execution;
3. duplicate/unknown local settings;
4. role-filtered digest-pinned production units, trusted compute isolation and
   HTTPS application readiness;
5. valid lifecycle inventory rendering, manifesting and tamper rejection;
6. rejection of lifecycle automation without cloud inventory;
7. rejection of unsafe, symlinked or invalid cloud inventory;
8. approved version-file parsing;
9. rejection of executable version-file content;
10. exact RPM manifest file sets;
11. source-pin matching;
12. paired source override requirements;
13. Containerfile delegation to the three public build phases;
14. Containerfile/version default agreement;
15. proof that runtime configuration does not generate secret material.

The module references `scripts/lib/build-common.sh`,
`scripts/lib/runtime-env.sh`, `build-rpms.sh`, `configure-runtime.sh`,
`install-runtime.sh`, `render-production-unit.sh`,
`validate-production-unit.sh`, `Containerfile`, and `build/versions.conf`.
Subprocess snippets source the two libraries and call `build_load_versions`,
`build_verify_manifest`, `build_verify_manifest_sources`, and
`runtime_env_load`. Other tests invoke the public script CLIs or inspect source
text; the test module does not import runtime implementation code.

Run it directly with:

```bash
PYTHONPYCACHEPREFIX=/tmp/finsim-tests \
  python3 -m unittest discover -s scripts/tests -v
```

## 10. Slurm lifecycle hook scripts

These three source files are installed by `configure-runtime.sh` under
`/usr/local/libexec/slurm`. `prepare-config.sh` names the installed resume and
suspend programs only when `FINSIM_LIFECYCLE_ENABLE=1`. Slurm invokes them;
operators should test them through a staging controller and lifecycle service,
not use them as general cloud CLI tools.

### `deploy/lifecycle/lifecycle-common.sh`

Purpose: cloud-neutral, bounded mTLS client shared by the Slurm resume and
suspend hooks. It deliberately contains no Podman, AWS, Azure or other provider
API calls. The external lifecycle service owns infrastructure authorization
and reconciliation.

Function catalog:

| Function | Contract |
|---|---|
| `lifecycle_die`, `lifecycle_log` | Namespaced fatal and informational logging |
| `lifecycle_load_config` | Parses the root-controlled client file as a fixed data grammar |
| `lifecycle_validate_uint` | Enforces bounded integer settings |
| `lifecycle_validate_private_key` | Requires an EUID-owned private, non-symlink key |
| `lifecycle_validate_public_file` | Requires readable non-writable public files |
| `lifecycle_init` | Loads config, validates mTLS inputs, URL, cluster and time/size bounds |
| `lifecycle_request` | Creates a bounded JSON request, idempotency key, TLS 1.3 curl call and bounded diagnostics |

`SLURM_LIFECYCLE_CONFIG` defaults to `/etc/slurm/lifecycle-client.conf`.
Allowed data keys are `API_URL`, `CLUSTER_ID`, `TLS_CA_FILE`, `TLS_CERT_FILE`,
`TLS_KEY_FILE`, `CONNECT_TIMEOUT_SECONDS`, `TOTAL_TIMEOUT_SECONDS`, and
`MAX_RESUME_CONTEXT_BYTES`. `lifecycle_init` exports corresponding
`LIFECYCLE_*` globals used by `lifecycle_request`.

### `deploy/lifecycle/slurm-resume-program`

Purpose: Slurm `ResumeProgram` adapter. Slurm calls it with one node hostlist
and provides `SLURM_RESUME_FILE`. The hook copies that ephemeral context into a
private temporary directory before any network work, bounds it to 10 MiB, then
sources `lifecycle-common.sh` and calls:

```text
lifecycle_init
lifecycle_request resume NODE_HOSTLIST COPIED_RESUME_CONTEXT
```

Example test shape in a controlled staging environment:

```bash
SLURM_RESUME_FILE=/tmp/resume.json \
SLURM_LIFECYCLE_CONFIG=/etc/finsim/config/lifecycle-client.conf \
/usr/local/libexec/slurm/slurm-resume-program 'compute-[0001-0004]'
```

Do not use production client credentials from an interactive shell merely to
test the API; use a dedicated staging identity.

### `deploy/lifecycle/slurm-suspend-program`

Purpose: Slurm `SuspendProgram` adapter. It accepts one node hostlist, sources
`lifecycle-common.sh`, calls `lifecycle_init`, then calls
`lifecycle_request suspend NODE_HOSTLIST`. Suspend has no resume-context file.

Controlled staging example:

```bash
SLURM_LIFECYCLE_CONFIG=/etc/finsim/config/lifecycle-client.conf \
/usr/local/libexec/slurm/slurm-suspend-program 'compute-[0001-0004]'
```

## 11. Shared files and variables

### Local generated paths

| Path | Writer | Readers |
|---|---|---|
| `.runtime/cluster.env` | `bootstrap-dev-secrets.sh` | runtime-env consumers and Compose |
| `.runtime/config/*` | `prepare-config.sh` | validator and role containers |
| `.runtime/secrets/*` | `bootstrap-dev-secrets.sh` | Compose role-specific mounts |
| `.runtime/container-secrets/cache-client.jwt` | `bootstrap-dev-secrets.sh` | proxy and compute containers |
| `.runtime/credentials/finsim.jwt` | installed `dev-token-renewer` | proxy and master-capable compute |
| `.runtime/apps/releases/<ID>` | `publish-app.sh` | proxy/cache and Slurm jobs, read-only |
| `.runtime/state/*` | `prepare-storage.sh`, then daemons | corresponding role containers |
| `/var/packages/BUILD-MANIFEST` | `build-rpms.sh` | `install-runtime.sh` |

### Most important variable ownership

| Variable family | Writer/operator | Main consumers |
|---|---|---|
| `FINSIM_IMAGE_TAG`, `FINSIM_APP_RELEASE`, `FINSIM_PROXY_PORT` | developer/CI; persisted by bootstrap | local orchestration and Compose |
| production endpoint/capacity `FINSIM_*` | deployment renderer invocation | `prepare-config.sh` |
| `FINSIM_CLOUD_INVENTORY_FILE` | deployment renderer invocation | production scheduler inventory |
| `CONTAINER_ROLE`, `CLUSTER_DEPLOYMENT_PROFILE` | Compose/Quadlet | entrypoint and healthcheck |
| `SLURM_*`, `SLURMD_*`, `SLURMDBD_*` role settings | Compose/Quadlet environment | entrypoint role branches |
| `APPLICATION_TLS_*` | production unit renderer | application entrypoint validation |
| `SLURM_TOKEN_*` | simulation Compose/token-agent | token renewer and healthcheck |
| lifecycle config keys | `prepare-config.sh` output/deployment PKI | lifecycle common client |

## 12. Maintenance checklist

When changing scripts:

1. Add, remove or rename the path in the inventory and dependency matrix.
2. Update every caller/callee relationship and exact sourced function list.
3. Update environment variables, generated files, ownership and permission
   contracts.
4. Keep examples safe and copyable; never include actual tokens or private
   keys.
5. Distinguish simulation-only helpers from production-supported commands.
6. Run:

```bash
bash -n scripts/*.sh scripts/lib/*.sh deploy/lifecycle/*
PYTHONPYCACHEPREFIX=/tmp/finsim-script-tests \
  python3 -m unittest discover -s scripts/tests -v
git diff --check
```

7. Confirm every tracked script path appears in this manual and all local
   Markdown links resolve before committing.
