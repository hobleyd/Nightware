# Nightware — Claude Code Guide

Read `docs/architecture.md` before changing anything; it holds the layer rules,
id/delta conventions and per-feature file ownership. Phase plans live in
`docs/phases/`.

- .NET (see `global.json`), Clean Architecture: Domain ← Application ← Adapters.* ← Api.
- Every class gets tests. `dotnet test` must be green before committing.
- Implement only Graph surface listed in `docs/contract/endpoints.json`.
- Commit on `main` (or merge feature worktrees into `main`); no remote pushes
  unless asked.
