# Phase 11 — JSON batching (`$batch`)

> **Status (2026-11-20): planned.** Decisions: [ADR 0006](../adr/0006-graph-parity.md) D9.

**Goal:** a Graph client can send up to 20 requests in one `POST /$batch`, and
each one behaves exactly as it would on its own.

Depends on: nothing new. No backend work.

## Operations
| id | Request |
|---|---|
| `batch.post` | `POST /v1.0/$batch` with independent GETs, including one that fails (its own 404 inside a 200 batch) |
| `batch.post-depends-on` | `POST /v1.0/$batch` with a `dependsOn` chain whose first request fails (the dependant gets 424) |

## Built here
- `Api/Features/Batch/`: request/response DTOs, `BatchDispatcher`. The
  dispatcher builds a sub-`HttpContext` per request and invokes the app's
  `RequestDelegate`, with bounded concurrency and `dependsOn` ordering.
- Refusals, each per sub-request: a nested `$batch`; an absolute or
  version-crossing `url`; a self-authorising route; more than 20 requests (the
  whole batch).
- Tests: dispatcher unit tests, endpoint tests (auth per sub-request, `Prefer`
  honoured, continuation links absolute), and conformance for both ids.

## Acceptance
- Batched `GET /me/messages?$top=5` returns the same body as the direct call.
- One 404 sub-request does not fail its siblings.
- An unauthenticated batch is 401 as a whole.
