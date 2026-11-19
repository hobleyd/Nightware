# Nightware — Claude Code Guide

Read `docs/architecture.md` before changing anything; it holds the layer rules,
id/delta conventions and per-feature file ownership. Phase plans live in
`docs/phases/`.

- .NET (see `global.json`), Clean Architecture: Domain ← Application ← Adapters.* ← Api.
- Every class gets tests. `dotnet test` must be green before committing.
- Implement only Graph surface listed in `docs/contract/endpoints.json`.
- Commit on `main` (or merge feature worktrees into `main`); no remote pushes
  unless asked.
- Every commit bumps `<Version>` in `Directory.Build.props` (shown beside
  "Nightware console"). `.githooks/pre-commit` adds one to the last number; a
  clone needs `git config core.hooksPath .githooks` once (check it is set before
  committing). For a `feat` commit, bump the middle number (and zero the last) by
  hand in the same commit; the hook then leaves it. Amends keep the bump they have.
