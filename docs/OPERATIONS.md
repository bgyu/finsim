# Local operations

## Lifecycle

Run these as the ordinary development user:

```bash
scripts/build-images.sh
scripts/prepare-cluster.sh
scripts/cluster-up.sh
scripts/submit-simulation.sh --wait
scripts/acceptance-test.sh
scripts/cluster-down.sh
```

Preparation and startup are deliberately separate. Repeat
`validate-deployment.sh` at any time. It checks required files, secret modes,
the selected immutable release and configuration hashes without printing
credentials.

The full command sequence, prerequisites and production runbook are in
[USER_GUIDE.md](USER_GUIDE.md).

## Publish another application release

```bash
scripts/publish-app.sh --release 2026.10.30.2
```

Set `FINSIM_APP_RELEASE=2026.10.30.2` when regenerating `.runtime/cluster.env`,
then recreate the proxy/cache containers. Already submitted jobs continue to
use the earlier absolute release path. Remove old releases only after `squeue`
and accounting prove no job references them.

The development image contains the source snapshot present when it was built.
Rebuild `finsim-dev` after a source change. `prepare-cluster.sh` reuses an
existing selected release and never overwrites it; publish a new release ID
when code changes.

## Dynamic local nodes

```bash
scripts/local-node.sh add compute-host-006 worker
scripts/local-node.sh remove compute-host-006 900
```

Removal drains and waits for jobs, deletes only the dynamic Slurm node and
retains its spool. Do not use `podman stop` as a normal scale-down procedure.

## State and recovery

`cluster-down.sh` keeps `.runtime/state`, `.runtime/data` and application
releases. Back up MariaDB data using database-consistent tools, controller
`StateSaveLocation`, generated configuration and reports. Never copy a live
MariaDB data directory as a logical backup.

For a disposable development reset, stop the cluster first and move the
specific `.runtime` directory aside. Do not recursively delete a deployment
root while containers are running.

## Troubleshooting

```bash
podman ps --format '{{.Names}} {{.Status}}'
podman logs finsim-controller
podman compose --env-file .runtime/cluster.env -f compose.yaml logs slurmdbd
podman exec finsim-controller scontrol ping
podman exec finsim-controller sinfo -Nel
podman exec finsim-controller sacct --starttime today
podman exec finsim-controller ps -eo user,pid,comm,args
```

An interactive `podman exec ... id` normally reports root because it creates a
new administrative process. Inspect `ps` and accounting to determine daemon
and job identity.
