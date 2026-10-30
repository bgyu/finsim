# Two-image production-parity implementation plan

## Goal

Replace the current `slurm-runtime`/`finsim-app`/`finsim-sim`/`finsim-dev`
deployment chain with two published images and a fully external deployment
bundle, while preserving the working REST-driven FinSim workflow.

## Work sequence

1. Refactor `Containerfile` so the internal RPM builder feeds one
   `finsim-runtime` target containing Slurm, MariaDB and .NET runtime packages;
   make `finsim-dev` add only development/CI tools.
2. Replace baked simulation/production profiles with generated configuration
   inputs mounted under `/etc/finsim-cluster` and staged into private runtime
   directories by the role entrypoint.
3. Add explicit preparation commands for configuration, secrets, storage and
   immutable application releases.  Add a validation command and a wrapper
   which runs all preparation without starting containers.
4. Update the role entrypoint and health checks to use `compute-master`,
   `compute-worker`, `token-agent` and `application`, while retaining fixed
   service identities and non-root jobs.
5. Rewrite Compose so every service uses `finsim-runtime`, binds the prepared
   deployment tree, and never builds or generates mutable input at startup.
6. Update lifecycle and acceptance scripts for the single image, versioned
   application paths, explicit preparation and persisted host directories.
7. Add a production per-host environment/Quadlet contract and rootful cgroup
   validation without embedding provider-specific provisioning.
8. Consolidate user documentation around the new contract; delete old plans,
   profile files and documentation that describe superseded image boundaries.
9. Run shell syntax, Python, .NET, Compose-rendering, container-build and
   end-to-end tests.  Commit implementation and final documentation separately.

## Safety constraints

- Preserve user-owned work and delete only files made obsolete by this design.
- Do not print key, password or bearer-token values.
- Do not put secrets in image layers, environment variables or command lines.
- Make preparation idempotent and startup fail closed.
- Keep application releases immutable and execute jobs using versioned paths.
- Keep `slurmd` root while enforcing UID/GID 64032 for every FinSim payload.
