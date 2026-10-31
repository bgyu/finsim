# Exhaustive script reference design

Date: 2026-11-01

## Objective

Create one authoritative `docs/SCRIPTS.md` manual covering every tracked shell
or Python script under `scripts/` and `deploy/lifecycle/`. The reference must
explain not only what each file does, but also why it exists, where it runs,
how to invoke it safely, and how it depends on other repository scripts.

## Chosen approach

Use one hand-maintained exhaustive manual rather than splitting the material by
audience or generating shallow reference text from comments. A single document
makes host, build-container, runtime-container, CI and Slurm-hook call paths
searchable together. Hand-written rationale is necessary because source-level
extraction cannot explain security boundaries, destructive effects,
idempotency or operational intent.

## Document structure

The manual will contain:

1. a task-oriented “which script should I run?” table;
2. execution-context and lifecycle overviews;
3. a complete inventory and direct-dependency matrix;
4. detailed entries for every public, internal, library, lifecycle and test
   script;
5. function catalogs for `build-common.sh`, `runtime-env.sh` and
   `lifecycle-common.sh`;
6. a role/function map for the large runtime entrypoint;
7. shared environment-variable and generated-file contracts;
8. maintenance rules for keeping the reference synchronized with source.

Each script entry will identify its purpose, rationale, caller, syntax,
examples, inputs, outputs, state changes, direct script calls, sourced files,
referenced functions and important shared variables. It will also record
external dependencies, idempotency, permissions, failure behavior and safety
notes where applicable.

## Accuracy strategy

The inventory will be derived from tracked files, not memory. Direct calls,
`source` relationships, copied/installed helpers, shared variables and library
functions will be checked against source. A final repository check will compare
the tracked script list with literal paths present in the manual, validate all
local Markdown links and run the existing static tests.

The manual is explanatory; the script source remains authoritative. New or
renamed scripts and changes to cross-script calls, sourced functions, role
variables or generated paths must update `docs/SCRIPTS.md` in the same change.

## Navigation

Add links from `README.md` and `docs/USER_GUIDE.md`. Existing focused guides
remain in place and link to the exhaustive manual rather than duplicating its
dependency detail.

## Acceptance criteria

- Every tracked file under `scripts/` and `deploy/lifecycle/` is documented.
- Every direct repository-script dependency is listed.
- Sourced libraries list the exact functions used by their consumers.
- Operator-facing scripts include copyable examples.
- Internal scripts clearly state that they are invoked by images, containers,
  Slurm or CI rather than by normal operators.
- Local links resolve, shell/Python checks pass, and the worktree is clean after
  committing the completed manual.
