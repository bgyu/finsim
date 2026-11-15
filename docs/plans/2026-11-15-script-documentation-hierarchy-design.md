# Script documentation hierarchy design

## Objective

Turn `docs/SCRIPTS.md` into a high-level navigation page and create detailed
per-script documentation under `docs/scripts/`. The first implementation pass
documents the production and operational core, with tags identifying whether a
script belongs to production, simulation, testing, build or shared workflows.

## Structure

- `docs/SCRIPTS.md`: lifecycle overview, tag index, high-level script matrix,
  and links to detailed pages.
- `docs/scripts/README.md`: documentation conventions and the operational-core
  call graph.
- One Markdown page per operational-core script. Each page contains purpose,
  tags, caller/callee chain, workflow diagram, inputs/outputs, function map,
  validation/failure behavior, security and idempotency notes, and related
  source links.

## Initial detailed script set

Production and operational core:

- `build-images.sh`, `build-rpms.sh`, `install-runtime.sh`,
  `configure-runtime.sh`;
- `prepare-storage.sh`, `prepare-config.sh`;
- `validate-production-config.sh`, `render-production-unit.sh`,
  `validate-production-unit.sh`;
- `cluster-entrypoint.sh`, `cluster-healthcheck.sh`;
- `cluster-up.sh`, `cluster-down.sh`.

Other scripts remain listed with tags and one-line descriptions until a later
documentation pass.

## Tag vocabulary

Use tags in each detailed page: `production`, `simulation`, `testing`,
`build`, `runtime`, `shared`, `security`, `storage`, `configuration`,
`deployment`, or `lifecycle`. A script may have multiple tags.

## Accuracy requirements

- Derive function maps and call chains from the current source, not from old
  documentation.
- Distinguish direct callers from scripts invoked through Podman, Compose,
  Quadlet or systemd.
- Explain generated files, mounted files and persistent state separately.
- Explain failure-closed validation, privilege transitions, ownership and
  idempotency where applicable.
- Do not imply that documentation scripts are executable or that source
  scripts should normally be invoked directly when an orchestrator owns them.

## Validation

- Check all local Markdown links.
- Run `git diff --check`.
- Run the repository problems check on changed Markdown files.
- Compare the documented function names and role maps against the source.
