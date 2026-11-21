# Load tests (k6)

Two scripts, run with a pinned `grafana/k6` image inside the `deploy/` stack's Docker network
(`run.sh` mounts Caddy's local CA and reads secrets from `deploy/.env`). No local k6 install needed.

| Script | What it models | Thresholds |
|---|---|---|
| `poll-cycle.js` | One NightMail device per virtual user, at its steady-state cycle: inbox delta (following `nextLink`, keeping the `deltaLink`, re-bootstrapping on 400/404/410 like `mail_poller_cubit.dart`), the folder list, a 28-day `calendarView` with `Prefer: outlook.timezone="UTC"`, and To Do lists — every `POLL_INTERVAL` seconds (30, the client default), with ±10 % drift. | p95 < 300 ms per call, < 1 % failures, zero 429s, every 429/503 has an integer `Retry-After` |
| `harness.js` | Proves the harness on routes that need no feature phase: `/healthz/live`, `/healthz`, discovery and `GET /v1.0/me`. | p95 < 50 ms (live, discovery), < 300 ms (`/me`, readiness) |

## Running

```bash
make stack api                                   # backends + façade (deploy/README.md)
tests/Nightware.Load/run.sh harness.js           # ~30 s sanity run
VUS=50 DURATION=10m tests/Nightware.Load/run.sh poll-cycle.js
make load LOAD_SCRIPT=poll-cycle.js              # same, via make
```

| Variable | Default | Meaning |
|---|---|---|
| `VUS` | 10 (harness 5) | concurrent devices |
| `DURATION` | `5m` (harness `30s`) | run length |
| `POLL_INTERVAL` | `30` | seconds between cycles per device |
| `LOAD_USERS` | `alice,bob` | accounts the devices sign in as, round-robin |
| `ALLOW_501` | `true` | count `501 notImplemented` as expected (a feature not merged yet) |
| `K6_IMAGE` | `grafana/k6:1.4.2` | pinned runner |

A JSON summary is written to `tests/Nightware.Load/results/` (git-ignored).

**Users and the rate limiter.** The dev realm has two accounts, so many VUs share one `sub` and
therefore one rate-limit partition. Thirty-second cycles of four calls stay far inside the default
budget (10 requests/s sustained per user), but a compressed `POLL_INTERVAL` with many VUs will be
answered `429` by design — the `throttled` threshold then fails. To load the backends rather than the
limiter, start the façade with `NIGHTWARE_RATE_LIMITING=false make api`, or create more test users.

**Tokens** come from the `nightware-dev` password grant and are refreshed per VU before they expire,
so runs longer than the realm's access-token lifespan work.

## Targets

- **Façade overhead:** `/healthz/live` and discovery touch no backend; their p95 is the pipeline's own
  cost (auth, correlation, limiter, budget, serialisation) and should stay under 50 ms.
- **Poll cycle:** p95 < 300 ms per call end to end on the dev stack, which includes Stalwart/Nextcloud
  time and the cached token exchange. Every page must also finish well inside NightMail's 30 s receive
  timeout; the façade's own 25 s budget answers 503 + `Retry-After` before that.
- **Throttling:** zero 429s for the modelled load; any 429/503 must carry integer `Retry-After`.

## Results so far (2026-11-15, Apple Silicon, Docker Desktop)

- `harness.js`, 5 VUs, 30 s: all thresholds pass — p95 live 5.3 ms, discovery 3.4 ms, `/me` 14.0 ms,
  readiness 108 ms; 0 % failures. Unpaced (no `sleep`), the same single-user run is answered 429 after
  the 300-request burst, as designed.
- `poll-cycle.js`, 20 VUs (alice/bob), 90 s, 30 s cycle, after the phase 2 and 5A merges: inbox
  delta p95 55 ms and folder list p95 7.5 ms — both mail calls pass every check, zero 429s. Calendar
  and To Do routes (phases 4 and 5B) were not merged yet and answered `400 BadRequest` (unmapped
  route, ADR 0001 §31), so their status checks and the overall failure-rate threshold fail by
  construction. Re-run once those phases are merged.
- `poll-cycle.js`, 20 VUs (10 devices per account), 90 s, after phases 2, 3, 4, 5A and 5B merged,
  on a host with load average ~20 from other suites running in parallel: folders p95 166 ms
  (pass), inbox delta p95 486 ms, task lists p95 3.3 s, calendarView p95 8.3 s (Nextcloud CPU-bound
  on that host; re-measure on an idle machine before reading these as product numbers). 4 of 80
  delta calls were answered 429: not the façade's limiter but Stalwart's per-account
  `maxConcurrentRequests` (4), reached because ten devices shared one mailbox — see the runbook.

## Results (2026-11-21, Apple Silicon, colima)

`poll-cycle.js`, 20 VUs (10 per account), 3 min, 30 s cycle, façade started cold:

| | calendarView median | calendarView worst after the first cycle | first cycle |
|---|---|---|---|
| before | 164 ms | about 1.3 s | up to 4.3 s |
| after | 99 ms | 151 ms | 1.6–2.1 s |

Two changes made the difference:
- Nextcloud's Apache keeps 20–40 idle workers (`deploy/nextcloud/mpm_prefork.conf`) instead of the
  image's 5–10, which it grew by 1, 2, 4… per second. A burst of more than 10 requests used to wait
  up to 4 s for workers.
- calendarView remembers the default calendar per caller (`DavCalendarScopes.DefaultAsync`), so a warm
  call is one REPORT instead of a PROPFIND and a REPORT.

The k6 thresholds on calendarView and task lists still fail. Their p90/p95 come from the first cycle,
where all 20 devices hit a freshly started façade in the same second (`docs/known-issues.md`). Inbox
delta p95 219 ms, folders 10 ms, 0 failures, 0 throttled.

