# `cluster-down.sh` — stop the simulation cluster

**Tags:** `simulation` `lifecycle` `storage`

Source: [`scripts/cluster-down.sh`](../../scripts/cluster-down.sh)

## Purpose

[`cluster-down.sh`](../../scripts/cluster-down.sh) stops and removes the local
simulation containers managed by Podman Compose. It is the normal teardown
operation for the simulation workflow.

## Call chain

```text
cluster-down.sh
  ├─ load the generated runtime environment when present
  ├─ locate the Compose project
  └─ podman compose down --remove-orphans [forwarded arguments]
```

If the runtime environment has not been created, the script can safely report
that there is no prepared simulation deployment to stop.

## What it removes

The ordinary command removes the Compose-managed containers and network. It
does not normally delete the project’s named volumes or the `.runtime/`
configuration directory. This preserves database and other state for the next
simulation run.

Additional arguments are forwarded to the Compose teardown command. Use
volume-removal options only when intentionally discarding simulation state;
that operation is materially different from simply stopping the cluster.

## Failure behavior

The script returns a failure when the Compose teardown command fails. It does
not attempt to repair or rebuild the deployment. If the goal is a clean reset,
remove state explicitly according to the project’s simulation instructions,
then run the preparation workflow again.

## Relationship to production

This script is for the local Compose-based simulation. Production services are
managed by systemd/Quadlet and should be stopped through their generated
systemd units, not by treating the production container as a Compose service.

