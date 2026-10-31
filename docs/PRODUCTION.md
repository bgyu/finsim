# Production deployment

## Host model

Pin `finsim-runtime` by digest. Each compute host runs one rootful Podman
container under systemd/Quadlet with host cgroup v2 access. Do not use Compose
as a 2,000-node scheduler. Render each role through
`scripts/render-production-unit.sh`; never run `envsubst` against its internal
template. The renderer provides role-filtered mounts and a non-secret
environment example.

Generated units require a pre-created named Podman network that provides
routable addresses across hosts. This can be an operator-managed
macvlan/ipvlan or an approved network plugin; a default host-local bridge is
not sufficient. Provide DNS/service discovery separately. Units reject host
networking and do not grant `SYS_ADMIN`. Confirm systemd cgroup v2 delegation
on each compute host instead of broadening privileges.

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
scripts/validate-production-config.sh /etc/finsim/config
```

Production startup additionally requires role certificates/keys, CA trust and
RS256 JWKS trust. Private issuer keys never enter the Slurm containers. MUNGE,
TLS and storage credentials are projected at the documented paths with the
fixed numeric ownership required by their consumers.

Proxy and demonstration cache roles serve HTTPS directly with role-filtered
PEM identities. The public service CA bundle is also projected to compute nodes and
propagated through Slurm job environments; server private keys never enter a
job allocation.

The exact host layout, ownership modes, role variables and render commands are
in [USER_GUIDE.md](USER_GUIDE.md).

## Compute isolation and elasticity

Production compute startup rejects a remapped user namespace, non-cgroup-v2
host, or non-writable delegated hierarchy. `slurmd` is root; `DisableRootJobs`
and accounting ensure FinSim payloads are UID 64032.

Provisioning assigns a stable node name, mounts config/secrets/app/data paths,
and starts one `compute-master` or `compute-worker` role. Dynamic registration
uses `slurmd -Z`; predeclared FUTURE and cloud inventories use the explicit
`future` and `static` modes. Removal drains the node, waits for allocations and
reservations, stops the container, then deletes its dynamic record. Cloud API
calls belong behind the external mTLS lifecycle service.

Optional `FINSIM_LIFECYCLE_ENABLE=1` rendering connects the installed bounded
resume/suspend clients to Slurm. It does not implement the lifecycle service or
cloud adapter.

## Storage

Mount the same paths used locally. `/finsim-app` is a distributed read-only
release tree or verified node-local cache; `/finsim-data` is shared workflow
storage or the implementation behind the same application interface. MariaDB
uses dedicated block storage, Slurmd spool is node-local, and controller state
is HA shared storage. Test restore procedures, not only backups.

The repository database role enables server TLS in the production profile but
remains one MariaDB process, not an HA database. Large clusters should use a
separately operated replicated MariaDB service.

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
- <https://slurm.schedmd.com/dynamic_nodes.html>
- <https://slurm.schedmd.com/power_save.html>
- <https://docs.podman.io/en/latest/markdown/podman-systemd.unit.5.html>

Complete every launch gate in [REVIEW.md](REVIEW.md). Local acceptance does not
validate production networking, PKI, identity, storage, lifecycle, monitoring
or disaster recovery.
