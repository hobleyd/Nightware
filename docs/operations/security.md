# Security hardening

What the façade enforces, where, and why. Code references are under `src/Nightware.Api/`.

## Authentication

- Bearer JWTs from the configured Keycloak realm only: issuer (`Auth:Authority`/`ValidIssuers`),
  audience (`nightware-api`), signature and lifetime are all validated (`Auth/`). A missing or bad
  token is Graph's `401 InvalidAuthenticationToken`; "not allowed" is always 403, never 401, because
  NightMail treats 401 as an expired token.
- The façade never forwards the user's token to a backend as-is (default `TokenExchange`): Stalwart
  and Nextcloud each receive a token exchanged for their own audience, and reject an un-exchanged
  NightMail token (asserted by `deploy/smoke.sh`).

## Per-user rate limits (`Throttling/`)

Token bucket (300 burst, 10/s) chained with a concurrency limit (32), partitioned by `sub`; requests
without a valid token are partitioned by client address. A rejection is `429 TooManyRequests` with
an integer `Retry-After`, which NightMail honours. Discovery, health and CORS preflights are exempt.
Limits are per façade instance; with N instances behind a load balancer a user can reach N× the
configured rate (sticky sessions or a smaller per-instance budget if that matters).

## Request budgets (`Hosting/RequestBudget.cs`)

Each request gets 25 s (2 min for transfers). A read that overruns is answered
`503 serviceNotAvailable` + `Retry-After: 5`; a write that overruns is answered `504 gatewayTimeout`
without `Retry-After`, because NightMail retries *any* method on 503 and a write may already have
taken effect (a duplicate sendMail is worse than an error the user sees).

## Request size and Kestrel limits

`Limits:MaxRequestBodyBytes` (≈ 67 MB, from Stalwart's 50 MB `maxSizeUpload` as base64) is enforced
on every request: a declared oversize `Content-Length` is refused before the body is read, and a
chunked body is cut off by Kestrel; both answer Graph's `413 requestEntityTooLarge`. Header size,
header count, request line and keep-alive limits come from `Kestrel:Limits`.

## CORS (`Hosting/GraphCors.cs`)

Off by default. `Cors:AllowedOrigins` lists exact origins of the NightMail web build; the policy
applies to discovery and the Graph routes only (not `/healthz`), allows the verbs and headers the
client sends, no credentials mode (bearer only), and exposes `Retry-After`, `request-id`,
`client-request-id`, `Location`, `ETag` and `Preference-Applied` so the web client can read them.
Error responses (401, 429, 500, budget 503) carry the CORS headers too. The token endpoint's CORS is
configured on the Keycloak `nightmail` client (web origins), not here.

## Proxies and forwarded headers

Only `ForwardedHeaders:KnownProxies/KnownNetworks` may set `X-Forwarded-*` (default: loopback). An
untrusted sender cannot choose the client address (rate-limit partition, logs) or the scheme/host.

## Response headers (`Hosting/SecurityHeadersMiddleware.cs`)

Every response: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; sandbox` (an HTML attachment
opened from `$value` cannot run script), `Referrer-Policy: no-referrer`, and `Cache-Control: no-store`
unless the endpoint set its own. HSTS on HTTPS requests. No `Server` header.

## Logging and telemetry redaction (`Observability/LogRedaction.cs`)

Never logged or exported: `Authorization` headers, tokens (user, exchanged, service-account),
client secrets, cookies, request and response bodies, query strings.

- No HTTP request/response logging middleware is registered; adapters log status codes and backend
  error codes only; IdentityModel PII logging stays off, so token-validation failures never print
  the token.
- ASP.NET Core's request-line log (`Microsoft.AspNetCore.Hosting.Diagnostics`, which prints the full
  URL with its query) is pinned to `Warning` in code, so turning logging up to `Trace` cannot
  re-enable it. Traces record the route template and a query-redacted URL instead.
- Unhandled exceptions are logged with the route template, not the path.
- **Paths are not redacted** (the framework's log scope carries `RequestPath`; spans carry
  `url.path`). A URL that authorises itself — a To Do upload session's `uploadUrl` — must carry its
  credential in the **query string** (as Graph's `?authtoken=` does), never in a path segment.

`tests/Nightware.Api.Tests/Observability/LogRedactionTests.cs` drives valid, garbage, expired and
tampered tokens, cookies, bodies, malformed JSON, an unhandled exception, a token exchange with a
client secret and an outbound call with an `Authorization` header through the real pipeline with
every category at `Trace` and OTLP export on, and asserts none of those secrets appears in any log
record (message, state, scope, exception) or span attribute.

## Backend HTTP clients keep no cookies

`IHttpClientFactory` pools primary handlers and shares them across requests — and so across
**users**. A cookie container on such a handler replays one user's backend session (Nextcloud sets
a session cookie in answer to a bearer request) on the next user's call: this happened (CSRF 401s,
and a path to another user's data). Rules:

1. Every backend client is cookie-free. The adapters build their primary handlers with
   `UseCookies = false`, and `Hosting/CookielessHttpClientsFilter` turns cookies off on the primary
   handler of **every** factory client in the process, so a client added later cannot regress it.
2. Never create a backend `HttpClient` outside `IHttpClientFactory`, and never give one a
   `CookieContainer`. The façade is stateless towards its backends: every call carries its own
   credential.
3. `tests/Nightware.Api.Tests/Hosting/HttpClientCookiePolicyTests.cs` enumerates every named and
   typed client in the real composition and fails if any primary handler keeps cookies.

## Dependencies

CI fails on any known-vulnerable NuGet package, direct or transitive (`make vulnerable`), and
restore runs NuGet audit. Container base images are pinned by tag; see [upgrades.md](upgrades.md).
