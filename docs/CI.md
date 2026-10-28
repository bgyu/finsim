# Development and TeamCity pipeline

The supported development artifact is the `finsim-dev` target. It inherits the
same UBI, Slurm, ASP.NET runtime, fixed identities, and runtime libraries as
production, then adds the .NET SDK, git, jq, tests, source, and local simulation
assets. It runs as the non-login `finsim` UID 64032 by default. Never promote
this target to a production registry/repository.

The CI agent host owns Podman. There is no Docker/Podman socket in the dev
container and no nested container engine. This keeps the build's container
privileges from becoming host-root-equivalent.

## TeamCity steps

1. Checkout with clean-worktree enforcement.
2. Run `scripts/ci.sh verify` on a dedicated rootless Podman agent.
3. Run `scripts/ci.sh integration` on an isolated integration agent.
4. Generate SBOM and vulnerability/license results for the exact image digest.
5. Sign and publish `slurm-runtime` and `finsim-app` digests only after all
   gates pass; never publish `finsim-sim` or `finsim-dev` to production paths.
6. Attach source pins, `/var/packages/BUILD-MANIFEST`, SRPMs, git commit,
   rendered build metadata, and test results as release provenance.

The integration step brings up the single-host topology, runs the acceptance
test, and tears it down in an EXIT trap. Set `KEEP_CLUSTER=1` only on a secured
diagnostic agent when an operator needs post-failure inspection.

`BUILD_TAG` may contain only letters, digits, `_`, `.`, and `-`; release
promotion should use a content digest even if CI also applies a human-readable
tag. TeamCity secrets must be password parameters or external secret-provider
projections, masked from logs and unavailable to untrusted pull-request builds.
The local integration bootstrap generates disposable credentials and does not
need production secrets.

Developers can build and enter the tool image without nesting Podman:

```bash
podman build --target finsim-dev -t localhost/finsim-dev:local -f Containerfile .
podman run --rm -it localhost/finsim-dev:local
```

For active editing, mount the checkout using an agent-approved UID mapping and
SELinux label rather than copying credentials into it. The convenient local
cluster is still started from the host checkout with `scripts/cluster-up.sh`.
