# `prepare-config.sh`

**Tags:** `configuration`, `production`, `simulation`, `security`

Source: [scripts/prepare-config.sh](../../scripts/prepare-config.sh)

## Purpose

Renders the Slurm configuration bundle consumed by controllers, accounting
daemons and compute nodes. It is the profile boundary between portable local
simulation and production configuration.

## Call chain

```text
operator / prepare-cluster.sh / production deployment
    ↓
prepare-config.sh --profile simulation|production
    ├── validate host and bounded numeric inputs
    ├── optionally validate and normalize cloud-nodes.conf
    ├── choose profile-specific Slurm values
    ├── substitute config/*.in templates
    ├── write generated-manifest.json
    └── output directory consumed by validators and containers
```

## Profile behavior

Simulation selects local controller/accounting names, development JWT trust,
Linux process tracking, no task cgroup plugin, Linux accounting and
`CgroupPlugin=disabled`. It rejects cloud inventory and lifecycle integration.

Production requires primary/backup controller and `slurmdbd` endpoints, uses
MUNGE plus RS256 JWKS trust, TLS, cgroup v2 process/task/accounting plugins,
resource constraints and optional validated cloud inventory.

## Definitions

- `validate_host`: rejects unsafe endpoint names.
- `validate_bounded_uint`: validates bounded node/job/resource values.
- `prepare_cloud_inventory`: safely opens an operator-owned inventory, checks
  exact fields/ranges/features and writes normalized `State=CLOUD` records.
- The profile branches set the template substitutions; the final rendering
  writes the bundle and a manifest of file hashes.

## Outputs and failure behavior

Outputs include `slurm.conf`, `cgroup.conf`, `job_container.conf` where
applicable, `slurmdbd.conf.template`, MariaDB configuration, lifecycle inputs
and `generated-manifest.json`. It rejects malformed hosts, missing production
HA inputs, untrusted cloud inventory, simulation-only lifecycle options and
unresolved template markers.
