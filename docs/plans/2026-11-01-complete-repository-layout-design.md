# Complete Repository Layout Design

## Goal

Remove the obsolete empty `infra/slurm` directory and make the repository
layout section in `docs/USER_GUIDE.md` a complete, maintainable map of the
tracked source tree.

## Scope

The guide will document every tracked root-level file and every functional
directory. It will identify the important contents of `build/`, `config/`,
`deploy/`, `docs/`, `scripts/`, `src/`, and `tests/`, while linking to the
exhaustive script manual instead of duplicating script internals.

Generated `.runtime/` data will be shown separately because it is operationally
important but intentionally ignored by Git. Local agent metadata, credentials,
compiled outputs, Python caches, and other untracked workstation artifacts are
not repository layout and will not be presented as source files.

## Presentation

Use a compact tracked-tree overview followed by purpose tables. This keeps the
section navigable while still explaining build inputs, deployment templates,
application projects, tests, generated state, and documentation ownership.

The obsolete `infra/slurm` directory is empty, untracked, and unreferenced. It
will be removed with `rmdir`, which fails safely if it is no longer empty.

## Alternatives Considered

A single exhaustive flat file list is complete but difficult to understand and
quickly becomes noisy. The previous short table is readable but omits important
root files, application boundaries, tests, and deployment assets. A structured
tree plus focused tables provides both coverage and context.

## Validation

- Confirm `infra/slurm` is empty before removal.
- Verify that all tracked root paths are represented in the guide.
- Check relative Markdown links resolve to tracked files.
- Run `git diff --check`.
- Preserve unrelated working-tree changes, especially `docs/SCRIPTS.md`.
