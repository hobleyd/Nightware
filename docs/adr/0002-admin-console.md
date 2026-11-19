# ADR 0002 — Admin console: entities, users and downstream account linking

Status: accepted (2026-11-17). Scope: a second host, `Nightware.Admin`, that lets
an administrator manage *entities* (organisations owning mail domains) and their
*users* without signing in to Keycloak, Stalwart or Nextcloud, linking accounts
that already exist downstream and creating the ones that do not. ADR 0001 stays
in force; this ADR adds to it and names every place it bends a rule.

## Context

Today a person exists in three systems with three different keys:

| System | Key | How it is set today |
|---|---|---|
| Keycloak (`nightware` realm) | user `id`; identity is the `email` claim; `username` is `preferred_username` | realm import (alice, bob), admin console |
| Stalwart | account `emailAddress` = `email` claim (`claimUsername: email`); accounts are `name` (local part) + `domainId` | `deploy/stalwart/configure.sh` pre-creates `STALWART_ACCOUNTS`; otherwise first sign-in, and **mail bounces until then** (README "Known limitations" 6) |
| Nextcloud | uid = `preferred_username` (`--unique-uid=0 --mapping-uid=preferred_username`) | first bearer request (`--bearer-provisioning=1`) |

There is one domain (`MAIL_DOMAIN`), one set of admin credentials per backend in
`.env`, and no notion of an organisation. The façade has no database (ADR 0001
rule 2 and `architecture.md`), and the Graph host implements only
`docs/contract/endpoints.json`.

## Decisions

### D1. Keycloak is the system of record; the console stores nothing

- **Entity = Keycloak Organization** (`/admin/realms/nightware/organizations`;
  supported since 26.0, image is 26.8.2). Its `domains[]` are the entity's mail
  domains; Keycloak enforces that a domain belongs to at most one organization
  in the realm, which is exactly the uniqueness the console needs. `alias` is
  the stable handle shown in URLs; `attributes` are not used for anything
  security-relevant.
- **User ∈ Entity = organization membership**, always `UNMANAGED` (the user is a
  realm user first; deleting the organization never deletes people). The
  Keycloak invitation flow (`invite-user`) is **not** used: for a user without an
  account it routes through realm self-registration, which the realm disables
  (`registrationAllowed: false`) and which would let the invitee choose their
  own identity.
- **Admin scope lives in realm roles and organization groups, never in user
  attributes** (the user profile makes attributes user-viewable; roles and group
  membership are admin-only by construction):
  - global admin: realm role `nightware-global-admin`;
  - entity admin: membership of the organization group `admins` of that
    organization (organization groups, Keycloak 26.6+). The group carries the
    realm role `nightware-entity-admin` (role inheritance through organization
    groups, 26.7+), so the claim `realm_access.roles` says *that* someone is an
    entity admin and the group membership says *of which entity*.
  - The console treats token claims as a hint only. `IAdminScopeSource`
    re-resolves the actor's scope from Keycloak (`GET
    /organizations/members/{id}/organizations`, then `…/members/{id}/groups`)
    on each request, cached for 60 s per `sub`, so a revoked role stops working
    within a minute, not at the next sign-in.
- **Links are computed, never stored.** A user's state in each backend is
  derived on read by matching keys (D3). Every write is an idempotent "ensure it
  exists and matches" step; running it twice is a no-op. The only caches are
  disposable `IMemoryCache` entries (scope, backend session) that a restart can
  lose without consequence.
- **No store is needed.** The one thing that tempted one — remembering what the
  admin *decided* about a conflict — is deliberately not remembered: a conflict
  is resolved by changing the backend (rename the Keycloak username, pick a
  different address, remove the stray Nextcloud user) until the keys match, so
  the resolution is visible in the systems of record themselves.

### D2. A separate host, same Domain and Application

- `src/Nightware.Admin` is a second ASP.NET Core host. It references
  `Nightware.Application` and the three adapter projects (composition root only,
  like the Api) and **never** `Nightware.Api`. The Api never references the
  admin feature.
- Admin ports and use cases live in **`Nightware.Application/Administration/`**
  (one feature folder, `AddAdministrationUseCases()`), not in a new application
  project. Reasons: they use the same kernel (`Result`, `Error`, `Principal`,
  `MailboxRef`), the same conventions (one sealed class per use case,
  `ExecuteAsync`, `Result<T>`), and will share value types with
  `Directory/` (`DirectoryUser` aliases, user-profile attribute names). A
  second application project would duplicate the kernel's DI plumbing for no
  isolation that ArchUnit cannot give: `AddApplication()` does **not** call
  `AddAdministrationUseCases()`, and a new rule forbids `Nightware.Api` from
  depending on `Nightware.Application.Administration`, `Nightware.Admin` from
  depending on `Nightware.Api`, and anything but the two composition roots from
  depending on adapters.
- Each backend keeps its adapter: `Adapters.Directory/Administration/`
  (Keycloak organizations, users, role mappings, action e-mails),
  `Adapters.Jmap/Administration/` (Stalwart `x:` management objects),
  `Adapters.Dav/Administration/` (Nextcloud OCS provisioning). The management
  clients are **separate typed `HttpClient`s with their own options and
  credentials**; they never go through `IBackendCredentials`, which is the
  per-user credential path of the Graph façade.
- Hosting code the two hosts share (security headers with a parameterised CSP,
  forwarded-headers options, the cookie-free `HttpClient` filter, log
  redaction) moves from `Nightware.Api/Hosting|Observability` to a new
  `src/Nightware.Hosting` library that depends on nothing else in the solution.
  Copying security-critical middleware into a second host would be worse than
  touching frozen files once; the move is a pure relocation (ADR 0001 §24
  freeze is lifted for exactly those files, in one commit, by the coordinator).
- **Separate Keycloak client and audience.** `nightware-admin` is a confidential
  client with the standard flow (PKCE S256 enforced), redirect
  `${PUBLIC_URL}/console/signin-oidc`, and its own service account. Graph
  tokens (`aud: nightware-api`) are never accepted by the console (it accepts no
  bearer tokens at all, D5), and the console's ID tokens (`aud:
  nightware-admin`) are useless at the Graph host. `nightware-api` is **not**
  widened: its service account keeps `view-users`, `query-users`,
  `manage-users` (ADR 0001 §17, §29).

### D3. Link keys and link states

The console matches by the keys each backend actually uses; nothing else.

| Backend | Match key | Source |
|---|---|---|
| Keycloak | `email` (exact, case-insensitive) → then `username` | `IKeycloakAdminClient.FindUserByEmailAsync/FindUserByUsernameAsync` |
| Stalwart | `emailAddress` = Keycloak `email`; `name` = local part, `domainId` = the entity domain's Stalwart id | `x:Account/query` filter by `emailAddress` (verify exact filter name in step 0) |
| Nextcloud | uid = Keycloak `username` (`preferred_username`) | `GET /ocs/v1.php/cloud/users/{uid}` |

Per backend a user is in exactly one `LinkState`:

| State | Meaning | Console action offered |
|---|---|---|
| `Linked` | the account exists and its keys agree with Keycloak | none |
| `Missing` | no account with that key | **Create** |
| `Pending` | will exist on first sign-in (Nextcloud without pre-provisioning; Stalwart never — mail would bounce) | Create now / leave |
| `Conflict` | an account with that key exists but disagrees: Stalwart account is a `Group`/list, or its `emailAddress` differs from the Keycloak `email`; Nextcloud uid exists with a different, non-empty e-mail, or in a backend other than `user_oidc`/`Database`; Keycloak user found by username has a different e-mail; the address's domain is not one of the entity's | **Show both sides; resolve manually** (edit Keycloak e-mail/username, choose another address, or remove the stray account out of band). Never auto-merged. |
| `Disabled` | Keycloak `enabled=false` (Stalwart/Nextcloud states mirror it; Stalwart has no documented account flag) | Reinstate |
| `Error` | the backend could not be asked (unreachable, 5xx, credential rejected) | Retry |

Entity domains have the same shape: Keycloak org domain ↔ Stalwart `Domain`
(`x:Domain/query` by `name`), plus `Dkim` (keys present via `x:DkimSignature/query
{domainId}`) and `Dns` (the record set from `Domain.dnsZoneFile`, server-set).
Stalwart CE has no domain ownership, so a Stalwart domain that another
organization claims in Keycloak is a `Conflict` detected on the Keycloak side.

### D4. Multiple domains, DKIM and DNS

- An entity's domain list is the Keycloak organization's `domains[]`. Adding a
  domain = `PUT /organizations/{id}` with the domain added **and** `x:Domain/set
  create {name, description: "<entity alias>", dkimManagement: Automatic,
  dnsManagement: Manual}` (then `x:Action/set {ReloadSettings}`; whether the
  reload is required for a new domain is verified in step 0). Order: Keycloak
  first (uniqueness), then Stalwart; a Stalwart failure leaves the domain in
  state `Missing` with a Repair button, never rolls back Keycloak.
- The **DNS records view** reads `Domain.dnsZoneFile` (Stalwart's own zone
  text, which `publishRecords` keeps in sync: `mx`, `spf`, `dkim`, `dmarc`,
  `mtaSts`, `tlsRpt`, `autoConfig`, `autoDiscover`, `caa`, `srv`) and renders a
  table (type, name, TTL, value, "copy") plus a "download zone file" link.
  Parsing is in `Adapters.Jmap/Administration/ZoneFileParser` (backend format),
  the result is the Domain type `DnsRecord`. v1 shows the expected records; it
  does not resolve live DNS (v2: DNS-over-HTTPS check per record).
- `MAIL_DOMAIN` stays the bootstrap default domain (Stalwart `defaultDomain`,
  Nextcloud `mail_domain`, the dev users). `deploy/keycloak/configure.sh`
  creates the organization "Nightware" (alias `nightware`) with
  `MAIL_DOMAIN` and makes alice and bob members, so the dev stack is a
  one-entity deployment. The façade source already has no single-domain
  assumption; `MailboxRef` is a full address.
- Addresses an entity admin may assign (primary and aliases) must be in one of
  the entity's domains; the Application policy enforces it (D6).

### D5. Pre-provisioning: Stalwart always, Nextcloud when possible

- **Stalwart accounts are created before first sign-in** (`x:Account/set
  create {@type: User, name, domainId, description, roles: {@type: User}}`), as
  `configure.sh` already does for alice and bob; otherwise mail to the address
  bounces. On first OIDC sign-in Stalwart matches the existing account by the
  `email` claim (`claimUsername: email`), which is the same key, so nothing is
  duplicated. Aliases are `Account.aliases [{name, domainId}]`.
- **Nextcloud is pre-provisioned through `user_oidc`'s own API**
  (`POST /ocs/v2.php/apps/user_oidc/api/v1/user {providerId, userId: <username>,
  displayName, email}`), so the user is created in the `user_oidc` backend with
  the uid first sign-in would have produced, and `--bearer-provisioning=1` then
  finds and updates it instead of creating it. Not the generic
  `POST /ocs/v1.php/cloud/users`: that creates a `Database`-backend user that
  `user_oidc` only adopts via `soft_auto_provision`. If the `user_oidc` endpoint
  is absent (older app) the console reports `Pending` and the user is
  provisioned at first sign-in, which was the behaviour until now and is
  acceptable: calendar/contacts/files have no inbound traffic before the user
  exists. Disable/enable use `PUT /ocs/v1.php/cloud/users/{uid}/disable|enable`.
- Keycloak users are created `enabled: true, emailVerified: true` (the façade
  does not require `email_verified` yet, `docs/known-issues.md`; marking
  admin-entered addresses verified is the intended path), with `requiredActions:
  ["UPDATE_PASSWORD"]` (+ `CONFIGURE_TOTP` for anyone granted an admin role).

### D6. Authorization is in Application use cases, not in the UI

`AdminActor(UserId, IsGlobal, IReadOnlySet<string> EntityIds)` is resolved by
the host (D1) and injected as `IAdminActor`. Every use case calls
`AdminAuthorization` (pure, unit-tested) before touching a port:

- `RequireEntity(actor, entityId)`: global, or `entityId ∈ actor.EntityIds`,
  else `Error.Forbidden`.
- `RequireGlobal(actor)` for: create/delete entity, add/remove entity domains,
  grant/revoke **global** admin, move a user between entities, purge.
- `RequireAddressInEntity(entity, address)`: every primary address and alias
  must be in `entity.Domains`; entity admins and global admins alike.
- `RequireNotSelf(actor, targetUserId)` for revoke-admin and offboard (no
  self-lockout by accident; a global admin can still be removed by another).
- Entity admins may grant/revoke *entity* admin within their own entity, never
  the global role; `GrantEntityAdmin` refuses a target that is not a member of
  that entity.
- Listing is scoped at the port: `ListUsers(entityId)` reads
  `/organizations/{id}/members`, so an entity admin cannot enumerate the realm.

The page models only decide what to *render*; a forged form post for another
entity gets `403` from the use case. Endpoint tests assert that.

### D7. Session model: Razor Pages, OIDC code + PKCE, cookie, no tokens in the browser

- **Razor Pages**, not Blazor Server. The console is forms and tables (a
  three-step wizard is three pages). Razor Pages need no persistent WebSocket
  through Caddy, no circuit state, no SignalR script, so the CSP can be
  `default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self';
  frame-ancestors 'none'; base-uri 'none'` with **no script at all** in v1.
  Antiforgery is built in for every POST. It is the smaller stack and the
  smaller attack surface; Blazor would be reconsidered only if the UI needs
  live updates.
- Sign-in: `Microsoft.AspNetCore.Authentication.OpenIdConnect` (new package)
  with `nightware-admin`, `response_type=code`, PKCE, `max_age=300` (Keycloak
  re-authenticates rather than reusing a NightMail SSO session), `SaveTokens =
  false`: the ID token is validated and dropped, the access token is never
  requested for anything. The browser holds only the encrypted auth cookie
  (`__Secure-nightware.console`, `HttpOnly`, `Secure`, `SameSite=Strict`, path
  `/console`). The console calls backends with **its own** service
  credentials (D8); the human's identity is carried by `IAdminActor`.
- Idle timeout 20 min sliding, absolute 8 h (ticket `IssuedUtc` checked in
  `OnValidatePrincipal`), sign-out also ends the Keycloak session (RP-initiated
  logout). Data-protection keys persist in a volume (`admin-keys`), so a
  rebuild or restart doesn't sign everyone out; sessions still end at the
  idle and absolute limits, and deleting the keys signs everyone out.
- **MFA/step-up.** The realm gets `acr.loa.map = {"pwd": 1, "mfa": 2}` and a
  browser flow with a Conditional-LoA subflow (level 2 → a passkey or OTP,
  whichever the user has; neither → OTP set-up). The
  `nightware-admin` client sets "Minimum ACR value" = `mfa` (26.1+), so
  Keycloak refuses to issue it a token below level 2. The console additionally
  **checks `acr` itself** (`Admin:AcceptedAcrValues`, default `["mfa"]`) and
  rejects a sign-in whose ID token does not carry one of them — the docs ask
  relying parties to do so, and a documented bypass made that advice concrete.
  `Admin:AcceptedAcrValues` may be emptied only outside Production (dev stack
  and integration tests, where test users have no OTP); Production refuses to
  start without it, the way `SigningKeyValidator` refuses a weak delta key.
  Granting any admin role adds `CONFIGURE_TOTP` as a required action, so an
  authenticator app is every admin's baseline. An admin may then add a passkey
  (`/account/passkey`, `kc_action=webauthn-register`); the console ranks it
  first among their credentials, because Keycloak offers the alternative
  matching the user's first-ranked credential, so their sign-in asks for the
  passkey and "Try another way" offers the code.
- Security headers come from the shared `SecurityHeadersMiddleware` with the
  page CSP above, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`,
  HSTS; forwarded headers trusted from `DOCKER_SUBNET` only.
- **Exposure.** Caddy serves the console at `/console/` (`/admin/` is Stalwart's
  web admin at the host root, `caddy/Caddyfile`) with `X-Forwarded-Prefix
  /console`, and restricts it with a `remote_ip` allow-list from
  `ADMIN_ALLOWED_CIDRS` (default: any address — decided 2026-11-17, the console
  is reachable wherever the stack is; narrow it to a management network or VPN
  range if you have one; set it to the
  management network or VPN range). The container joins the stack network with
  no published port. Network restriction is defence in depth, not the control:
  the control is OIDC + MFA + the Application policy.

### D8. Least-privilege service credentials per backend

| Backend | Principal | Permissions | Secret (`init-env.sh`) |
|---|---|---|---|
| Keycloak | service account of `nightware-admin` (`scope=kc-admin-api`) | realm-management `manage-users`, `view-users`, `query-users`, `manage-organizations`, `view-organizations`, `query-organizations` | `NIGHTWARE_ADMIN_CLIENT_SECRET` |
| Stalwart | internal account `nightware-admin@${MAIL_DOMAIN}` with a Password credential and a custom role `nightware-management` | `sysAccount{Get,Query,Create,Update}`, `sysDomain{Get,Query,Create,Update,Destroy}`, `sysDkimSignature{Get,Query,Destroy}`, `sysActionCreate` with `actionReloadSettings` (Stalwart checks each action type separately; a domain create reloads settings so its DKIM keys load), plus the built-in **User** role (Stalwart only lets a principal change accounts whose effective permissions it holds, so managing user accounts requires holding the User permissions; `sysAccountDestroy` is granted for D10's delete-mail action and gated by the Application policy; `sysDomainDestroy` and `sysDkimSignatureDestroy` are granted for removing a domain, which Stalwart refuses with `objectIsLinked` while any account has an address or alias on it; `impersonate` is **not** granted) | `STALWART_MANAGEMENT_PASSWORD` |
| Nextcloud | `nightware-admin` (Database backend, group `admin`) | Nextcloud admin (the `user_oidc` provisioning endpoint and OCS user admin require it; there is no narrower role for provisioning) | `NEXTCLOUD_PROVISIONING_PASSWORD` |

- The Stalwart principal's ability to authenticate with a password while the
  default directory is OIDC is **unverified** (README: "not tested"). Step 0 of
  the phase tests it; if it fails, the console falls back to
  `STALWART_RECOVERY_ADMIN` (full access, `impersonate`) and the README's
  "standing full-access credential" warning extends to the console. An API key
  (`Authorization: Bearer API_…`) is the alternative to a password and is
  tried in the same spike; it cannot be pre-generated by `init-env.sh`, so the
  password variant is preferred for the scripts.
- Nextcloud uses the account password over Basic (chosen by `init-env.sh`, so it
  fits the existing secret flow); an app password (`occ user:auth-tokens:add`)
  is minted by Nextcloud and would have to be written back into `.env` by
  `configure.sh`. Open question OQ4.
- The recovery admin, `KEYCLOAK_ADMIN_*` (master realm) and
  `NEXTCLOUD_ADMIN_*` stay bootstrap-only; the console never reads them.

### D9. New-user credentials: Keycloak sends the link; the console never sets a password

`PUT /users/{id}/execute-actions-email?client_id=nightware-admin&lifespan=…`
with body `["UPDATE_PASSWORD"]` (+ `"CONFIGURE_TOTP"` for admins) sends
Keycloak's own action-token link. This needs realm SMTP, which the realm does
not have today: `configure.sh` sets `smtpServer {host: stalwart, port: 25, from:
noreply@${MAIL_DOMAIN}, auth: false}` — the same trusted-subnet path Nextcloud's
iMIP already uses, which Stalwart accepts for **local recipients only**.

That exposes the real problem: Keycloak sends only to the user's `email`, which
is the *new* mailbox the person cannot read yet. Keycloak cannot send an action
link elsewhere, and the console must not mint Keycloak action tokens. v1
therefore offers two delivery modes in the wizard, neither of which lets an
admin choose a password:

1. **"E-mail the set-password link to the mailbox"** (default): right for
   re-linking (the person already reads this mailbox via another route) and for
   migrations.
2. **"Generate a one-time temporary password"**: the console asks Keycloak to
   set a random 20-character password with `temporary: true` (Keycloak forces
   `UPDATE_PASSWORD` at first sign-in) and shows it **once**, never logs it, and
   records an audit event. This is the standard admin workflow for a mail host
   whose users have no other address; it is "system-generated, single-use,
   forced rotation", not "admin-set". **OQ1** asks the user to confirm this is
   acceptable; the alternative (relay invitations to an external address) needs
   Stalwart outbound relaying from the subnet and is listed there.

### D10. Partial failure, repair, offboarding and destructive options

- A provisioning run is a `ProvisioningPlan` (per backend: `Create`, `Link`,
  `Update`, `Skip`, `Conflict`) that the wizard **shows before committing**.
  `ApplyUserProvisioning` executes Keycloak → organization membership →
  Stalwart → Nextcloud → credential delivery, each step idempotent, each
  result recorded per backend. **No rollback**: a pre-existing linked account
  is never deleted because a later step failed; the user page shows the
  per-backend state and a **Repair** button re-runs the plan for the missing
  parts. A `Conflict` stops before any write to that backend.

- **Offboarding disables; it never deletes.** `OffboardUser` sets Keycloak
  `enabled=false`, ends the user's Keycloak sessions
  (`POST /users/{id}/logout`), and disables the Nextcloud user
  (`PUT …/users/{uid}/disable`). Stalwart is not written: it has no documented
  account flag, and access is already blocked because no token can be issued
  or refreshed for a disabled Keycloak user. Already-issued access tokens (and
  the façade's cached exchanged tokens) remain valid until their `exp`, so
  the block is effective within 15 minutes (`accessTokenLifespan`). Mail keeps
  arriving and is retained; files, calendars, contacts and tasks are retained.
  Entity admins may offboard their own entity's users; nobody may offboard
  themselves. `ReinstateUser` reverses it.

- **Deleting data is a separate, guarded action, never part of offboarding.**
  v1 offers two, each with the same guard rails, each on its own confirmation
  page:
  1. **Delete mail** — `x:Account/set destroy` of the Stalwart account whose
     `emailAddress` equals the Keycloak `email` (needs `sysAccountDestroy`,
     added to the `nightware-management` role; the Application policy, not
     Stalwart, is what gates it). This removes the mailbox and every message,
     the account's identities, Sieve scripts and its aliases; mail to the
     address bounces afterwards (unless the domain has a catch-all, ADR 0005). It does
     **not** touch the Keycloak user (kept, disabled — so the address and
     username stay claimed and the audit trail stays attached to a person) or
     anything in Nextcloud.
  2. **Delete Nextcloud data** — `DELETE /ocs/v1.php/cloud/users/{uid}`
     (files, calendars, contacts, tasks, shares; Nextcloud does not trash a
     deleted user's data). It does **not** touch Keycloak or Stalwart.

  **Deleting the Keycloak identity is not offered in v1.** Freeing the
  username and address would let the same address be re-created for someone
  else and would remove the handle the console uses to show what remains. A
  v2 "Purge" (identity, only after both data deletions) is OQ2.

  Guard rails, enforced in the use case (`AdminAuthorization`) and mirrored by
  the page, for both deletions:
  - **Global admins only** (see OQ2b). The action is irreversible, entity
    admins are the larger and less vetted population, and v1 has no
    per-entity retention policy to justify delegating it. The management
    principal holds `sysAccountDestroy`, so this policy is the only thing
    between an entity admin and someone else's mail.
  - **Target must already be disabled** (Keycloak `enabled=false`); an enabled
    user is refused with `Validation` ("offboard first"). Not self.
  - **Fresh step-up authentication**: the actor's `AuthenticatedAt` (the ID
    token's `auth_time`, kept as a cookie claim) must be within
    `Admin:DestructiveActionMaxAge` (default 5 minutes) and `acr` must be an
    accepted value; otherwise the page redirects to a re-authentication
    (`max_age=0`) that returns to the confirmation. The use case checks the same
    facts from `AdminActor`, so a forged POST cannot skip it.
  - **Typed confirmation**: the admin retypes the user's primary address; the
    use case compares it (case-insensitive, trimmed) with the Keycloak `email`;
    mismatch is `Validation`.
  - **No deletion on `Conflict`**: if the downstream account does not match the
    Keycloak keys (D3) the use case refuses — it will not destroy an account it
    cannot prove is this person's.
  - **Idempotent**: a downstream account that is already absent is a successful
    no-op, audited as "already absent".
  - **Audited** as `admin.audit` events `user.mail.deleted` /
    `user.groupware.deleted` with actor, target id and address, downstream id,
    mailbox size at the time (`IMailAccounts.GetUsageAsync`), outcome and
    correlation id. The console also writes the admin-only Keycloak attributes
    `nightware_mail_deleted_at` / `nightware_groupware_deleted_at` (ISO-8601,
    declared in the user profile), so the user page can say "mail deleted on …"
    instead of merely "Missing" — a marker in the system of record, not a store
    in the console.
  - **Repair never recreates** a downstream account for a disabled user.
    `ReinstateUser` on a user whose mail was deleted shows the plan with
    `Create` for Stalwart ("new, empty mailbox") and asks before applying.
  - Whether Stalwart reclaims blob storage synchronously on destroy is
    **unverified**; the page says "space is reclaimed by the server shortly
    after".

### D11. Audit without a database

- Keycloak admin events are enabled by the realm import (`adminEventsEnabled`,
  `adminEventsDetailsEnabled`, `eventsExpiration` 90 days), so every write the
  console makes through Keycloak is recorded by Keycloak — attributed to the
  `nightware-admin` service account.
- The console attributes the human: `IAuditLog` writes one structured
  `admin.audit` log event per use case with actor `sub`, action, target ids
  (user id, entity alias, address), per-backend outcome and the request's
  correlation id, through the shared redaction (no tokens, cookies, bodies or
  passwords; addresses are identifiers and are logged). Stalwart and Nextcloud
  writes are therefore auditable from the console log alone. v2 may surface
  Keycloak admin events in the console (`view-events`).

## Consequences

- One new host image (`nightware-admin`), one new library (`Nightware.Hosting`),
  one new package (`Microsoft.AspNetCore.Authentication.OpenIdConnect`), three
  new secrets, a new Keycloak client, two realm roles, one realm flow, SMTP on
  the realm. No database.
- `AddDirectoryAdapter` is split so the admin host can register Keycloak core
  clients bound to `nightware-admin` without `IBackendCredentials`.
- Frozen files touched once by the coordinator: `Api/Hosting/*` and
  `Api/Observability/LogRedaction*` (moved), `Composition/` (no behaviour
  change), `Directory.Packages.props`, `.slnx`, `LayerTests.cs`.
- Entity admins depend on Keycloak organization groups (26.6+) and role
  inheritance through them (26.7+); the pinned image is 26.8.2. A downgrade
  below 26.7 breaks entity-admin resolution.
- `docs/known-issues.md` "Stalwart cannot look up recipients in OIDC" is closed
  for console-created users.

## Verified / unverified

| Claim | Status |
|---|---|
| Keycloak Organizations REST surface used above; org groups; `manage-organizations` role; FGAP V2 org scopes `view`/`manage` only; "Minimum ACR value"; admin-events API; `kc-admin-api` scope | verified (keycloak.org docs, REST index, javadoc, repo) |
| Organization invitations use self-registration for unknown users | verified (server admin guide) |
| `PUT /users/{id}/execute-actions-email` query `client_id`, `lifespan`, `redirect_uri`, JSON array body | from the admin REST reference and client libraries; re-check against the live stack in step 0 |
| Stalwart `Account`/`Domain`/`DkimSignature`/`Role` fields and `sys*` permissions; `dnsZoneFile`; API keys as `Bearer API_…` | verified (stalw.art schema reference) |
| Stalwart internal password/API-key account authenticates while `directoryId` is OIDC | **unverified** (step 0 spike; fallback D8) |
| Stalwart account enable/disable flag | none documented; offboarding does not rely on it |
| `x:Domain/set` with `dkimManagement: Automatic` generates keys; reload needed | **unverified** (step 0) |
| `x:Domain/set` destroy is refused with `objectIsLinked` (naming each `linkedObjects` entry) while the domain has DKIM keys or any account has an address or alias on it; destroying the keys in the same request first succeeds; after a reload SMTP no longer treats the domain as local | verified (Stalwart 1.1.0, throwaway container, 2026-11-18) |
| Nextcloud OCS provisioning endpoints and headers; `user_oidc` pre-provisioning API; `soft_auto_provision`; `occ user:auth-tokens:add` | verified (Nextcloud admin manual, user_oidc README) |
| OCS `GET users/{uid}` `backend` value for `user_oidc` users; how to read the provider id | **unverified** (step 0) |
| Caddy `remote_ip {$ADMIN_ALLOWED_CIDRS}` placeholder expansion | **unverified** (step 9) |
| Organization `PUT` without an attribute key deletes that attribute (SPF/DMARC reset, domain removal) | **unverified** (adapter tested against a stub only) |
| Organization attribute values hold 255 characters (SPF/DMARC records are capped there) | **unverified**; the cap is also one TXT string's limit |
| Cloudflare: a token from the "Edit zone DNS" template finds its zone with `GET zones?name=` and accepts the record bodies the console sends | **unverified** (WireMock only) |

## Decisions taken (2026-11-17)

- **OQ1** — accepted: the wizard offers the system-generated, shown-once,
  forced-change temporary password (D9 mode 2) alongside the e-mail link.
- **OQ2b** — opt-in: `Admin:EntityAdminsMayDeleteData` (default `false`) lets
  entity admins delete data of their own entity's disabled users, with the same
  guards. Global admins always may.
- **OQ5** — accepted: entity admins add and remove admins of their own entity
  (never themselves, never the global role).
- **Exposure** — reachable from anywhere, protected by OIDC + MFA + the
  Application policy; `ADMIN_ALLOWED_CIDRS` defaults to `0.0.0.0/0 ::/0` and
  stays as a knob.

## Implementation notes (2026-11-17)

Where the build refined the decisions above:

- **Target-user scope (D6).** An entity admin may act on an existing user only
  when every entity the user belongs to is one the admin administers, and never
  on a global admin or an admin of another entity
  (`AdminAuthorization.RequireMayManageUser`, applied by `UserAccess` to every
  per-user use case and by the planner before linking an existing Keycloak
  user). Without it, issuing a temporary password to a global admin who is a
  member of the entity would hand the entity admin global rights. Users outside
  the scope answer NotFound to entity admins, not Forbidden.
- **Session cookie (D7)** is `SameSite=Lax`, not Strict: the session must ride
  on the top-level redirect back from Keycloak. Every state-changing request is
  a POST with an antiforgery token (antiforgery cookie `SameSite=Strict`).
- **Stalwart domain fields (D4).** `dkimManagement`/`dnsManagement` are typed
  objects (`{"@type":"Automatic"}`), per the schema reference.
- **Nextcloud local accounts (D3).** A uid held by a `Database`-backend user is
  a Conflict: with `--unique-uid=0` and no `soft_auto_provision`, Keycloak
  sign-in would not use it. `NextcloudProvisioning:IdentityProviderBackends`
  can widen this deliberately.
- **Nextcloud pre-provisioning (D5)** answers 200 and is idempotent
  (`getOrCreate`); its `providerId` is user_oidc's numeric provider id, looked up
  from the identifier `keycloak` when not configured numerically.
- **MFA switch.** `ADMIN_REQUIRED_ACR` (default `mfa`) feeds
  `Admin:AcceptedAcrValues`; an empty value turns the requirement off and is
  refused in Production.
- **Stalwart principal (D8), verified live: an API key, not a password.**
  Stalwart refuses credentials on internal accounts while Keycloak is the
  directory ("Cannot set credentials for accounts in an external directory").
  `stalwart/configure.sh` creates `nightware-admin@MAIL_DOMAIN` with a custom
  role extending the built-in `User` role (which carries `authenticate`), mints
  it an API key usable only from the console's address, records the key and its
  id in `.env`, and on re-minting destroys the account's other keys. The
  recovery-admin fallback (`STALWART_MANAGEMENT_LOGIN`/`_PASSWORD`) remains.
- **Keycloak service-account roles (D8)** also include `query-clients` (to find
  `realm-management` for the administrator check, with the client id taken from
  the service account's own role mappings because `query-clients` alone lists
  nothing) and the read-only `view-realm` (`/roles/{name}` and its users answer
  403 without it). `view-clients` is never granted: it would expose every
  client's secret.
- **Off-limits accounts (D6).** Nobody manages through the console a Keycloak
  administrator or service account (any effective `realm-management` role), a
  privileged Stalwart account (an `Admin`/`Custom` role or permissions of its
  own; fails closed), or a Nextcloud administrator or sub-administrator. They are
  never linked, reset, offboarded or deleted. **Amended 2026-11-19**
  (`Admin:GlobalAdminsAdministerServers`, on in compose): global admins also
  administer the servers, so that one Keycloak sign-in opens Keycloak's realm
  admin console, Stalwart's web admin and Nextcloud with admin rights. Their
  role carries Keycloak's `realm-admin` as a composite, and granting or
  revoking it in the console gives or takes their mailbox's Stalwart `Admin`
  role (the console's Stalwart account is an administrator for that: Stalwart
  refuses the change, either way, to anyone else) and their Keycloak-signed-in
  Nextcloud account's membership of the `admin` group. A global admin may then manage another global
  admin, whose server rights the actor holds already, so a password reset
  hands over nothing new. Still off-limits: service accounts, Keycloak or
  Stalwart administrators who are not global admins, any other Stalwart
  privilege (a `Custom` role, permissions of its own), Nextcloud
  administrators who are not global admins and Nextcloud sub-administrators;
  entity admins never act on a global admin. A revoke goes to Keycloak first,
  so a server that refuses leaves its administrator role in place, which the
  console reports and shows as a conflict until it is taken away there. Reserved local parts
  (`nightware-admin`, `noreply`, `postmaster`, …) are never assigned, and an
  alias can't be another Keycloak user's address.
- **Offboarding (D10)** also blocks every sign-in to the mailbox: app passwords
  and API keys a user made in Stalwart outlive the Keycloak account, so the
  console merges away the account's `authenticate` permission (delivery is a
  separate permission and continues; verified). Reinstating, repair and
  re-linking restore it. An offboarded administrator loses the console within
  the one-minute scope cache, and nobody reinstates themselves. The wizard
  refuses an offboarded user ("reinstate instead") and never resets an existing
  person's password.
- **Own password (D9).** Administrators never reset their own credential
  through the console's reset tools (`RequireNotSelf`). Instead, **Change
  password** challenges Keycloak with the application-initiated action
  `kc_action=UPDATE_PASSWORD` (same client, same `acr_values`; Keycloak asks
  for a fresh sign-in when the last one is older than its max auth age).
  Keycloak's `kc_action_status` comes back on the authorization response, and
  only `success`, `cancelled` or `error` reach the page. The marker never
  stays in the session.
- **Tokens (D7).** `SaveTokens` stays off; only the ID token is kept, in the
  encrypted cookie, as the `id_token_hint` Keycloak needs at sign-out. `acr` and
  `auth_time` are kept in the session (ASP.NET deletes them by default). A token
  without `auth_time` is refused, and a forced re-authentication must report an
  `auth_time` at or after the console's request (Keycloak does; verified).
- **NightMail sign-in unchanged.** Turning Organizations on activates the
  identity-first step of Keycloak's browser flow (username, then password on a
  second page); `configure-console.sh` disables that step in the realm's bound
  browser flow. The console's own step-up flow is bound to its client only.
- **SPF and DMARC per domain (D4).** Stalwart's `Domain` has no SPF or DMARC
  setting (only `reportAddressUri`), and with `dnsManagement: Manual` the zone
  it generates is a recommendation anyway. So a domain's own records are
  organization attributes `nightware_spf:<domain>` and
  `nightware_dmarc:<domain>` in Keycloak; without them the domain uses
  Stalwart's records. The DNS page replaces Stalwart's apex `v=spf1` TXT and
  `_dmarc` TXT with the chosen ones (never adds beside them: two SPF records
  fail evaluation); where Stalwart publishes none it adds `v=spf1 mx -all` and
  `v=DMARC1; p=quarantine; rua=mailto:postmaster@<domain>`
  (`DomainMailPolicy`). Global administrators edit them on the DNS page
  (`SetDomainMailPolicy`, audited `entity.domain.mail-policy`); removing a
  domain drops its attributes. Stalwart's zone names the MX host (e.g.
  `mail.<domain>`) but not its address, so an MX host inside the domain with no
  A/AAAA there gets one per server public address (`MailHostAddresses`, the
  addresses the SPF suggestion uses); without it the domain receives no mail.
  Nothing is checked against live DNS.
- **Publishing DNS records to Cloudflare (per entity).** An entity may have a
  Cloudflare API token, set by a global administrator on the entity page
  (`SetEntityDnsToken`, audited `entity.dns-token.set`/`.removed`). The token
  is checked first (the zone lookup for each of the entity's domains) and is
  kept only sealed: ASP.NET Data Protection (`ISecretSealer`, purpose
  `dns-token:cloudflare:<entity id>`) as the organization attribute
  `nightware_dns_token:cloudflare`. That keeps D3 ("no store") intact and keeps
  plaintext out of Keycloak's admin events. Tokens are capped at 79 characters
  so the sealed form fits the 255-character attribute value. The DNS page
  plans the changes against the live zone (`PlanDnsPublication`,
  `DnsPublicationPlanner`) and publishes only the plan the admin saw
  (`PublishDnsRecords`, fingerprint, audited `entity.domain.dns-published`
  with counts). The planner never deletes a record the domain doesn't need,
  except duplicates of a one-per-name TXT kind (SPF, DMARC, DKIM, MTA-STS,
  TLS-RPT). It reports another provider's MX as a warning, writes records
  unproxied, skips records outside the zone, and doesn't add a first CAA
  record (which would restrict every other CA). The Cloudflare adapter is its
  own project (`Nightware.Adapters.Cloudflare`): reads retried, writes never.
  Its data format (SRV/CAA/TLSA `data`, TXT as quoted ≤255-character strings)
  is **unverified** against the live API (WireMock only).
- **Other DNS hosts (amended).** An entity's DNS host may instead be
  DigitalOcean, Gandi LiveDNS, Hetzner (the Cloud API's zones; its DNS API closed
  in May 2026) or Amazon Route 53, one `IDnsHosting` each in
  `Nightware.Adapters.DnsHosts`, chosen on its DNS hosting page. The host is
  which `nightware_dns_token:<host>` attribute it has (one at a time; storing one
  removes the others), sealed for `dns-token:<host>:<entity id>`, so entities
  connected to Cloudflare before this kept their attribute, purpose and plans
  unchanged. Record-set hosts (Gandi, Hetzner, Route 53) give each value an id
  from its name, type and a hash of the value, so a plan's fingerprint is stable,
  and change one value per write (read the set, change it, write it back; Hetzner
  with its add/set/remove actions). Route 53 takes `KEYID:SECRET` (61 characters,
  within the 79 the attribute allows). Their formats are **unverified** against
  the live APIs (WireMock only; Route 53's signing matches AWS's published test
  vectors).
- **Azure DNS and Google Cloud DNS (amended).** Their credentials are longer than
  one attribute value: an Azure service principal
  (`TENANT:CLIENT:SUBSCRIPTION:RESOURCEGROUP:SECRET`) and a Google service
  account's key (kept as JSON with the project, address, key ID and private key
  only, up to 4096 characters). A sealed credential longer than 255 characters is
  stored as the attribute's numbered values `00:…`, `01:…` (sealed text is
  base64url, so a colon marks a chunk), and one that fits is one plain value as
  before, so existing entities read unchanged. Each signs in for a short-lived
  access token (client credentials at Entra ID; an RS256 JWT exchanged at
  Google's token endpoint, never the key's own `token_uri`), cached in memory per
  credential hash. Azure zones are read by name in the given resource group, so
  DNS Zone Contributor on the zone is enough; writes are guarded by the set's
  etag; api-version 2018-05-01 has no TLSA. Google writes create, patch or
  delete one record set, so no deletion must match exactly. Both are
  **unverified** against the live APIs (WireMock only).
- **Verified end to end** on an isolated stack (Keycloak 26.8.2, Stalwart 1.1.0,
  Nextcloud 35): MFA sign-in, entity and domain creation (DNS records shown),
  linking existing users, creating a user who then signs in to NightMail and
  reads mail through the façade (one Stalwart account), aliases, offboarding,
  reinstating (with a recreated mailbox), deleting mail and Nextcloud data after
  a forced re-authentication, entity admins confined to their entity, global
  admin grants, sign-out. Scripts: `deploy/scripts/console-e2e/`.

## Open questions

- **OQ1 — first credential delivery.** Is the system-generated, shown-once,
  forced-change temporary password (D9 mode 2) acceptable where the person
  cannot read the new mailbox? Alternative: Stalwart relaying for the compose
  subnet so Keycloak can mail an *alternate* address — but Keycloak only mails
  the user's own `email`, so that alternative also needs a realm e-mail
  override that does not exist; the honest options are mode 2 or a custom
  Keycloak SPI (rejected: keeps the stack small).
- **OQ2 — purge identity (v2).** Offer a global-admin-only "Purge" that deletes
  the Keycloak user, allowed only when mail and Nextcloud data are already
  deleted, with the same guard rails? Until then a disabled identity is kept
  indefinitely, which is the conservative default.
- **OQ2b — who may delete data.** v1 restricts "Delete mail" and "Delete
  Nextcloud data" to global admins. If entity admins must be able to, the
  recommended form is an explicit per-deployment opt-in
  (`Admin:EntityAdminsMayDeleteData`, default `false`), still with the
  disabled-first, fresh-MFA and typed-confirmation guards.
- **OQ3 — Stalwart management principal.** If the step 0 spike shows internal
  accounts cannot authenticate under an OIDC default directory, accept the
  recovery admin for the console, or run the console's Stalwart calls through a
  per-domain internal directory (Enterprise-only)?
- **OQ4 — Nextcloud credential.** Account password from `init-env.sh` (fits the
  scripts) vs an app password minted by `configure.sh` and written to `.env`?
- **OQ5 — entity admins editing entity admins.** v1 lets an entity admin add
  and remove other entity admins of their own entity (not themselves). Prefer
  global-only?
- **OQ6 — v2 candidates**: shared mailboxes (done: ADR 0004, Stalwart `Group`
  accounts with `memberGroupIds`), mailing lists (done: ADR 0003, groups), rooms (only `occ`; no OCS API), quotas
  (`Account.quotas.maxDiskQuota`, Nextcloud `quota`), live DNS verification,
  Keycloak admin-event viewer, FGAP V2 per-organization `manage` grants as a
  second enforcement layer inside Keycloak.
