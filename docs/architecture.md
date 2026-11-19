# Nightware architecture

Nightware is a self-hosted, Microsoft Graph–compatible backend for the
NightMail client. It is a **stateless façade**: it speaks the subset of Graph
v1.0/beta that NightMail calls, and translates each request onto open-source
servers that own the data.

```
NightMail ──HTTPS + OIDC bearer──▶ Nightware.Api (Graph façade)
                                     │
            ┌────────────────────────┼─────────────────────────┐
            ▼ JMAP (RFC 8620/8621)   ▼ CalDAV/CardDAV/WebDAV   ▼ OIDC / admin API
        Stalwart Mail Server      Nextcloud (Sabre/DAV)     Keycloak
        mail, SMTP, spam, Sieve   calendar, contacts,       identity, directory
                                  tasks, files, rooms
```

> **Read [`docs/adr/0001-skeleton-decisions.md`](adr/0001-skeleton-decisions.md) too** — it
> fixes ids, delta, auth and parallelisation decisions and overrides this file
> where they differ.

## Non-negotiable rules

1. **Implement only what NightMail calls.** The endpoint inventory in
   `docs/contract/` is the scope. Anything else returns a Graph-shaped
   `501 notImplemented` (or `400 invalidRequest` for unsupported OData).
2. **No data of record in the façade.** Delta tokens and IDs are
   self-describing (encoded backend state), so the façade needs no database.
   A cache, if ever added, must be disposable.
3. **Graph fidelity is defined by golden fixtures**, not by reading docs.
   `tests/Nightware.Conformance.Tests/Fixtures` holds request/response pairs;
   a response shape change that breaks a fixture is a bug.
4. **Every class has tests.** Unit tests for domain/application, HTTP-level
   tests (WireMock.Net) for adapters, `WebApplicationFactory` tests for
   endpoints, opt-in integration tests against the real stack.

## Layers (Clean Architecture)

Dependencies point inward only. Enforced by `tests/Nightware.Architecture.Tests`
(ArchUnitNET).

| Project | Depends on | Owns |
|---|---|---|
| `Nightware.Domain` | nothing | Entities, value objects, `Result<T>`/`Error`. No I/O, no framework types. |
| `Nightware.Application` | Domain | Ports (interfaces) the adapters implement, use cases, the provider-neutral query model (`ItemQuery`, `FilterNode`), `IUserContext`. |
| `Nightware.Adapters.Jmap` | Application | Stalwart JMAP client + mail/mailbox-settings/spam port implementations. |
| `Nightware.Adapters.Dav` | Application | Nextcloud CalDAV/CardDAV/WebDAV/OCS client + calendar, contacts, tasks, rooms, file-share port implementations. |
| `Nightware.Adapters.Directory` | Application | Keycloak admin/userinfo + Nextcloud avatar: user lookup, photos. |
| `Nightware.Adapters.Cloudflare` | Application | Cloudflare v4 API for the admin console: publishing an entity's DNS records with the entity's own token; and public DNS lookups (Cloudflare's DNS-over-HTTPS resolver) for the server's public addresses. |
| `Nightware.Adapters.DnsHosts` | Application | The admin console's other DNS hosts, one `IDnsHosting` each: DigitalOcean (v2), Gandi (LiveDNS v5), Hetzner (Cloud API zones), Amazon Route 53 (REST/XML, SigV4), Azure DNS (Resource Manager, client-credentials sign-in) and Google Cloud DNS (v1, service-account JWT sign-in), each with the entity's own credential; Azure and Google access tokens are cached in memory until shortly before they expire. Record-set hosts give each value a stable id (name, type, value hash). |
| `Nightware.Hosting` | nothing in the solution (ASP.NET Core only) | Hosting code shared by the hosts (ADR 0002 §D2): security headers with a configurable CSP, trusted-proxy (`ForwardedHeaders`) registration, the cookie-free `HttpClient` filter, log redaction. |
| `Nightware.Admin` | Application, all Adapters (composition root only), Hosting | The admin console (ADR 0002): Razor Pages, OIDC + MFA sign-in, the `Administration/` use cases. Never references `Nightware.Api`, and the Api never references the `Administration/` feature. |
| `Nightware.Api` | Application, all Adapters (composition root only), Hosting | ASP.NET Core host, JWT auth, Graph routes, OData-subset parsing, `Prefer` header handling, Graph JSON DTOs + mappers, Graph error envelope. |

The Api references adapters **only** in `Composition/` (DI wiring). Endpoint
code depends on Application use cases, never on adapters.

## Conventions

### Results and errors
- Use cases and ports return `Result<T>` (Domain). Exceptions are for bugs and
  for adapter-internal transport failures, which adapters translate into
  `Error` at their boundary.
- `ErrorKind` → HTTP status + Graph `error.code` is mapped in exactly one
  place: `Nightware.Api/Graph/GraphErrorMapper.cs`.

### Identifiers
- Each adapter emits **opaque, URL-safe, stable** ids for its objects and is
  the only code that can decode them. JMAP ids are used as-is; DAV hrefs are
  base64url-encoded by `DavId`. The Api never parses an id.
- Message ids are stable across moves (Graph's "immutable id" behaviour).

### Delta
- Two layers of token. The adapter owns backend state: its `DeltaToken` is
  `base64url(json{v, ...})` that only it decodes. The Api owns Graph query
  options: the link it emits wraps the adapter token together with the frozen
  `$select`/`$top` (Graph freezes them into delta links and the client relies on
  that), so follow-up calls with only `$deltatoken`/`$skiptoken` are projected
  exactly like the first call. See ADR 0001 §6–8.

### OData
- The Api parses `$select/$filter/$orderby/$top/$skip/$search/$expand/$count`
  into `ItemQuery` (Application). Only the grammar in
  `docs/contract/odata-subset.md` is accepted; anything else is
  `400 invalidRequest` naming the unsupported construct.
- `$select` is applied by the Api when serialising (projection of the DTO), so
  adapters may return more than asked; adapters *may* use `Select` to fetch
  less.

### Per-request identity
- `IUserContext` (Application) exposes the authenticated user's id, primary
  SMTP address and the raw bearer token. Adapters pass the bearer token
  through to Stalwart/Nextcloud (both trust the same Keycloak issuer). A
  service-account fallback is per-adapter configuration, never the default.
- `/users/{id|upn}/...` routes resolve the target mailbox through
  `IMailboxResolver`; delegated (shared-mailbox) access is authorised by the
  backend, not re-implemented in the façade.

### Use cases
- One class per use case, `sealed`, named as a verb phrase
  (`ListMessagesInFolder`), with one public `ExecuteAsync`. No mediator
  library. Registered by each feature's `AddXxxUseCases()`.

### Features and file ownership
Code is organised by feature folder in every project, so phases can be built
in parallel without touching each other's files:

`Mail/`, `Calendar/`, `Contacts/`, `People/`, `Directory/`, `Tasks/`,
`MailboxSettings/`, `Files/`, `Security/` (threat submission), `Rooms/`.

Each feature exposes, in its own files:
- `Application/<Feature>/<Feature>ServiceCollectionExtensions.cs` → `Add<Feature>UseCases()`
- `Adapters.*/<Feature>/...ServiceCollectionExtensions.cs` → `Add<Backend><Feature>()`
- `Api/Features/<Feature>/<Feature>Endpoints.cs` → `Map<Feature>Endpoints()`

`Program.cs` and `Composition/` already call every feature's registration
method; phase work fills the bodies, never edits the shared wiring (except to
add configuration options).

## Testing stack
- xUnit v3, Shouldly, NSubstitute.
- WireMock.Net for adapter HTTP tests (record real JMAP/DAV responses as
  fixtures under each adapter test project's `Fixtures/`).
- `Microsoft.AspNetCore.Mvc.Testing` for endpoint and conformance tests.
- `Nightware.Integration.Tests` runs against `deploy/docker-compose.yml`; it is
  skipped unless `NIGHTWARE_INTEGRATION=1`.
- ArchUnitNET for layer rules; Testcontainers for the integration suite.

## Backends
- **Stalwart** — mail store, JMAP, SMTP submission, spam filter, Sieve,
  `VacationResponse`.
- **Nextcloud** — Calendar (CalDAV, iTIP/iMIP via Sabre), Contacts (CardDAV),
  Tasks (CalDAV VTODO), `calendar_resource_management` (rooms), Files
  (WebDAV), OCS Share API, avatars.
- **Keycloak** — OIDC issuer trusted by Nightware, Stalwart and Nextcloud.
