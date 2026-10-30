# Production deployment

## Host model

Pin `finsim-runtime` by digest. Each compute host runs one rootful Podman
container under systemd/Quadlet with host cgroup v2 access. Do not use Compose
as a 2,000-node scheduler. Render `deploy/quadlet/finsim-role.container.in` per
host and provide only non-secret role inputs through `node.env`.

Use separate failure domains for primary/backup controllers and slurmdbd.
Controllers share a strongly consistent `StateSaveLocation`; slurmdbd members
connect to the same HA MariaDB endpoint. Database members have separate data
volumes and database-layer replication. Run multiple stateless REST/proxy
instances behind internal TLS endpoints.

## Required production inputs

Render configuration with:

```bash
FINSIM_CONTROLLER_PRIMARY=controller-primary \
FINSIM_CONTROLLER_BACKUP=controller-backup \
FINSIM_SLURMDBD_PRIMARY=slurmdbd-primary \
FINSIM_SLURMDBD_BACKUP=slurmdbd-backup \
FINSIM_MAX_NODES=4096 \
scripts/prepare-config.sh --profile production --output-dir /etc/finsim/config
```

Production startup additionally requires role certificates/keys, CA trust and
RS256 JWKS trust. Private issuer keys never enter the Slurm containers. MUNGE,
TLS and storage credentials are projected at the documented paths with the
fixed numeric ownership required by their consumers.

## Compute isolation and elasticity

Production compute startup rejects a remapped user namespace, non-cgroup-v2
host, or non-writable delegated hierarchy. `slurmd` is root; `DisableRootJobs`
and accounting ensure FinSim payloads are UID 64032.

Provisioning assigns a stable node name, mounts config/secrets/app/data paths,
and starts one `compute-master` or `compute-worker` role. Removal drains the
node and waits for allocations before stopping the container and host. Cloud
API calls belong in provider adapters outside this repository's runtime image.

## Storage

Mount the same paths used locally. `/finsim-app` is a distributed read-only
release tree or verified node-local cache; `/finsim-data` is shared workflow
storage or the implementation behind the same application interface. MariaDB
uses dedicated block storage, Slurmd spool is node-local, and controller state
is HA shared storage. Test restore procedures, not only backups.

## Promotion gates

Build once, record SBOM/provenance, scan and sign the runtime digest, test that
digest in the local production-parity profile, then promote without rebuilding.
Roll control-plane services according to Slurm upgrade guidance and drain
compute nodes in batches. Retain the prior runtime digest, configuration,
controller state and database backup for rollback.

Primary upstream references:

- <https://slurm.schedmd.com/quickstart_admin.html>
- <https://slurm.schedmd.com/configless_slurm.html>
- <https://slurm.schedmd.com/cgroup_v2.html>
- <https://slurm.schedmd.com/rest.html>
- <https://slurm.schedmd.com/jwt.html>
- <https://slurm.schedmd.com/big_sys.html>
