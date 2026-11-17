# Phase 8 — Admin console (entities, users, downstream linking)

**Goal:** a global or entity administrator signs in once (OIDC + MFA) at
`https://${PUBLIC_HOST}/console/`, manages entities (organisations owning mail
domains) and their users, and the console links accounts that already exist in
Keycloak, Stalwart and Nextcloud and creates the ones that do not — without the
admin ever opening a backend's own console. Decisions: `docs/adr/0002-admin-console.md`.

Depends on: phases 1–7 (merged). Runs alone (it touches frozen files in step 1).

## Decisions recap (see ADR 0002)
- Entity = Keycloak Organization; user ∈ entity = unmanaged membership; global
  admin = realm role `nightware-global-admin`; entity admin = member of the
  organization group `admins` (carries realm role `nightware-entity-admin`).
- No store: link states computed by key matching; writes idempotent; no rollback.
- Second host `Nightware.Admin` (Razor Pages, OIDC code+PKCE, cookie session,
  `SaveTokens=false`, CSP without script), own Keycloak client `nightware-admin`.
- Use cases in `Application/Administration/`; adapters per backend under
  `*/Administration/`; management clients with their own least-privilege
  credentials (`nightware-admin` service account, Stalwart
  `nightware-admin@MAIL_DOMAIN` + role `nightware-management`, Nextcloud
  `nightware-admin`).
- Stalwart pre-created always; Nextcloud pre-provisioned via `user_oidc` API,
  `Pending` otherwise; Keycloak `execute-actions-email`, never admin-chosen
  passwords (generated one-time temporary password is OQ1).
- Offboard = disable. Deleting mail / Nextcloud data is a separate,
  global-admin-only action on a disabled user (fresh MFA + typed address);
  deleting the Keycloak identity is deferred (OQ2).

## Screens (v1)

| Screen | Content | Who |
|---|---|---|
| Dashboard | entities (count, domains, users), backend health (Keycloak, Stalwart, Nextcloud readiness), "my scope" | all admins |
| Entities | list (name, alias, domains, users, admins); **Create entity** (name, alias, first domain) | list: all (scoped); create: global |
| Entity detail | tabs: **Domains** (per domain: Keycloak ✓, Stalwart Linked/Missing/Error, DKIM ✓/–, Add domain, Repair), **DNS records** (table from Stalwart's zone data, copy buttons, download zone file), **Admins** (grant/revoke entity admin), **Users** (same list as Users, filtered) | entity/global |
| Users | list with per-backend **link chips** (Keycloak / Stalwart / Nextcloud: Linked · Missing · Pending · Conflict · Disabled · Error), search, filter by entity/state | scoped |
| User detail | identity, chips with reasons, **Repair**, **Resend set-password e-mail**, **Offboard** (disable; shows what is kept) / **Reinstate** (shows plan; "recreate empty mailbox" when mail was deleted), admin roles. For a **disabled** user a "Danger zone" lists **Delete mail** and **Delete Nextcloud data**, each a separate page: what will be deleted (address, aliases, mailbox size), what is kept (Keycloak identity; the other backend), retype-the-address field; opening it requires authentication fresher than 5 min (redirect to re-auth otherwise). | scoped; danger zone: global |
| New user wizard | 1 *Who*: entity, first/last name, primary address (local part + domain picker limited to the entity's domains), username (proposed: local part if free, else full address; immutable later), aliases. 2 *Plan*: per backend "will be created" / "will be linked to existing account …" / "conflict: …" (conflict blocks). 3 *Apply*: per-backend result; then credential delivery (mode 1 e-mail link, mode 2 one-time password shown once). | scoped |
| Global admins | list, grant/revoke (adds `CONFIGURE_TOTP`) | global |
| Sign-in / denied / signed-out | plain pages | – |

Later (not v1): shared mailboxes, mailing lists, rooms, quotas, live DNS checks,
admin-event viewer, purge of the Keycloak identity.

## File ownership

### `src/Nightware.Domain/Administration/`
- `Entity.cs` — `Entity(Id, Alias, Name, IReadOnlyList<MailDomain> Domains)`; `MailDomain(Name)`.
- `ManagedUser.cs` — `ManagedUser(Id, Username, DisplayName, Email, Enabled, EntityId?, Aliases, IsGlobalAdmin, EntityAdminOf)`.
- `BackendLink.cs` — `enum LinkState { Linked, Missing, Pending, Conflict, Disabled, Error }`; `BackendLink(BackendKind Backend, LinkState State, string? Detail, string? DownstreamId)`; `enum BackendKind { Identity, Mail, Groupware }`.
- `Provisioning.cs` — `enum PlannedAction { Create, Link, Update, Skip, Conflict }`; `ProvisioningPlan(IReadOnlyList<PlannedStep>)`; `PlannedStep(BackendKind, PlannedAction, string Reason)`; `ProvisioningOutcome(IReadOnlyList<BackendLink>)`; `enum CredentialDelivery { ActionEmail, TemporaryPassword }`; `TemporaryCredential(string Password)` (ToString redacts).
- `DnsRecord.cs` — `DnsRecord(Name, Type, Ttl, Value)`; `DomainStatus(MailDomain, BackendLink Mail, bool DkimPresent)`.
- `AdminActor.cs` — `AdminActor(string UserId, bool IsGlobal, IReadOnlySet<string> EntityIds)`.
- Tests: `tests/Nightware.Domain.Tests/Administration/` (records, redaction of `TemporaryCredential`).

### `src/Nightware.Application/Administration/`
- `AdministrationServiceCollectionExtensions.cs` — `AddAdministrationUseCases()` (**not** called from `AddApplication()`).
- `IAdminActor.cs` (scoped, host-provided), `IAdminScopeSource.cs` — `ResolveAsync(userId)` → `AdminActor`.
- `AdminAuthorization.cs` — pure policy: `RequireGlobal`, `RequireEntity`, `RequireAddressInEntity`, `RequireNotSelf`.
- `LinkStateEvaluator.cs` — pure: `(KeycloakSide?, MailSide?, GroupwareSide?) → BackendLink[]` and `→ ProvisioningPlan`.
- Ports (`AdministrationPorts.cs`):
  - `IEntityRegistry` — `ListAsync(scope)`, `GetAsync(id|alias)`, `CreateAsync(NewEntity)`, `AddDomainAsync`, `RemoveDomainAsync`, `ListMembersAsync(entityId, page)`, `AddMemberAsync`, `RemoveMemberAsync`, `ListAdminsAsync`, `GrantEntityAdminAsync`, `RevokeEntityAdminAsync`.
  - `IIdentityAccounts` — `FindByEmailAsync`, `FindByUsernameAsync`, `GetAsync`, `CreateAsync(NewIdentity)`, `UpdateProfileAsync`, `SetAliasesAsync` (user attribute `nightware_aliases`), `SetEnabledAsync`, `EndSessionsAsync`, `SendActionEmailAsync(id, actions, lifespan)`, `SetTemporaryPasswordAsync(id, password)`, `GrantGlobalAdminAsync`, `RevokeGlobalAdminAsync`, `ListGlobalAdminsAsync`, `AddRequiredActionAsync`.
  - `IMailAccounts` — `FindByAddressAsync`, `CreateAsync(localPart, domain, description)`, `SetAliasesAsync`, `GetUsageAsync` (v1 read-only).
  - `IMailDomains` — `ListAsync`, `FindAsync(name)`, `CreateAsync(name, description)`, `GetDnsRecordsAsync(name)`, `HasDkimAsync(name)`, `ReloadAsync`.
  - `IGroupwareAccounts` — `FindAsync(uid)`, `PreProvisionAsync(uid, email, displayName)`, `SetEnabledAsync(uid, enabled)`, `UpdateAsync(uid, email?, displayName?)`.
  - `IBackendStatus` — readiness per backend for the dashboard.
  - `IAuditLog` — `Record(AuditEvent)`.
  - `IPasswordGenerator` — `Generate()` (host implements with `RandomNumberGenerator`).
- Use cases (sealed, one `ExecuteAsync`, `Result<T>`), in `Entities/` and `Users/`:
  `ListEntities`, `GetEntity`, `CreateEntity`, `AddEntityDomain`, `RemoveEntityDomain`, `GetEntityDnsRecords`, `RepairEntityDomain`, `ListEntityAdmins`, `GrantEntityAdmin`, `RevokeEntityAdmin`, `ListGlobalAdmins`, `GrantGlobalAdmin`, `RevokeGlobalAdmin`, `ListUsers`, `GetUser`, `PlanUserProvisioning`, `ApplyUserProvisioning`, `RepairUser`, `SetUserAliases`, `ResendCredentialEmail`, `IssueTemporaryPassword`, `OffboardUser`, `ReinstateUser`, `GetSystemStatus`.
- Tests: `tests/Nightware.Application.Tests/Administration/` — every use case with NSubstitute ports (authorization matrix: global / own entity / other entity / self; address outside entity; conflict blocks writes; partial failure leaves earlier links intact), `AdminAuthorizationTests`, `LinkStateEvaluatorTests` (table-driven over every state).

### `src/Nightware.Adapters.Directory/`
- `Core/Admin/IKeycloakAdminClient.cs` (+ impl): add organizations (`ListOrganizationsAsync`, `GetOrganizationAsync`, `CreateOrganizationAsync`, `UpdateOrganizationAsync`, `ListMembersAsync`, `AddMemberAsync`, `RemoveMemberAsync`, `ListMemberOrganizationsAsync(userId)`, `ListMemberGroupsAsync(orgId, userId)`, org groups get/create/add member), users (`CreateUserAsync` → id from `Location`, `UpdateUserAsync`, `SetEnabledAsync`, `LogoutAsync`, `ExecuteActionsEmailAsync`, `ResetPasswordAsync(temporary)`, realm role mapping get/add/remove, `ListUsersInRoleAsync`). Models in `Core/Admin/KeycloakOrganization.cs`, `KeycloakMember.cs`.
- `DirectoryAdapterServiceCollectionExtensions.cs`: split into `AddKeycloakCore(configuration)` (options, token client, admin client, health) and the existing `AddDirectoryAdapter` (core + `IBackendCredentials` + features). The admin host calls `AddKeycloakCore` only.
- `Administration/KeycloakEntityRegistry.cs`, `KeycloakIdentityAccounts.cs`, `KeycloakAdminScopeSource.cs`, `KeycloakBackendStatus.cs`, `DirectoryAdministrationServiceCollectionExtensions.cs` (`AddKeycloakAdministration()`).
- Tests: `tests/Nightware.Adapters.Directory.Tests/Administration/` + `Core/Admin/` WireMock tests for every new request shape and error (400/401-refresh/403/404/409/429/5xx); fixtures recorded from the live stack into `Fixtures/`.

### `src/Nightware.Adapters.Jmap/Administration/`
- `StalwartManagementOptions.cs` (`StalwartManagement:{SessionUrl|ApiUrl, Username, Password | ApiKey, RequestTimeout}`) + validator.
- `StalwartManagementClient.cs` — its own typed `HttpClient` (Basic or `Bearer API_…`), `using: ["urn:ietf:params:jmap:core","urn:stalwart:jmap"]`, cookie-free handler, retry on safe calls only; reuses `Core/Requests` builders/response parsing where they are credential-agnostic (do not route through `JmapAuthenticationHandler`).
- `StalwartMailAccounts.cs`, `StalwartMailDomains.cs`, `ZoneFileParser.cs`, `StalwartBackendStatus.cs`, `JmapAdministrationServiceCollectionExtensions.cs` (`AddStalwartAdministration()`).
- Tests: `tests/Nightware.Adapters.Jmap.Tests/Administration/` — WireMock for `x:Account/query|get|set`, `x:Domain/query|get|set`, `x:DkimSignature/query`, `x:Action/set`, `setError` mapping (`alreadyExists` → Conflict, `forbidden` → Forbidden), zone-file parser over recorded `dnsZoneFile` samples.

### `src/Nightware.Adapters.Dav/Administration/`
- `NextcloudProvisioningOptions.cs` (`NextcloudProvisioning:{BaseUrl, Username, Password, OidcProviderId}`) + validator.
- `NextcloudProvisioningClient.cs` — typed `HttpClient`, Basic, `OCS-APIRequest: true`, JSON (`?format=json`), cookie-free; `OcsEnvelope` parsing (`meta.statuscode`).
- `NextcloudGroupwareAccounts.cs`, `NextcloudBackendStatus.cs`, `DavAdministrationServiceCollectionExtensions.cs` (`AddNextcloudAdministration()`).
- Tests: `tests/Nightware.Adapters.Dav.Tests/Administration/` — WireMock for get user (found / 404 / other backend), `user_oidc` pre-provision (201 / 404 app missing → `Pending` / 409), disable/enable, OCS error codes.

### `src/Nightware.Hosting/` (new, shared by both hosts)
- Moved from `Nightware.Api`: `SecurityHeadersMiddleware` (+ `SecurityHeadersOptions.ContentSecurityPolicy`), `ProxyOptions` + forwarded-headers registration, `CookielessHttpClientsFilter`, `LogRedaction`, `RedactingScopeProvider`, `RedactingHttpClientLoggingFilter`, `UrlRedaction`. Namespaces become `Nightware.Hosting.*`; the Api keeps `GraphCors`, rate limiting, budgets, telemetry, `RequestBodyLimit`, `ReadinessCache`, `SigningKeyValidator`.
- Tests: the corresponding `Nightware.Api.Tests/Hosting|Observability` tests move to `tests/Nightware.Hosting.Tests/` where they do not need the Graph pipeline; `LogRedactionTests` and `HttpClientCookiePolicyTests` stay in Api.Tests (they drive the real Graph host) and a twin is added in Admin.Tests.

### `src/Nightware.Admin/` (new host)
- `Program.cs`, `Composition/AdminComposition.cs` (`AddNightwareAdmin`, `UseNightwareAdmin`: forwarded headers → security headers → correlation → exception page (generic) → routing → authentication → authorization → Razor Pages; `/healthz/live`, `/healthz`).
- `Auth/AdminAuthOptions.cs` (`Admin:{Authority, MetadataAddress, ClientId, ClientSecret 🔒, AcceptedAcrValues[], IdleTimeout, AbsoluteLifetime, PublicBaseUrl}`), `Auth/AdminAuthenticationExtensions.cs` (OIDC + cookie, `SaveTokens=false`, `max_age`, `acr` check in `OnTokenValidated`, absolute lifetime in `OnValidatePrincipal`, RP-initiated logout), `Auth/AcrValidator.cs` (+ Production refuses empty `AcceptedAcrValues`), `Auth/SessionAdminActor.cs` (`IAdminActor` from the cookie principal + `IAdminScopeSource`, 60 s cache), `Auth/RequireAdminHandler.cs` (authorization policy: any admin scope, else `/denied`).
- `Pages/` — `Index`, `Entities/Index|Create|Detail` (+ handlers `OnPostAddDomain`, `OnPostRepairDomain`, `OnPostGrantAdmin`, `OnPostRevokeAdmin`), `Entities/Dns`, `Users/Index|Detail|New` (wizard steps as `?step=` with TempData for the draft; plan re-computed server-side on apply, never trusted from the form), `Admins/Index`, `Account/SignIn|SignOut|Denied|SignedOut`, `Shared/_Layout` (no script; one stylesheet under `wwwroot/console.css`).
- `Hosting/AuditLog.cs` (`IAuditLog` → `ILogger` category `Nightware.Admin.Audit`, event `admin.audit`), `Hosting/PasswordGenerator.cs`.
- `appsettings.json`, `appsettings.Development.json`, `Dockerfile`.
- Tests: `tests/Nightware.Admin.Tests/` — `TestHost` like the Api's (test auth scheme that injects a principal with chosen scope); every page GET/POST for global, entity and foreign-entity actors (200/403/404), antiforgery enforced, unauthenticated → redirect to sign-in, security headers and CSP, `acr` rejection, absolute-lifetime expiry, cookie flags, `HttpClientCookiePolicyTests` twin, `LogRedactionTests` twin (password never appears in logs, including the temporary password path).

### `tests/Nightware.Architecture.Tests/LayerTests.cs`
- Load `Nightware.Hosting` and `Nightware.Admin`; add rules: `Nightware.Hosting` depends on no other solution assembly; `Nightware.Admin` never depends on `Nightware.Api`; `Nightware.Api` never depends on `Nightware.Application.Administration` or any `*.Administration` adapter namespace; only `Nightware.Admin.Composition` and `Nightware.Api.Composition` depend on adapters; `Nightware.Admin.Pages` depend on Application only.

### `tests/Nightware.Integration.Tests/Administration/`
- `AdminLiveStack.cs` (reads `NIGHTWARE_ADMIN_CLIENT_SECRET`, `STALWART_MANAGEMENT_PASSWORD`, `NEXTCLOUD_PROVISIONING_PASSWORD` from `.env` via `DevStack`), `EntityLiveTests` (create entity `it-<rand>` with domain `it-<rand>.example`, see it in Keycloak and Stalwart, DNS records contain MX/SPF/DKIM/DMARC; remove the Stalwart domain by hand is out of scope — domains are left, named uniquely), `UserLiveTests` (plan for a new user shows Create×3; apply; Keycloak user is org member; Stalwart `x:Account/query` finds the address; Nextcloud OCS finds the uid in backend `user_oidc`; repair is a no-op; offboard disables Keycloak + Nextcloud; reinstate), `LinkingLiveTests` (bob: plan shows Link×3; a username whose Nextcloud uid carries another e-mail reports `Conflict` and apply refuses).

## Deploy changes (`deploy/`)
- `keycloak/nightware-realm.json`: `organizationsEnabled: true`; `adminEventsEnabled: true`, `adminEventsDetailsEnabled: true`, `eventsExpiration: 7776000`; `smtpServer {host: stalwart, port: "25", from: "noreply@${MAIL_DOMAIN}", fromDisplayName: "Nightware", auth: "false", ssl: "false", starttls: "false"}`; realm roles `nightware-global-admin`, `nightware-entity-admin`; realm attribute `acr.loa.map` `{"pwd":"1","mfa":"2"}`; client `nightware-admin` (confidential, standard flow, `pkce.code.challenge.method: S256`, redirect `${PUBLIC_URL}/console/signin-oidc`, post-logout `${PUBLIC_URL}/console/signed-out`, `serviceAccountsEnabled`, optional scope `kc-admin-api`, "Minimum ACR value" `mfa` unless `ADMIN_REQUIRE_MFA=false`); service-account user with realm-management `manage-users`, `view-users`, `query-users`, `manage-organizations`, `view-organizations`, `query-organizations`; alice gets `nightware-global-admin` (dev).
- `keycloak/configure.sh`: browser flow copy with Conditional-LoA (level 2 → OTP form) bound as the realm browser flow (skipped with `ADMIN_REQUIRE_MFA=false`); organization "Nightware" (alias `nightware`, domain `MAIL_DOMAIN`), org group `admins`, alice + bob as unmanaged members; declare user-profile attribute `nightware_aliases` (multivalued, admin-edit) via `user-profile.jq`; all idempotent read-modify-write.
- `stalwart/configure.sh`: role `nightware-management` (`description` "Nightware admin console"; `roleIds` = User role; `enabledPermissions` as ADR D8); account `nightware-admin@${MAIL_DOMAIN}` (`@type User`, Password credential `STALWART_MANAGEMENT_PASSWORD`, `roles {@type: Custom, roleIds}`); verify with a `x:Account/query` as that principal and **fail loudly** if it cannot authenticate (OQ3).
- `nextcloud/configure.sh`: `occ user:add --password-from-env --group admin --display-name "Nightware admin console" nightware-admin` (`OC_PASS=NEXTCLOUD_PROVISIONING_PASSWORD`), idempotent; print the `user_oidc` provider id and write `NEXTCLOUD_OIDC_PROVIDER_ID` into `.env` if absent (step 0 decides how it is read).
- `scripts/init-env.sh`: generate `NIGHTWARE_ADMIN_CLIENT_SECRET`, `STALWART_MANAGEMENT_PASSWORD`, `NEXTCLOUD_PROVISIONING_PASSWORD`; add `ADMIN_ALLOWED_CIDRS` and `ADMIN_REQUIRE_MFA` to `.env.example` (documented defaults).
- `docker-compose.yml`: service `nightware-admin` (profile `api`, build `src/Nightware.Admin/Dockerfile`, image `nightware/admin:dev`, env: `Admin__Authority`, `Admin__MetadataAddress` (`http://keycloak:8080/auth/...`), `Admin__ClientId`, `Admin__ClientSecret`, `Admin__PublicBaseUrl=https://${PUBLIC_HOST}/console`, `Admin__AcceptedAcrValues__0=mfa` (empty when `ADMIN_REQUIRE_MFA=false`), `Directory__*` bound to `nightware-admin`, `StalwartManagement__*`, `NextcloudProvisioning__*`, `ForwardedHeaders__KnownNetworks__0`, `SSL_CERT_FILE`; liveness healthcheck; no published port).
- `caddy/Caddyfile`: `handle_path /console/* { @allowed remote_ip {$ADMIN_ALLOWED_CIDRS}; reverse_proxy @allowed nightware-admin:8080 { header_up X-Forwarded-Prefix /console }; respond 403 }`.
- `smoke.sh`: `nightware-admin` service-account token lists organizations (200); Stalwart management principal `x:Account/query` (200) and is refused `impersonate` (master-user login fails); Nextcloud `nightware-admin` OCS `GET users/nightware-admin` (200); `GET /console/healthz` (200) when the profile is up; `/console/` from loopback is 302 to Keycloak.
- `get-token.sh --admin-service`: the `nightware-admin` service-account token.
- `bootstrap.sh`: unchanged order; the console needs nothing at first-run beyond the scripts above.

## Docs to update
- `docs/architecture.md`: layers table (+ `Nightware.Hosting`, `Nightware.Admin`), feature list (+ `Administration/`), "Per-request identity" note that the admin host has no `IUserContext`.
- `docs/adr/0001-skeleton-decisions.md`: addendum noting the one-time move of `Api/Hosting` files and the split of `AddDirectoryAdapter`.
- `deploy/README.md`: stack table row for `/console/`, clients table (+ `nightware-admin`), test users (alice is global admin; `ADMIN_REQUIRE_MFA`), new secrets, "Admin console" section, Known limitations (Stalwart principal result of the spike).
- `docs/operations/configuration.md`: new sections `Admin`, `StalwartManagement`, `NextcloudProvisioning`.
- `docs/operations/security.md`: "Admin console" (session model, CSP, MFA, network allow-list, least-privilege principals, audit).
- `docs/operations/runbook.md`: locked-out admin recovery (grant `nightware-global-admin` in Keycloak's console), rotating the three new secrets, "console says Conflict".
- `docs/known-issues.md`: close "Stalwart cannot look up recipients in OIDC" for console users; add the OQs that stay open.
- `docs/phases/agent-brief.md`: frozen-file list gains `Nightware.Hosting`.

## Ordered steps (each ends with `dotnet build` clean and `dotnet test` green)

0. **Spikes (deploy only, no src).** Against the running stack: (a) create role + `nightware-admin@MAIL_DOMAIN` with a password and call `x:Account/query` with Basic while `directoryId` is the OIDC directory; also with an API key (`Bearer API_…`); record the result in `deploy/README.md` and pick D8's primary or fallback. (b) `x:Domain/set` create a throw-away domain with `dkimManagement: Automatic`; read `dnsZoneFile`; note whether ReloadSettings was needed; save the zone text as a test fixture. (c) `POST /ocs/v2.php/apps/user_oidc/api/v1/user` for a throw-away uid; `GET /ocs/v1.php/cloud/users/{uid}?format=json` and record the `backend` string; find the provider id. (d) `PUT /users/{id}/execute-actions-email` with realm SMTP set to `stalwart:25`; confirm the mail lands in the user's Stalwart inbox. (e) Create an organization, org group `admins`, add a member, read `members/{id}/organizations` and `…/groups` with a service-account token carrying `manage-organizations`.
1. **`Nightware.Hosting` extraction.** Move the shared hosting files, update namespaces and tests, add the project to `.slnx` and ArchUnit loading. No behaviour change; `LogRedactionTests`/`HttpClientCookiePolicyTests` still green.
2. **Domain `Administration/`** records + tests.
3. **Application `Administration/`**: ports, `AdminAuthorization`, `LinkStateEvaluator`, use cases, DI; tests. ArchUnit rule "Api does not depend on Administration" added now (passes trivially).
4. **Keycloak**: admin-client extensions + WireMock tests; `AddKeycloakCore` split (Api behaviour unchanged, Api.Tests green); `Administration/` port implementations + tests.
5. **Stalwart**: management client, domains/accounts ports, zone parser; tests with step-0 fixtures.
6. **Nextcloud**: provisioning client and port; tests.
7. **Admin host, read-only.** Project, composition, auth (OIDC/cookie/acr/lifetimes), security headers, health, `IAdminActor`, dashboard, entity list/detail/DNS, user list/detail with chips. `Nightware.Admin.Tests` with the test auth scheme; ArchUnit rules for the new host.
8. **Admin host, writes.** Create entity, add/repair domain, grant/revoke admins, user wizard (plan → apply → credential delivery), repair, aliases, offboard/reinstate, resend e-mail. Audit log events; redaction tests for the temporary-password path.
9. **Deploy.** Realm, configure scripts, `init-env.sh`, compose, Caddy, `smoke.sh`, `get-token.sh`. Bring the stack up from fresh volumes once; run `smoke.sh`.
10. **Integration tests** (`NIGHTWARE_INTEGRATION=1`): entity, user, linking suites; `make integration` includes them.
11. **Docs** listed above; record test counts and the step-0 findings in the phase report.

## Acceptance
- Unit, WireMock, page and architecture tests for every class; `make ci` green.
- Live: a global admin creates entity "Acme" with domain `acme.example`; the DNS tab lists MX, SPF, DKIM (two selectors), DMARC; an entity admin of Acme creates `carol@acme.example` (plan: Create×3), receives the set-password e-mail in Keycloak's SMTP path or a one-time password; carol signs in to NightMail and sends/receives mail; the same entity admin cannot open the "Nightware" entity (403 from the use case, not just hidden), cannot grant global admin, cannot assign `carol@nightware.localhost`; bob (existing in all three) shows Link×3 and Repair is a no-op; a deliberately mismatched Nextcloud uid shows `Conflict` and apply refuses; offboarding carol disables Keycloak and Nextcloud while mail to her address still lands in Stalwart.
- Security: console refuses a sign-in whose ID token lacks `acr=mfa` (when MFA is required), sets the CSP without script, rejects POSTs without the antiforgery token, holds no tokens in cookies, and `smoke.sh` proves the Stalwart principal cannot impersonate.

## Offboarding and data deletion (ADR 0002 §D10)

Offboarding disables; deleting mail or Nextcloud data is a separate,
global-admin-only action on an already-disabled user, behind fresh MFA and a
typed confirmation. These additions belong to the steps named below.

- **Domain `Administration/`** (step 2): `enum DeletionTarget { Mail, GroupwareData }`;
  `DeletionPreview(ManagedUser, DeletionTarget, bool AlreadyAbsent, long? MailboxBytes, IReadOnlyList<string> Removes, IReadOnlyList<string> Keeps)`;
  `AdminActor` gains `DateTimeOffset AuthenticatedAt` and `string? Acr`;
  `ManagedUser` gains `MailDeletedAt?`, `GroupwareDeletedAt?`.
- **Application `Administration/`** (step 3): `AdminAuthorization` gains
  `RequireDisabled(user)`, `RequireFreshAuthentication(actor, now, maxAge, acceptedAcr)`,
  `RequireTypedConfirmation(typed, expectedAddress)`, `RequireMayDeleteData(actor)`
  (global, or entity admin + `EntityAdminsMayDeleteData`). Ports:
  `IMailAccounts.DestroyAsync(accountId)` (returns `NotFound` honestly; the use
  case turns it into "already absent"), `IMailAccounts.GetUsageAsync`,
  `IGroupwareAccounts.DeleteAsync(uid)`, `IIdentityAccounts.SetMarkerAsync(id, name, value)`.
  Use cases: `GetDeletionPreview`, `DeleteUserMail`, `DeleteUserGroupwareData`;
  `ReinstateUser` gains `recreateMailbox: bool`; `RepairUser` refuses disabled
  users. Options: `AdministrationOptions { DestructiveActionMaxAge = 5 min, EntityAdminsMayDeleteData = false }`
  (bound by the host from `Admin:`). Tests, per deletion use case: entity
  admin refused (allowed only with the flag), enabled target refused, self
  refused, stale `AuthenticatedAt` refused, wrong `acr` refused, typed address
  mismatch refused, `Conflict` refused with no port write, absent account →
  success + audit "already absent", success writes the marker and audit, port
  failure → `Error` with no marker.
- **Adapters** (steps 4–6): Jmap `StalwartMailAccounts.DestroyAsync`
  (`x:Account/set {destroy:[id]}`; WireMock: destroyed, `notFound`,
  `forbidden`) and `GetUsageAsync` (`usedDiskQuota`). Dav
  `NextcloudGroupwareAccounts.DeleteAsync` (`DELETE /ocs/v1.php/cloud/users/{uid}`;
  WireMock: 100, 404 → NotFound, 997 → Forbidden). Directory: markers through
  the existing `UpdateUserAttributesAsync`; `user-profile.jq` declares
  `nightware_mail_deleted_at`, `nightware_groupware_deleted_at` (admin-only).
- **Admin host** (step 8): `Auth/FreshAuthenticationFilter` (page filter
  `[RequireFreshAuthentication]` → `Account/Reauthenticate?returnUrl=`, which
  challenges with `max_age=0` and `acr_values` and returns),
  `Pages/Users/DeleteMail` and `Pages/Users/DeleteGroupwareData` (GET preview,
  POST with antiforgery + typed address), `Pages/Users/Reinstate` (plan +
  recreate checkbox). Tests: redirect to re-auth when stale, 403 for an entity
  admin, 400 on a wrong typed address, success renders the outcome, nothing
  destructive on GET, the typed address not logged beyond the audit event.
- **Deploy** (step 9): `stalwart/configure.sh` grants `sysAccountDestroy` to
  `nightware-management`; `smoke.sh` keeps asserting the principal cannot
  impersonate. `.env.example`: optional `ADMIN_DESTRUCTIVE_MAX_AGE` →
  `Admin__DestructiveActionMaxAge`.
- **Integration** (step 10): `OffboardingLiveTests` — create `it-<rand>`;
  offboard → Keycloak disabled, Nextcloud disabled, Stalwart account still
  present; delete mail with a fresh-auth actor → `x:Account/query` empty,
  marker set, second call a no-op; delete Nextcloud data → OCS 404; reinstate
  with recreate → Stalwart account exists, Nextcloud user pre-provisioned; an
  enabled user is refused.
- **Acceptance**: an entity admin sees no danger zone and gets 403 on a forged
  POST; a global admin whose sign-in is older than 5 minutes is sent back to
  Keycloak (OTP) before the confirmation renders; retyping the wrong address is
  refused; after "Delete mail" Stalwart no longer has the account, Keycloak and
  Nextcloud are untouched, and the user page reads "mail deleted on …";
  running it again reports "already absent".

## Open questions
Carried from ADR 0002: OQ1 first-credential delivery, OQ2 purge identity, OQ2b who may delete data, OQ3
Stalwart principal (decided by step 0), OQ4 Nextcloud credential form, OQ5
entity admins managing entity admins, OQ6 v2 scope. Decided: OQ1 (temporary password offered), OQ2b (entity-admin deletion opt-in,
default off), OQ5 (entity admins manage their own entity's admins), exposure
(anywhere; `ADMIN_ALLOWED_CIDRS` defaults to `0.0.0.0/0 ::/0`). Plus: whether `max_age=300` is too aggressive for admins
who hop between NightMail and the console.
