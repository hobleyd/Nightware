# Phase 6 — Hardening and operations

**Goal:** production-worthy behaviour under load and failure.

Depends on: phases 2–5 substantially complete.

- Rate limiting per user (ASP.NET Core rate limiter) with Graph-style
  `429` + `Retry-After` and `TooManyRequests` error code.
- Backend failure mapping: timeouts → `503` + `Retry-After`; auth failures from
  backends → `401` with `InvalidAuthenticationToken` so NightMail re-auths.
- Resilience (`Microsoft.Extensions.Http.Resilience`): retries for idempotent
  backend calls only, circuit breakers per backend.
- `$batch` (JSON batching) — only if Phase 0 inventory shows the client needs it.
- OpenTelemetry traces/metrics/logs; correlation via `request-id` /
  `client-request-id` headers (Graph behaviour).
- Security: strict audience/issuer validation, CORS off, request size limits,
  header/redaction rules for logs, dependency scanning in CI.
- CI (GitHub Actions or local `make ci`): build, test, coverage gate, container
  image build, integration suite against compose.
- Ops docs: backup/restore for Stalwart, Nextcloud, Keycloak; upgrade policy.
- Optional: Graph `subscriptions` (webhooks) for mobile push.

## Acceptance
- Load test (k6) of a NightMail-shaped poll cycle sustains target users with
  p95 under budget; chaos test (kill a backend) yields Graph-shaped errors and
  recovery without restarts.
