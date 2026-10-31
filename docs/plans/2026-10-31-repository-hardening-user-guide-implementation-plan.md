# Repository hardening and operator-guide implementation plan

Date: 2026-10-31
Design: `2026-10-31-repository-hardening-user-guide-design.md`

## 1. Harden generated local inputs

- Add `scripts/lib/runtime-env.sh` with a strict, non-evaluating parser for
  `.runtime/cluster.env`.
- Use it from startup, validation and acceptance scripts.
- Validate values before writing the file in `bootstrap-dev-secrets.sh`.
- Extend Python/shell tests with malformed, duplicate and injected values.

## 2. Verify immutable application releases

- Extend `validate-deployment.sh` to validate manifest schema, release ID,
  complete file set and every SHA-256 digest.
- Reject symlinks, writable content and unexpected ownership.
- Add positive and tamper-negative tests that do not expose credentials.

## 3. Complete production configuration

- Validate all production controller/accounting hostnames.
- Add opt-in lifecycle settings with bounded timeouts and rates.
- Render lifecycle client configuration separately from `slurm.conf`.
- Keep manual dynamic-node registration as the default cloud-neutral mode.

## 4. Produce deployable role definitions

- Replace the unresolved generic Quadlet with role-specific templates and a
  strict renderer.
- Emit only role-required mounts, environment, capabilities and commands.
- Add a production-bundle validator and example non-secret inventory.
- Test every supported role render and reject invalid input.

## 5. Correct and expand documentation

- Add `docs/USER_GUIDE.md` for build, development, CI, production deployment,
  REST job operation, scaling, upgrades, rollback and troubleshooting.
- Update README and focused documents to match actual commands and boundaries.
- Add primary upstream references next to the production requirements they
  support.
- Explicitly label external production dependencies and launch gates.

## 6. Verification and history

- Run shell/Python/static/Compose and production-render checks.
- Rebuild affected image targets.
- Republish a fresh application release.
- Start the local cluster, submit a REST workflow and run acceptance tests.
- Commit implementation and documentation separately and leave a clean tree.
