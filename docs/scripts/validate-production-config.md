# `validate-production-config.sh`

**Tags:** `production`, `configuration`, `security`, `testing`

Source: [scripts/validate-production-config.sh](../../scripts/validate-production-config.sh)

## Purpose

Validates a rendered production configuration directory before it is mounted
into containers or distributed to hosts. It is an offline gate, not a test of
DNS, PKI, storage, network reachability or daemon startup.

## Workflow

```text
validate-production-config.sh DIRECTORY
    ↓
resolve a real directory
    ↓
Python validator checks required files and manifest
    ↓
verify every SHA-256 digest and permissions
    ↓
verify Slurm, JWT, TLS, HA and cgroup invariants
    ↓
accept or fail before deployment
```

## What it validates

- exact required file set and production manifest schema;
- no symlinks or group/world-writable generated files;
- `DisableRootJobs=YES`, JWT/JWKS policy, TLS/s2n and cgroup process/task
  settings;
- exactly two controller records;
- lifecycle hooks only when lifecycle configuration and cloud inventory exist;
- normalized cloud inventory fields, features, state and resource bounds;
- `ConstrainCores`, RAM, swap and device settings;
- MariaDB TLS and secure transport;
- no unresolved template markers.

## Definitions

The shell portion validates the argument and delegates all bundle inspection to
one embedded Python definition. There are no reusable shell functions because
the operation is intentionally one atomic validation pass.

Failure exits non-zero with the invariant that failed. It does not modify the
bundle.
