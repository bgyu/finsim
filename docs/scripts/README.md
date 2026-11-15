# Detailed script guides

This directory explains the operational-core scripts in detail. Each page
describes what the script does, why it exists, who calls it, its call chain,
inputs and outputs, function definitions, workflow, failure behavior and
security implications.

## Operational call graph

```text
build-images.sh
  └── podman build
        └── build-rpms.sh

prepare-config.sh
  └── rendered slurm.conf, cgroup.conf and production inventory
        └── validate-production-config.sh

render-production-unit.sh
  └── deploy/quadlet/finsim-role.container.in
        └── validate-production-unit.sh
              └── systemd/Quadlet
                    └── cluster-entrypoint.sh
                          ├── run_database
                          ├── run_slurmdbd
                          ├── run_controller
                          ├── run_compute
                          ├── run_restd
                          ├── run_token_agent
                          └── run_application
                                └── cluster-healthcheck.sh
```

## Reading a guide

Tags describe intended workflows, not security guarantees. The source file is
always authoritative. “Caller” can mean a host script, Podman/Compose, Quadlet
or systemd. “Callee” includes both directly executed commands and important
scripts invoked inside a container.

## Source links

- [Containerfile](../../Containerfile)
- [production guide](../PRODUCTION.md)
- [container concepts](../CONTAINERS.md)
- [Quadlet template](../../deploy/quadlet/finsim-role.container.in)
- [runtime entrypoint source](../../scripts/cluster-entrypoint.sh)
