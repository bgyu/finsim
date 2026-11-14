# Container concepts guide design

## Objective

Create a comprehensive beginner-to-intermediate conceptual guide at
`docs/CONTAINERS.md`. The guide will explain how Linux containers work and
then map the concepts to FinSim's production Slurm deployment.

## Guide structure

1. Establish the mental model: processes, images, containers, runtimes,
   registries, Containerfiles, layers, and build-versus-run time.
2. Explain the Podman stack and lifecycle: Podman CLI, Buildah, OCI runtime,
   conmon, container PID 1, `ENTRYPOINT`, `CMD`, shutdown and health checks.
3. Explain Linux isolation mechanisms in depth:
   - namespaces: PID, mount, network, user, UTS, IPC, cgroup and time;
   - cgroups v1/v2, controllers, delegation, limits, accounting and the
     distinction between container and job cgroups;
   - Linux capabilities and why `--privileged` is a broad exception;
   - seccomp system-call filtering;
   - SELinux/AppArmor policy and labels;
   - filesystem layers, mounts, bind mounts, volumes, read-only roots and
     mount propagation;
   - rootless user namespaces and identity mapping.
4. Explain how the mechanisms combine into defense in depth, including what
   each mechanism can and cannot protect against.
5. Map the concepts to this repository:
   - image stages and runtime instructions in `Containerfile`;
   - role-specific Quadlet generation;
   - common `CgroupsMode=enabled` and systemd delegation;
   - compute-only `--privileged`, `--cgroupns=host` and writable cgroup mount;
   - Slurm `CgroupPlugin=cgroup/v2`, task, process and accounting plugins;
   - entrypoint validation, identity dropping, mounts, health checks and
     production failure-closed behavior.
6. Compare production and simulation, explaining why simulation disables
   Slurm cgroups and permits rootless operation while production compute uses a
   trusted host-agent boundary.
7. Provide inspection and troubleshooting commands with interpretation,
   followed by a glossary and rules of thumb.

## Accuracy and teaching constraints

- Clearly distinguish Linux kernel mechanisms from Podman options, Quadlet
  directives, systemd settings and Slurm settings.
- Explain that `--cgroupns=host` exposes the host cgroup namespace only; it
  does not expose every host namespace.
- Explain that `CgroupsMode=enabled` enables Podman container cgroup
  management, while Slurm `CgroupPlugin=cgroup/v2` manages job cgroups.
- Explain that root inside a rootless container is not host root.
- Describe `--privileged` as a broad trusted-host exception, not as a normal
  production default.
- Avoid claiming that containers are virtual machines or a complete security
  boundary.
- Keep production claims aligned with `docs/PRODUCTION.md`, the Quadlet
  template, `scripts/render-production-unit.sh`, `scripts/prepare-config.sh`,
  and `scripts/cluster-entrypoint.sh`.

## Navigation and validation

- Add links from `README.md`, `docs/PRODUCTION.md`, and, where useful,
  `docs/ARCHITECTURE.md`.
- Link explanations to source files and relevant settings rather than copying
  large implementation sections.
- Validate all local Markdown links and run `git diff --check`.
