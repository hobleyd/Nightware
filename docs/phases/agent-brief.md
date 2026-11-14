# Brief for phase agents

You are building one phase of Nightware in parallel with other agents. This file
holds the rules every phase follows; your task prompt names your phase,
worktree and operations.

## Read first (in full)
1. `docs/architecture.md` and **`docs/adr/0001-skeleton-decisions.md`** (the ADR
   overrides everything else; its addenda §29–43 and the ownership table matter).
2. `docs/api-plumbing.md` — how to add an endpoint, parse queries, project,
   map errors and register conformance arrangements.
3. `docs/contract/endpoints.md` (+ `endpoints.json` entries for your phase),
   `docs/contract/odata-subset.md`, `docs/contract/discovery-and-oauth.md`.
   **The contract is your scope.** Where `docs/phases/phase-*.md` disagrees with
   the contract (it over-scopes in places), the contract wins.
4. The README of every adapter you use: `src/Nightware.Adapters.{Jmap,Dav,Directory}/README.md`.
5. `deploy/README.md` and `deploy/recordings/` for real backend behaviour.
6. The NightMail client (`~/Source/nightmail/lib`, read-only) at the call sites
   cited in `endpoints.json` — what it sends and what it reads is the spec.

## Worktree
Work only in your own worktree, created from current `main`:
`git -C ~/Source/Nightware worktree add ~/Source/Nightware-wt/<branch> -b <branch>`.
Commit there; never push. Before you finish, `git merge main` and re-run the
build and tests, because other phases merge while you work.

## Ownership
- Edit only the feature folders your prompt lists (in every project, plus the
  matching test folders) and the registration hooks that already exist for them
  (`Add<Feature>UseCases`, `Add<Backend><Feature>`, `Map<Feature>Endpoints`).
- **Frozen:** `Domain/Kernel/*`, every port interface in `Application`, the
  adapter `Core/` folders, `Api/Graph/*`, `Api/Composition/*`, `Program.cs`,
  `Directory.Packages.props`, other phases' folders. If you believe a frozen
  file must change, do not change it: work around it and explain precisely in
  your report (file, change, reason).
- Domain models for your feature are yours to extend additively.

## Engineering standard
- Clean Architecture and SOLID: endpoint → use case (Application, one sealed
  class per use case, one `ExecuteAsync`) → port → adapter. Endpoints never
  touch adapters; use cases never see JSON, HTTP or backend protocols.
- `Result`/`Error` everywhere; adapters translate failures at their boundary;
  the Api maps errors only through `GraphErrorMapper`.
- Match the surrounding code: naming, comment density (explain *why*, not
  *what*), file layout, analyzer cleanliness. `dotnet build` must have zero
  warnings (warnings are errors).
- No new packages without a strong reason (say so in the report).

## Tests — every class is tested
- **Application:** unit tests for each use case with NSubstitute port fakes
  (unconfigured fakes return a failing `Result` by design — arrange everything).
- **Adapters:** WireMock.Net tests for every backend request shape and every
  error path; prefer real responses from `deploy/recordings/` as fixtures.
- **Api:** endpoint tests through `TestHost` for every route, both `/me` and
  `/users/{user}` forms, success and error cases.
- **Conformance:** register an arrangement for **every** operation id of your
  phase in `tests/Nightware.Conformance.Tests` (a class in your own file); each
  must pass, none may remain skipped. If a fixture is wrong about Graph
  behaviour, fix the fixture and justify it in the commit message.
- **Integration (live):** add opt-in tests under
  `tests/Nightware.Integration.Tests/<Feature>/` (skipped unless
  `NIGHTWARE_INTEGRATION=1`). The deploy stack may be running
  (`docker compose -p nightware ps` from `deploy/`); you may start it with
  `docker compose -p nightware up -d` (volumes are already bootstrapped). Never
  run `down -v`, never delete volumes, never reconfigure Keycloak/Stalwart/
  Nextcloud — other agents share the stack. Use the test users alice and bob
  (`deploy/README.md`), and use unique subjects/UIDs so parallel runs don't collide.

## Finish
`dotnet build` clean, `dotnet test` green, logical commits on your branch whose
messages end with the attribution trailer given in your prompt. Report back:
what you built, test counts by project, conformance operations passing, live
verification done, any frozen-file changes you needed, and open issues.
