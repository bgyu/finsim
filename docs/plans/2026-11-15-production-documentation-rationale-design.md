# Production documentation rationale design

## Objective

Enrich `docs/PRODUCTION.md` for production operators while retaining concise
beginner explanations. Document why the production Podman/Quadlet and Slurm
settings exist, where they are authored or generated, and how production
differs from the rootless simulation profile.

## Documentation structure

1. Add a production contract section after the host model. Explain the
   separation between the container image, generated Quadlet unit, rendered
   Slurm configuration, runtime validation, and host services.
2. Add a settings and source-of-truth section. Map Podman/Quadlet settings,
   role-specific generator behavior, Slurm production settings, entrypoint
   gates, mounts, identities, networking, health checks, and systemd
   supervision to their source files and operational rationale.
3. Add a production-versus-simulation comparison table. Cover execution
   identity, cgroups, namespaces, privileges, authentication, TLS, node
   registration, storage, networking, and health checks. Explain why
   simulation sacrifices host-level isolation while production requires it.
4. Add a deployment flow section showing how the source files become rendered
   units and runtime configuration, followed by validation and startup.

## Accuracy constraints

- State that `CgroupsMode=enabled` is Podman/Quadlet container cgroup
  management, while `CgroupPlugin=cgroup/v2` and related settings are Slurm
  job-control settings.
- State that compute roles are trusted host agents using rootful privileged
  Podman, the host cgroup namespace, and a writable host cgroup mount.
- State that non-compute production roles are restricted containers and do not
  receive the compute privilege profile.
- State that simulation uses `CgroupPlugin=disabled`, Linux process tracking,
  and no production isolation claim.
- Distinguish generated artifacts from source templates and avoid claiming
  that local acceptance validates production infrastructure.

## Validation

- Check Markdown links and headings.
- Review the final document against the current Containerfile, Quadlet
  template, production renderer, configuration renderer, and entrypoint.
- Run the repository's documentation/configuration validation available for
  this change, without changing unrelated files.
