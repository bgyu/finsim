# `render-production-unit.sh`

**Tags:** `production`, `deployment`, `security`, `configuration`

Source: [scripts/render-production-unit.sh](../../scripts/render-production-unit.sh)

## Purpose

Generates one role-specific systemd/Quadlet `.container` unit and a non-secret
environment example. It is the controlled translation from a production role
to Podman settings, mounts, capabilities, health checks and service execution.

## Call chain

```text
operator / production provisioning
    ↓
render-production-unit.sh --role ... --image ... --network ...
    ├── validate role, instance, digest-pinned image and network
    ├── select role-specific environment, capabilities and mounts
    ├── select trusted compute or restricted security profile
    ├── substitute deploy/quadlet/finsim-role.container.in
    ├── reject unresolved markers
    └── write .container and .env.example
```

## Definitions

- `usage`: prints accepted roles and options.
- `add_slurm_config`: mounts rendered Slurm configuration.
- `add_munge_socket`: supplies an external MUNGE socket contract.
- `add_control_tls`: adds role-specific Slurm CA/certificate/key mounts.
- `add_jwks`: adds public Slurm JWKS trust.
- `add_application_tls`: adds application CA and role certificate mounts.
- `add_service_caps`: adds the small startup capability allow-list.
- `join_lines`: converts arrays to multiline template substitutions.
- The embedded Python block substitutes every marker and rejects unresolved
  uppercase markers.

## Security branches

Non-compute roles receive `NoNewPrivileges=true` and `DropCapability=all`,
plus only role-specific capabilities. Compute roles receive:

```text
PodmanArgs=--privileged --cgroupns=host
Volume=/sys/fs/cgroup:/sys/fs/cgroup:rw
```

This is the trusted production `slurmd` host-agent profile. The script does not
accept arbitrary caller-provided mounts or capabilities.

## Outputs and failure behavior

The generated unit is mode 0644 and the environment example is mode 0644 with
no secret values. The script fails before writing useful output for unsupported
roles, invalid names, unpinned images, host networking, missing app release
values or invalid output paths.
