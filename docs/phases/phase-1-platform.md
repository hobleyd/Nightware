# Phase 1 — Platform stack and façade skeleton

**Goal:** a runnable stack where all services trust one OIDC issuer, and a
façade skeleton every feature phase builds on.

Depends on: nothing. Blocks: phases 2–5 (skeleton), integration tests (stack).

## 1.1 Façade skeleton (built first, before feature phases fan out)
- Solution, `Directory.Build.props` (nullable, warnings-as-errors, analyzers),
  `Directory.Packages.props` (central versions).
- Domain shared kernel: `Result<T>`, `Error`, `ErrorKind`, `DeltaToken`,
  `Page<T>`, strongly-typed ids.
- Application: `IUserContext`, `IMailboxResolver`, `ItemQuery`/`FilterNode`
  query model, every feature's port interfaces (signatures agreed up front so
  phases can work in parallel).
- Api: JWT bearer auth against the configured issuer, `/v1.0` and `/beta`
  route groups, `/me` ↔ `/users/{id}` mailbox resolution, OData-subset parser,
  `Prefer` header parsing, Graph error envelope, `$select` projection,
  `@odata.context/nextLink/deltaLink` builders, 501 fallback for unknown
  routes, `/.well-known/nightware` discovery document, health checks.
- JMAP core client (session discovery, method-call batching, blob
  upload/download, error mapping) and DAV core client (PROPFIND/REPORT,
  multistatus parsing, ETags) — shared by feature phases.
- Architecture tests enforcing layer rules.

## 1.2 Deployment stack — `deploy/`
- `docker-compose.yml`: Caddy (TLS, routing), Keycloak (+ Postgres),
  Stalwart, Nextcloud (+ Postgres, Redis) with Calendar, Contacts, Tasks,
  `calendar_resource_management`, `user_oidc`; the Nightware Api image.
- Keycloak realm export: `nightware` realm, `nightmail` public client (PKCE,
  loopback + custom-scheme redirects), `nightware-api` audience, test users.
- Stalwart configured with Keycloak as OIDC directory (bearer auth on JMAP),
  test domain, DKIM.
- Nextcloud bootstrap script (`occ`): apps, `user_oidc` provider with bearer
  validation, test rooms.
- iMIP plumbing: Nextcloud sends mail via Stalwart SMTP; Stalwart routes
  inbound iTIP to recipients (documented and smoke-tested).
- `deploy/README.md`: bring-up, test users, how to get a token.
- `deploy/smoke.sh`: gets a token for a test user and calls Stalwart JMAP
  session, Nextcloud CalDAV PROPFIND and Nightware `/v1.0/me`.

## Acceptance
- `dotnet build` and `dotnet test` green; architecture tests in place.
- `docker compose up` brings the stack healthy; `smoke.sh` passes.
