# CI

Everything runs through the `Makefile`; `.github/workflows/ci.yml` calls the same targets, so
`make ci` locally reproduces the CI build job.

| Target | Does |
|---|---|
| `make build` | restore (NuGet audit) + build in Release; warnings are errors |
| `make test` | every test project, no coverage (the integration suite skips itself unless `NIGHTWARE_INTEGRATION=1`) |
| `make test-architecture` | the ArchUnitNET layer rules |
| `make coverage` | unit, endpoint, adapter and conformance tests with coverage, then the gate |
| `make vulnerable` | fails on any known-vulnerable NuGet package, direct or transitive |
| `make ci` | `build vulnerable test-architecture coverage` |
| `make image` | the API container image |
| `make env` | create `deploy/.env` if missing and generate its `NIGHTWARE_DELTA_LINK_KEY` if absent (never replaces one); `stack` and `api` run it |
| `make stack` / `make api` | start the `deploy/` backends / (re)build and start only the façade |
| `make integration` | stack + bootstrap + façade + `smoke.sh` + `NIGHTWARE_INTEGRATION=1` live tests |
| `make load` | k6 against the running stack (`tests/Nightware.Load/README.md`) |

## Workflow

- **build** (every push/PR): build, vulnerable packages, architecture tests, coverage + gate; test
  results and Cobertura files are uploaded as the `test-results` artifact and the coverage table is
  written to the job summary.
- **image** (after build): builds the image and checks it starts and answers `/healthz/live`. It
  passes a random `DeltaLinks__SigningKey`, because the image runs as Production and refuses to start
  without one.
- **integration** (manual: *Run workflow* with `integration: true`): brings up `deploy/` on the
  runner, bootstraps, starts the façade, runs `smoke.sh` and the live suite, dumps compose logs on
  failure, and tears the ephemeral stack down.

## Coverage

Coverage is collected with `Microsoft.Testing.Extensions.CodeCoverage`, because the tests run on
Microsoft.Testing.Platform (`global.json`) and `coverlet.collector` is a VSTest data collector that
collects nothing there. `scripts/ci/coverage.settings.xml` restricts it to the six production
assemblies and excludes generated code (regex, `LoggerMessage`, JSON source generation).
`dotnet test` merges the per-project files into `artifacts/test-results/merged/*.cobertura.xml`.

The architecture tests run in their own pass (`scripts/ci/coverage.slnf` leaves them out): static
instrumentation rewrites the assemblies on disk, and ArchUnitNET then sees a dependency on the
coverage tracker in `Nightware.Domain`. Add any new test project to that solution filter.

**Gate: 90 % line, 80 % branch** (override with `make coverage COVERAGE_LINE=… COVERAGE_BRANCH=…`).
Measured on 2026-11-15 after the phase 5A merge: **96.9 % line, 91.0 % branch** (Api 97.0/94.3,
DAV 97.5/88.5, Directory 97.1/92.0, JMAP 95.6/89.5, Application 98.4/99.0, Domain 90.9/100).
Why these numbers:

- The codebase's rule is that every class is tested, and today's figure shows it is followed; a gate
  far below it (say 70 %) would let a whole untested adapter feature through unnoticed.
- Five feature phases are merging in parallel, each adding a few thousand lines with tests but also
  with defensive branches (backend error mappings, cancellation paths) that are legitimately hard to
  reach. Six to seven points of headroom on lines absorbs that without letting a feature land
  untested; branch coverage is gated lower because exhaustive error-path branches dominate it.
- Ratchet it up when the feature phases have merged and the figure settles.

## Dependency vulnerabilities

`scripts/ci/check-vulnerable.sh` parses `dotnet list package --vulnerable --include-transitive
--format json` (the command itself exits 0 when it finds vulnerabilities). NuGet audit also runs on
restore; with warnings-as-errors a newly published advisory can fail an unrelated build — fix it by
bumping the package (or, temporarily and with a dated comment, `NuGetAuditSuppress`).
