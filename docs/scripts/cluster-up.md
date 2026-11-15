# `cluster-up.sh` — start the simulation cluster

**Tags:** `simulation` `lifecycle` `testing`

Source: [`scripts/cluster-up.sh`](../../scripts/cluster-up.sh)

## Purpose

[`cluster-up.sh`](../../scripts/cluster-up.sh) starts the local simulation
cluster after its images, configuration, and runtime environment have been
prepared. It is an orchestration script: it does not build images or generate
the cluster configuration itself.

## Call chain

```text
cluster-up.sh
  ├─ validate-deployment.sh
  ├─ load .runtime/cluster.env
  ├─ verify the required image is available
  ├─ podman compose up --no-build --detach --remove-orphans
  ├─ wait for the expected compute nodes
  ├─ wait for the application/API readiness check
  └─ print cluster status; print diagnostics if startup fails
```

The readiness checks are intentionally bounded. A startup problem should
produce a failure and useful logs instead of leaving the command waiting
forever.

## Inputs and outputs

The script consumes the generated runtime environment under `.runtime/`, the
Compose definition, and the image named by that environment. On success, the
simulation containers are running and the expected services are ready. It
does not create a production systemd/Quadlet unit.

## Why `--no-build` matters

The build step is kept separate from cluster startup. This makes a startup
test repeatable and prevents an accidental source change or missing build
dependency from being hidden inside a long `compose up` operation. Run the
image build workflow first when the image is missing or stale; see
[`build-images.md`](./build-images.md).

## Failure behavior

The script fails if deployment validation fails, the required image is absent,
Podman Compose cannot start the services, or readiness is not reached within
the configured timeout. When possible it prints service status and recent
logs so the failure can be diagnosed without manually discovering the
container names.

## When to use it

Use this script for local simulation and test workflows. Production starts the
generated Quadlet units through systemd; the production preparation path is
described in [`render-production-unit.md`](./render-production-unit.md) and
[`cluster-entrypoint.md`](./cluster-entrypoint.md).

