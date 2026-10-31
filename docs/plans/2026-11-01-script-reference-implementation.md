# Exhaustive script reference implementation plan

Date: 2026-11-01

1. Inventory every tracked file below `scripts/` and `deploy/lifecycle/`,
   classifying it as operator command, build/runtime internal, library,
   lifecycle hook, wrapper or test.
2. Extract direct script calls, sourced files, copied/installed helpers,
   function definitions, shared environment variables and generated paths from
   source.
3. Write `docs/SCRIPTS.md` with task lookup, lifecycle/context maps, dependency
   matrix, per-script entries, library function catalogs, entrypoint role map,
   variable/file contracts and maintenance guidance.
4. Add navigation links from the repository landing page and user guide.
5. Verify that every tracked script path appears in the manual, local Markdown
   links resolve, static tests pass and the diff is clean.
6. Commit the completed reference separately from the approved design and plan.
