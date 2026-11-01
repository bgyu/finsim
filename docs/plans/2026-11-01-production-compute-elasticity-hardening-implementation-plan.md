# Production compute and elasticity hardening implementation plan

Date: 2026-11-01
Design: `2026-11-01-production-compute-elasticity-hardening-design.md`

## 1. Add cloud-inventory configuration tests

- Extend `scripts/tests/test_build_scripts.py` with a valid production
  inventory containing separate master and worker `State=CLOUD` ranges.
- Assert that lifecycle rendering fails when inventory is missing or empty.
- Add negative cases for symlinks, group/world-writable files, unknown Slurm
  keys, missing CPU/memory, non-CLOUD state, unsupported features, duplicate
  node expressions, shell syntax and unresolved markers.
- Assert that the generated manifest contains and hashes the inventory and that
  `validate-production-config.sh` detects tampering.

## 2. Render a bounded cloud inventory

- Add a single cloud-inventory marker to `config/slurm.conf.in`.
- Extend `scripts/prepare-config.sh` with `--cloud-inventory FILE` and a
  production-only environment equivalent for non-interactive deployment.
- Read the file as data, validate ownership/mode/type and parse every effective
  `NodeName=` record without evaluating it.
- Require `State=CLOUD`, explicit positive `CPUs` and `RealMemory`, and exactly
  one supported feature (`finsim-master` or `finsim-worker`).
- Reject duplicate node expressions and arbitrary configuration keys.
- Copy the validated inventory into the generated bundle, include it in the
  manifest, and render its records into `slurm.conf`.
- Require a non-empty inventory when lifecycle integration is enabled.

## 3. Harden production-config validation

- Teach `scripts/validate-production-config.sh` that the cloud inventory is an
  optional manifest member and mandatory with resume/suspend hooks.
- Re-parse the copied inventory independently rather than trusting the
  renderer.
- Require `use_jwt_client_ids_only` in both Slurm and slurmdbd authentication
  settings.
- Verify the configured node budget is positive and large enough to be a
  meaningful explicit ceiling; leave exact hostlist expansion to the target
  Slurm validation gate.

## 4. Generate the trusted compute-host Quadlet contract

- Add role-specific security markers to
  `deploy/quadlet/finsim-role.container.in` so broad compute settings cannot
  leak into other roles.
- For `compute-master` and `compute-worker`, render the rootful trusted-host
  profile: privileged compute execution, host cgroup namespace, host cgroup v2
  mount, systemd delegation, read-only image and existing role-filtered mounts.
- Preserve `DropCapability=all`, `NoNewPrivileges=true` and the narrow
  capability lists on every non-compute role.
- Keep all signing keys, database credentials, controller state and lifecycle
  credentials absent from compute units.
- Add comments to the generated unit that identify the dedicated-host trust
  boundary and required target-host acceptance test.

## 5. Validate compute privilege and startup isolation

- Extend `scripts/validate-production-unit.sh` to require the complete trusted
  compute contract and reject any of its broad settings on another role.
- Require the compute role to retain read-only application/configuration
  mounts and reject control-plane secret mounts.
- Extend `require_production_compute_isolation` in
  `scripts/cluster-entrypoint.sh` to verify host cgroup namespace identity and
  create/remove a bounded probe cgroup before starting `slurmd`.
- Keep non-remapped root, unified cgroup v2 and writable hierarchy checks.
- Do not weaken the local rootless simulation profile.

## 6. Emit application HTTPS readiness checks

- Render proxy and cache health commands that call their `/health/ready`
  endpoints over HTTPS with the public service CA and stable instance host.
- Keep health commands bounded, silent and independent of bearer tokens.
- Require the application readiness command in production-unit validation.
- Retain the generic application process check only as an internal fallback,
  not as the generated production health contract.

## 7. Update the production JWT and operational documentation

- Update `docs/USER_GUIDE.md`, `docs/PRODUCTION.md`, `docs/REVIEW.md`,
  `docs/ARCHITECTURE.md` and `README.md` where relevant.
- Document the exact `preferred_username`, `uid`, `gid`, nested `id`, and
  `groups`/`gids` claim contract for `use_jwt_client_ids_only`.
- State that the JWT issuer is cluster-root trust and that the shared `finsim`
  identity is not per-workflow tenant isolation.
- Add cloud-inventory examples, distinguish dynamic external scale-out from
  static lifecycle-managed nodes, and document safe scale-in for each.
- Replace stale loopback-HTTP application text with the routable HTTPS
  contract.
- Document compute-host equivalence to trusted root `slurmd`, dedicated-host
  requirements, firewall/admission controls and the mandatory EL10 host gate.
- Update `docs/SCRIPTS.md` for every changed option, variable, input, generated
  path and cross-script contract.

## 8. Run static and offline verification

- Run `bash -n` over every tracked shell/lifecycle script.
- Run all Python script tests without leaving bytecode in the repository.
- Render production configuration with and without lifecycle integration.
- Render and validate every production role.
- Run the installed Podman systemd generator against the rendered compute
  unit and inspect the resulting `podman run` arguments.
- Render Compose and check documentation links and stale production claims.

## 9. Run application and live integration verification

- Run locked restore, Release build and managed FinSim contract tests inside
  `finsim-dev`.
- Rebuild the images only if runtime/image-layer inputs changed.
- If rebuilt, publish a new immutable local application release rather than
  overwriting the active release.
- Run the existing live acceptance suite against the local cluster, proving
  REST-only workflow submission, result correctness, UID 64032, accounting,
  placement and root-job rejection.
- Do not claim the production compute profile is target-validated until the
  documented disposable rootful EL10 host test has passed.

## 10. Record focused history

- Commit cloud inventory and configuration validation together.
- Commit trusted compute units, startup checks and readiness behavior together.
- Commit updated production/operator documentation separately.
- Leave the FinSim worktree clean and report every automated gate plus the
  external rootful-host gate explicitly.
