# `validate-production-unit.sh`

**Tags:** `production`, `deployment`, `security`, `testing`

Source: [scripts/validate-production-unit.sh](../../scripts/validate-production-unit.sh)

## Purpose

Performs offline structural checks on one rendered Quadlet unit. It is a
security regression gate before installation on a target host; it does not
replace the target host's Quadlet generator, SELinux, PKI, network, storage or
runtime checks.

## Workflow

```text
validate-production-unit.sh UNIT
    ↓
check regular file and common invariants
    ↓
read exactly one CONTAINER_ROLE
    ↓
validate role-specific mounts/secrets/health command
    ↓
validate compute trusted profile or non-compute restricted profile
    ↓
print structural validity or fail
```

## Definitions

- `fail`: prints a precise invariant failure and exits with status 78.
- The remaining checks are top-level role branches. They verify digest-pinned
  images, named networks, read-only roots, systemd delegation, health commands,
  no unresolved markers and no whole-secret-directory mount.

## Role invariants

Compute units must contain privileged host cgroup settings, read-only config
and app mounts, and must not receive controller/database secrets or
`NoNewPrivileges` claims. Non-compute units must drop capabilities and must not
contain the compute privilege profile. Application units must execute a
versioned proxy/cache binary and have an HTTPS readiness check.
