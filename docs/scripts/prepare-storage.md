# `prepare-storage.sh`

**Tags:** `storage`, `simulation`, `shared`

Source: [scripts/prepare-storage.sh](../../scripts/prepare-storage.sh)

## Purpose

Creates the local mutable directory tree used by the Compose simulation and
maps ownership into the rootless Podman user namespace. It prepares paths; it
does not build images or start services.

## Workflow

```text
prepare-storage.sh
    ↓
resolve FINSIM_DEPLOY_ROOT or .runtime
    ↓
verify Podman exists
    ↓
create application, state, logs, credentials and spool paths
    ↓
podman unshare chown paths to container identities
```

The directories include releases, application/workflow data, MariaDB state,
controller state, accounting state, per-node Slurmd spools and role logs.

## Definitions and safety

There are no user-defined functions. The directory list is deliberately
explicit, and `podman unshare chown` performs ownership changes in the same
rootless namespace used by the containers. This avoids treating host UID 0 as
container UID 0.

The script does not delete existing state. It is safe to rerun for missing
paths, but ownership changes should be reviewed when changing the deployment
root or identity contract.
