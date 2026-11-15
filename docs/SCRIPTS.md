# Script overview

This page is the high-level map of FinSim automation. It is intended to answer
“which script should I run?” and “what workflow does this belong to?” Detailed
function-by-function explanations live under [docs/scripts/](scripts/).

For a detailed explanation of the runtime container itself, see
[Containers, Linux isolation and FinSim](CONTAINERS.md). For production
controls, see [PRODUCTION.md](PRODUCTION.md).

## How the workflows fit together

```text
build-images.sh
    ├── build-rpms.sh inside rpm-builder
    └── produces finsim-runtime and finsim-dev

prepare-cluster.sh / production operator inputs
    ├── prepare-storage.sh
    ├── prepare-config.sh
    └── bootstrap-dev-secrets.sh (simulation only)

production configuration
    ├── validate-production-config.sh
    ├── render-production-unit.sh
    └── validate-production-unit.sh

orchestration
    ├── cluster-up.sh / cluster-down.sh
    └── Quadlet or Compose starts finsim-runtime
            └── cluster-entrypoint.sh
                    └── selected role daemon
                            └── cluster-healthcheck.sh
```

Scripts are grouped by responsibility rather than by the directory from which
they happen to be called. A script can have multiple tags.

## Tags

- `production`: used by or required for production deployment;
- `simulation`: local disposable cluster behavior;
- `testing`: acceptance, validation or CI checks;
- `build`: image, RPM or runtime assembly;
- `runtime`: executes inside the runtime image;
- `configuration`: renders or validates configuration;
- `deployment`: creates host/service deployment definitions;
- `storage`: prepares persistent or disposable state;
- `security`: validates identities, secrets, certificates or privileges;
- `lifecycle`: starts, stops, adds or removes cluster resources;
- `shared`: used by more than one workflow.

## Detailed operational-core guides

### Build and runtime image

| Script | Tags | Role |
| --- | --- | --- |
| [build-images.sh](scripts/build-images.md) | `build`, `shared` | Builds the two published image targets. |
| [build-rpms.sh](scripts/build-rpms.md) | `build`, `shared` | Downloads, verifies and builds Slurm/MUNGE RPMs. |
| [install-runtime.sh](scripts/install-runtime.md) | `build`, `runtime`, `security` | Installs runtime packages and validates the package set. |
| [configure-runtime.sh](scripts/configure-runtime.md) | `build`, `runtime`, `security` | Creates fixed users/groups and installs immutable runtime assets. |

### Configuration and storage

| Script | Tags | Role |
| --- | --- | --- |
| [prepare-storage.sh](scripts/prepare-storage.md) | `storage`, `simulation`, `shared` | Creates local state and maps ownership for rootless storage. |
| [prepare-config.sh](scripts/prepare-config.md) | `configuration`, `production`, `simulation` | Renders the Slurm configuration bundle for a profile. |
| [validate-production-config.sh](scripts/validate-production-config.md) | `production`, `configuration`, `security` | Validates the rendered production bundle offline. |

### Production deployment

| Script | Tags | Role |
| --- | --- | --- |
| [render-production-unit.sh](scripts/render-production-unit.md) | `production`, `deployment`, `security` | Generates one role-filtered Quadlet unit and environment example. |
| [validate-production-unit.sh](scripts/validate-production-unit.md) | `production`, `deployment`, `security`, `testing` | Verifies generated Quadlet privilege and mount invariants. |

### Runtime and local lifecycle

| Script | Tags | Role |
| --- | --- | --- |
| [cluster-entrypoint.sh](scripts/cluster-entrypoint.md) | `production`, `simulation`, `runtime`, `security` | Universal PID 1 bootstrap, validation, role launcher and supervisor. |
| [cluster-healthcheck.sh](scripts/cluster-healthcheck.md) | `production`, `simulation`, `runtime`, `testing` | Bounded role-local health checks. |
| [cluster-up.sh](scripts/cluster-up.md) | `simulation`, `lifecycle` | Validates prepared local inputs and starts the Compose topology. |
| [cluster-down.sh](scripts/cluster-down.md) | `simulation`, `lifecycle`, `storage` | Stops local containers while retaining or deleting selected state. |

## Remaining scripts

These remain high-level in this first documentation pass and are candidates
for later detailed pages:

| Script | Tags | Summary |
| --- | --- | --- |
| `prepare-cluster.sh` | `simulation`, `configuration`, `storage` | Coordinates local preparation. |
| `bootstrap-dev-secrets.sh` | `simulation`, `security` | Creates disposable local credentials and environment. |
| `dev-jwt.py` | `simulation`, `security` | Creates local development JWT material. |
| `publish-app.sh` | `build`, `simulation`, `testing` | Builds, tests and publishes an immutable application release. |
| `local-node.sh` | `simulation`, `lifecycle` | Adds or safely removes a local dynamic node. |
| `dev-token-renewer.sh` | `simulation`, `runtime`, `security` | Renews development-only ordinary-user Slurm tokens. |
| `submit-simulation.sh` / `.py` | `simulation`, `testing` | Submits and waits for the demonstration workflow. |
| `acceptance-test.sh` | `testing`, `simulation`, `security` | Exercises the local workflow and security/accounting assertions. |
| `validate-deployment.sh` | `testing`, `simulation`, `configuration` | Validates prepared local deployment inputs. |
| `ci.sh` | `testing`, `build`, `simulation` | TeamCity/CI orchestration entrypoint. |
| `scripts/tests/test_build_scripts.py` | `testing`, `build` | Tests build and production-rendering contracts. |
| `deploy/lifecycle/lifecycle-common.sh` | `production`, `lifecycle` | Shared bounded lifecycle-client behavior. |
| `deploy/lifecycle/slurm-resume-program` | `production`, `lifecycle` | Requests node resume through the external lifecycle service. |
| `deploy/lifecycle/slurm-suspend-program` | `production`, `lifecycle` | Requests node suspension through the external lifecycle service. |
| `scripts/lib/build-common.sh` | `build`, `shared` | Shared build helpers. |
| `scripts/lib/runtime-env.sh` | `simulation`, `shared` | Shared local runtime environment helpers. |

## Common rules

- Run scripts from the repository root unless their guide says otherwise.
- Read the detailed guide before invoking a runtime entrypoint directly.
- Treat `.runtime/` and production output directories as generated artifacts.
- Prefer the orchestrator (`cluster-up.sh`, Compose, Quadlet or systemd) over
  calling a container-internal script manually.
- Do not pass secrets on command lines when a file or environment contract is
  provided.
- A successful validation script means that its defined contract passed; it
  does not prove that external DNS, PKI, storage, cloud APIs or HA behavior
  are working.
