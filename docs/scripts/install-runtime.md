# `install-runtime.sh`

**Tags:** `build`, `runtime`, `security`

Source: [scripts/install-runtime.sh](../../scripts/install-runtime.sh)

## Purpose

Installs the verified runtime subset from the RPM artifact directory into the
`finsim-runtime` image. It ensures that every daemon and plugin required by the
Containerfile's runtime contract exists and has resolvable shared libraries.

## Call chain

```text
Containerfile finsim-runtime RUN
    ↓
install-runtime.sh
    ├── lib/build-common.sh manifest/source verification
    ├── RPM selection by package name
    ├── dnf install runtime RPMs and dependencies
    ├── executable checks
    └── plugin and ldd checks
```

## Workflow

1. Parse `--versions-file` and `--packages-dir`.
2. Resolve both paths and load the pinned version contract.
3. Require root, EL10 and the build/query tools.
4. Verify the artifact manifest and source digests.
5. Select exactly one RPM for each required package: MUNGE, Slurm core,
   `slurmctld`, `slurmd`, `slurmdbd` and `slurmrestd`.
6. Install those RPMs and runtime dependencies such as Bash, OpenSSL, Python,
   MariaDB and ASP.NET runtime.
7. Clean package-manager caches.
8. Confirm daemon executables and required Slurm plugins exist.
9. Use `ldd` to reject unresolved plugin dependencies.

## Definitions

- `usage`: prints options.
- The package-selection loops are intentionally top-level: they build a
  name-to-path map and reject duplicate providers before installation.
- `wanted_rpms`, `runtime_dependencies` and the plugin list are the explicit
  runtime contract.

## Failure behavior

The script fails before installation when the artifact set is unverified,
incomplete or ambiguous. It fails after installation if an executable/plugin
is missing or a plugin has an unresolved dependency. It does not start any
daemon.
