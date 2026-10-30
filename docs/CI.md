# Development and TeamCity

Run CI on a Podman-capable Linux agent. Do not run nested containers and do not
mount a privileged Podman socket into `finsim-dev`.

```bash
scripts/ci.sh verify
scripts/ci.sh integration
```

`verify` checks shell/Python code and builds the two image targets. The dev
target supplies the SDK and tests; the runtime target is the only production
artifact. `integration` prepares an immutable application release, starts the
same runtime digest in the local topology, submits a REST workflow and runs the
acceptance/security/accounting suite.

Promotion should publish and sign only `finsim-runtime`. `finsim-dev` may be
published to an internal development registry but never to a production
deployment path. Application releases have their own checksum manifest and
promotion lifecycle under `/finsim-app/releases`.

Required gates are locked restore, warnings-as-errors build, managed contract
tests, shell/Python tests, Compose rendering, role startup, REST submission,
non-root payload proof, accounting proof, credential mount isolation and
runtime-image content checks.
