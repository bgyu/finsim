# `configure-runtime.sh`

**Tags:** `build`, `runtime`, `security`

Source: [scripts/configure-runtime.sh](../../scripts/configure-runtime.sh)

## Purpose

Installs immutable runtime identities, directories, configuration templates
and helper scripts into the runtime image. Secret creation and daemon startup
are intentionally not part of this build-time script.

## Call chain

```text
Containerfile finsim-runtime RUN
    ↓
configure-runtime.sh
    ├── validate source assets
    ├── normalize/create fixed identities
    ├── create directory ownership contract
    ├── install entrypoint, healthcheck and lifecycle helpers
    ├── verify UID/GID assignments
    └── verify required Slurm plugins
```

## Fixed identities

The script enforces the repository identity contract:

| Identity | UID/GID | Purpose |
| --- | ---: | --- |
| `munge` | 64029 | MUNGE daemon and key |
| `slurm` | 64030 | Slurm controller/accounting/compute support |
| `slurmrestd` | 64031 | REST daemon |
| `finsim` | 64032 | application and job payload |
| `developer` | 1000 | development image shell |
| `mysql` | 27 | MariaDB package identity |

## Definitions

- `usage`: parses `--source-root`.
- `asset`: validates and resolves a source asset.
- `ensure_group`: creates or verifies a fixed GID and rejects collisions.
- `ensure_user`: creates or verifies a fixed UID, group, home and shell.
- `normalize_munge_identity`: repairs/validates the package-created MUNGE
  identity to UID/GID 64029.
- `install_asset`: copies a validated asset with exact ownership and mode.
- `install_compatibility_symlink`: creates an expected symlink without
  replacing an unexpected existing path.

## Why it matters

Runtime scripts rely on stable numeric identities even when host ownership is
mapped through rootless storage or when containers move between hosts. The
script also makes the runtime assets root-owned and non-writable so a later
role cannot replace its own bootstrap or health-check code.
