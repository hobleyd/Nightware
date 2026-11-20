# Phase 11 — JSON batching (`$batch`)

> **Status (2026-11-21): done.** Decisions: [ADR 0006](../adr/0006-graph-parity.md) D9.
> Conformance runs against Microsoft 365 captures (2026-11-20).

**Goal:** a Graph client can send up to 20 requests in one `POST /$batch`, and
each one behaves exactly as it would on its own.

Depends on: nothing new. No backend work.

## Operations
| id | Request |
|---|---|
| `batch.post` | `POST /v1.0/$batch` with independent GETs, including one that fails (its own 404 inside a 200 batch) |
| `batch.post-depends-on` | `POST /v1.0/$batch` with a `dependsOn` chain whose first request fails (the dependant gets 424) |

## Built here
- `Api/Graph/Batch/`:
  - `BatchRequestReader`: whole-batch validation (shape, ids, `dependsOn`,
    cycles, 20 max).
  - `BatchDispatcher`: at most 4 at once, `dependsOn` ordering, 424.
  - `BatchSubRequest`: one in-process request per entry, with its own DI scope.
  - `GraphBatchPipeline`: a branch of the app built by `UseNightware`, running
    the same steps from routing on (the steps are shared with the main
    pipeline), wrapped in `GraphExceptionMiddleware`.
- Refused per sub-request: a nested `$batch`; an absolute `url`; an
  `authtoken` (self-authorising) URL; a body without `Content-Type`.
- `RequestBudget` gives `$batch` the transfer budget. Each sub-request still
  has its own budget.
- Tests: `Api.Tests/Graph/Batch`, 30 tests (reader units; end to end over the
  real pipeline).

## Acceptance
- Batched `GET /me/messages?$top=5` returns the same body as the direct call.
- One 404 sub-request does not fail its siblings.
- An unauthenticated batch is 401 as a whole.

## Capture findings (Microsoft 365, 2026-11-20)
- Graph returns `responses` in completion order. The façade uses request
  order, and conformance sorts both before comparing.
- A 424 has **no** `headers`. Its error is `FailedDependency`, "Request failed
  as dependent request failed". The façade now says the same.
- Sub-responses carry Exchange's headers (`Cache-Control`, `OData-Version`,
  `x-ms-resource-unit`). The façade sends its own, and conformance ignores
  them.
- An unknown folder id is `400 ErrorInvalidIdMalformed` on Exchange and
  `404` on the façade (a JMAP id has no format to break). The batch fixtures
  compare only that the request failed alone.
- `/me?$select=displayName,mail` has no `id` on Graph. The façade keeps it,
  as odata-subset §2 allows.
