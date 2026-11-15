# `cluster-healthcheck.sh`

**Tags:** `production`, `simulation`, `runtime`, `testing`

Source: [scripts/cluster-healthcheck.sh](../../scripts/cluster-healthcheck.sh)

## Purpose

Provides a bounded, read-only, role-local health check for Podman/Compose or
Quadlet. It is a liveness/readiness signal, not startup orchestration. The
entrypoint waits for dependencies; the health check verifies that the selected
role remains alive and locally usable.

## Call chain

```text
Podman health manager
    ↓ every configured interval
/usr/local/sbin/cluster-healthcheck
    ├── read CONTAINER_ROLE and profile
    ├── stage SLURM_CONF if readable
    └── role case
          ├── database       → pgrep + mariadb-admin socket ping
          ├── slurmdbd       → pgrep + TCP 6819
          ├── controller     → pgrep + scontrol ping
          ├── compute-*      → pgrep + registered node state
          ├── restd          → pgrep + local listener connect
          ├── token-agent    → renewer + fresh token file
          └── application    → process owned by UID 64032
```

## Definitions

This script has no user-defined functions. Its main definition is the role
`case` statement. It uses `set -euo pipefail`, so any failed check returns
non-zero and marks that health-check attempt failed.

The check uses bounded `timeout 5` operations and never prints bearer tokens.
It deliberately avoids fake Slurm REST payloads where TLS or authentication
would make such a payload misleading.

## Status behavior

- exit 0: the role is healthy;
- non-zero: the role-local check failed;
- unknown role: exit 1;
- repeated failures: Podman records the container as unhealthy according to
  the image or Quadlet retry policy.

It does not restart services itself. Systemd, Quadlet, Compose or another
supervisor decides what to do with the status.
