# Operations runbook

## Local lifecycle

Start and validate:

```bash
scripts/cluster-up.sh
scripts/acceptance-test.sh
```

Submit a different bounded workload:

```bash
scripts/submit-simulation.sh --count 100 --chunk-size 10 --parallel 4 --wait
```

Inspect scheduler state with either the container name or Compose service name:

```bash
podman exec finsim-controller sinfo --Node --long
podman compose --env-file .runtime/cluster.env -f compose.yaml exec controller squeue
```

Stop while retaining named volumes:

```bash
scripts/cluster-down.sh
```

Passing `--volumes` to `cluster-down.sh` deletes local simulation database,
controller, cache, report, token, and spool volumes. That is destructive and is
appropriate only when intentionally resetting disposable development data.

## Triage order

1. `podman ps -a --format '{{.Names}} {{.Status}}'`
2. cache and proxy health/logs;
3. `slurmrestd` authentication/parser logs;
4. `sinfo`, `squeue`, then `sacct` on `finsim-controller`;
5. master/worker logs under the `finsim-data` named volume;
6. keyed state in FinSimCache only through its authenticated API.

Useful commands:

```bash
podman logs --tail=200 finsim_cache_1
podman logs --tail=200 finsim_proxy_1
podman logs --tail=200 finsim_restd_1
podman logs --tail=200 finsim-controller
podman exec finsim-controller scontrol show job <job-id>
podman exec finsim-controller sacct -j <job-id> --format=JobID,JobName,User,State,ExitCode,NodeList
```

Do not print `/run/slurm-token/*`, `.runtime/secrets/*`, process environments,
or Authorization headers while collecting diagnostics.

## Common failure meanings

| Symptom | Likely boundary | Action |
|---|---|---|
| Proxy 401 | client JWT | Verify issuer/audience/expiry/scope and clock; do not bypass validation |
| Proxy 409 | idempotency conflict | Same caller key was reused with different input; choose a new business idempotency key |
| Proxy 422 | Slurm rejected request | Inspect `slurmrestd` and policy/partition/accounting association |
| `MASTER_SUBMISSION_UNKNOWN` | ambiguous transport after intent | Reconcile exact comment marker; never blindly resubmit |
| master pending | orchestrator pool unavailable | Check feature, partition, node state, limits, and token placement |
| worker pending | worker pool unavailable | Scale/provision worker nodes or inspect constraints/QOS |
| report not found after completion | finalization or filesystem | Inspect master log, private file mode/owner, and durable data service |
| node DOWN after resume | lifecycle/boot timeout | Correlate adapter operation ID, node bootstrap, TLS/MUNGE/configless registration |

## Production scale out/in

Scale-out is handled by the site lifecycle adapter or by manually starting an
approved compute deployment with a free FUTURE identity and matching feature.
Verify registration and health before relying on it:

```text
scontrol show node <name>
sinfo -N -n <name> -o '%N %T %f %C %m %E'
```

For scale-in, first prevent new allocations (`DRAIN` with an auditable reason),
wait for jobs/steps to reach zero or follow the approved cancellation policy,
then invoke suspend/termination. Return the node to FUTURE/POWERED_DOWN state
according to the site's lifecycle design. Never terminate active nodes just to
meet an autoscaling target.

Keep at least one healthy orchestrator node independent of worker scaling. A
master may be requeued only when input, intent records, worker result keys, and
report output remain durable and idempotent.

## Rotation

- Rotate API/cache/Slurm signing keys with overlapping public JWKS trust, issue
  new short-lived tokens, confirm consumers reload, then remove old public keys.
- Rotate TLS with a common trust overlap and role-specific keys. Verify all RPC
  and HTTPS paths before removing the old CA.
- Rotate MUNGE only with a documented cluster-wide maintenance sequence; mixed
  keys partition the cluster.
- Rotate DB credentials through the external secret system and restart/failover
  `slurmdbd` in a controlled sequence.

All rotation tests belong in a non-production environment first. Development
bootstrap credentials are regenerated each `cluster-up.sh` and are not a model
for production key custody.

## Upgrade and rollback

Record the source pins, RPM manifest, app commit, image digest, schema versions,
and rendered configuration as one release. Test the exact digest with the full
acceptance suite. Back up state, drain/canary nodes, and follow SchedMD's version
compatibility order for database/controllers/daemons. Rollback means restoring
the previous signed image/config and, where schema changes require it, the
tested state/database recovery point—not rebuilding an old tag.
