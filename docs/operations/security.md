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

## The root secret (`Hosting/SigningKeyValidator.cs`)

`DeltaLinks:SigningKey` signs delta/next links and keys (HKDF → AES-256-GCM, one key per purpose)
the seal on self-authorising upload and download URLs, which carry the caller's **access token**.
A known key means forged links and readable tokens, so Production refuses to start when it is
missing, shorter than 32 bytes or a development value from this repository, and the compose file
has no default for `NIGHTWARE_DELTA_LINK_KEY`. Generation and rotation:
[deployment.md](deployment.md#the-façades-root-secret-nightware_delta_link_key).

## Identity attributes are admin-only (Keycloak)

The user's identity is the token's `email` claim: it is the façade's `/me` mailbox and the Stalwart
account (`claimUsername: email`). If users could edit their own email in Keycloak's account console,
one could take an address that exists only in Stalwart (an offboarded user, a shared mailbox) and
open that mailbox. `deploy/keycloak/configure.sh` therefore merges `permissions.edit: ["admin"]`
into the realm user profile for `email` and `username` (read-modify-write, nothing else changes),
keeps `editUsernameAllowed` off and the `UPDATE_EMAIL` required action disabled. Address changes are
an admin task. The façade does not yet require `email_verified` (see `docs/known-issues.md`).

## Per-user rate limits (`Throttling/`)

Token bucket (300 burst, 10/s) chained with a concurrency limit (32), partitioned by `sub`; requests
without a valid token are partitioned by client address. A rejection is `429 TooManyRequests` with
an integer `Retry-After`, which NightMail honours. Discovery, health and CORS preflights are exempt.
Readiness (`/healthz`), which is anonymous and fans out to every backend, is instead cached for 10 s
and single-flight (`Hosting/ReadinessCache.cs`), so a caller looping on it cannot multiply backend
load.
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

## Response headers (`Nightware.Hosting/Http/SecurityHeadersMiddleware.cs`)

Every response: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; sandbox` (an HTML attachment
opened from `$value` cannot run script), `Referrer-Policy: no-referrer`, and `Cache-Control: no-store`
unless the endpoint set its own. HSTS on HTTPS requests. No `Server` header.

## Logging and telemetry redaction (`Nightware.Hosting/Observability/LogRedaction.cs`)

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
   `UseCookies = false`, and `Nightware.Hosting/Http/CookielessHttpClientsFilter` turns cookies off on the primary
   handler of **every** factory client in the process, so a client added later cannot regress it.
2. Never create a backend `HttpClient` outside `IHttpClientFactory`, and never give one a
   `CookieContainer`. The façade is stateless towards its backends: every call carries its own
   credential.
3. `tests/Nightware.Api.Tests/Hosting/HttpClientCookiePolicyTests.cs` enumerates every named and
   typed client in the real composition and fails if any primary handler keeps cookies.

## Admin console (`src/Nightware.Admin`, ADR 0002)

The console at `/console` manages entities, users and their accounts in all
three backends. It is a separate host from the Graph façade and shares no
credentials with it.

- **Sign-in.** OIDC authorization code + PKCE against its own confidential
  client `nightware-admin`. The ID token is validated and dropped
  (`SaveTokens=false`); the browser holds only the encrypted, `HttpOnly`,
  `Secure`, `SameSite=Lax` session cookie `__Secure-nightware.console`. Sessions
  end after 20 minutes idle and 8 hours absolute. A sign-in whose `acr` is not in
  `Admin:AcceptedAcrValues` (default `mfa`) is refused; Production refuses to
  start with the list empty.
- **Authorization** is decided in the Application use cases
  (`AdminAuthorization`), not the pages, so a forged form post gets the same
  answer. The admin's scope (global, or the entities whose `admins` group they
  are in) is re-read from Keycloak at most a minute old, never trusted from the
  token. Entity admins act only on users wholly inside their entities and never
  on another administrator; they cannot create entities, change domains or grant
  global rights. Nobody can offboard or demote themselves.
- **Destructive actions.** Deleting a user's mail or Nextcloud data needs a
  disabled user, a sign-in less than 5 minutes old with an accepted `acr` (the
  console sends the admin back to Keycloak with `max_age=0` otherwise), and the
  retyped address; it refuses an account it cannot prove belongs to the user.
  Global admins only, unless `ADMIN_ENTITY_ADMINS_MAY_DELETE_DATA=true`.
- **Pages** carry no script: `default-src 'none'; style-src 'self'; img-src
  'self'; form-action 'self' <keycloak origin>; frame-ancestors 'none';
  base-uri 'none'`, `Cache-Control: no-store`. Every POST needs an antiforgery
  token. A generated temporary password appears once, on a no-store response,
  and is never logged.
- **Backend credentials** are the console's own and least-privilege where the
  backend allows it: the Keycloak service account of `nightware-admin`
  (`manage-users`, organizations, `query-clients`, read-only `view-realm`; never
  `view-clients`), a Stalwart account with a custom role and an API key usable
  only from the console's address, and a Nextcloud local administrator
  (Nextcloud has no narrower role that can create users).
- **Off-limits accounts.** Keycloak administrators and service accounts,
  privileged Stalwart accounts and Nextcloud administrators are never linked,
  reset, offboarded or deleted through the console; reserved system addresses are
  never assigned; an alias can't be another person's sign-in address.
- **Offboarding** blocks every sign-in to the mailbox as well (Stalwart app
  passwords and API keys outlive the Keycloak account otherwise), and an
  offboarded administrator loses the console within a minute.
- **Exposure.** Caddy admits `ADMIN_ALLOWED_CIDRS` (default: anywhere). Narrow
  it to a management network or VPN where you have one. The console shares an
  origin with Nextcloud, Stalwart's UI and Keycloak; an XSS in one of them could
  act in the console (`docs/known-issues.md`, fix: a host of its own).
- **Cloudflare tokens** (per entity, for publishing DNS records). Global
  admins only enter them, in a password field that is never rendered back. The
  console first checks the token with Cloudflare and refuses one it does not
  accept or that reaches none of the entity's domains. It then seals the token
  with ASP.NET Data Protection (authenticated encryption with the `admin-keys`
  key ring, purpose bound to the entity, so a sealed token copied to another
  entity does not open). Only the sealed form is stored, as the organization
  attribute `nightware_dns_token:cloudflare`, so Keycloak's database, admin
  events and exports hold ciphertext. Reading it back needs both Keycloak and
  the key volume, and the key ring is not encrypted at rest, so protect the
  volume like a secret. The token never reaches logs, audit events or error
  messages. Ask entity owners for a token from Cloudflare's "Edit zone DNS"
  template, limited to their zones. Publishing only adds and corrects records.
  It never removes one the domain does not need, except a second SPF, DMARC or
  DKIM record at a name where there must be one. It writes records as plain
  DNS (never proxied), and does not add a first CAA record.
- **Audit.** Every action logs one `admin.audit` event naming the human actor;
  Keycloak's admin events record the console's service account for Keycloak
  writes.

## Dependencies

CI fails on any known-vulnerable NuGet package, direct or transitive (`make vulnerable`), and
restore runs NuGet audit. Container base images are pinned by tag; see [upgrades.md](upgrades.md).
