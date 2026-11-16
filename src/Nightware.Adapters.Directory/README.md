# Nightware.Adapters.Directory

Keycloak integration for Nightware. The project has two parts:

- **Identity infrastructure** (`Core/`, `Credentials/`). This is the token client, the backend credential strategies (`IBackendCredentials`), the Keycloak admin REST client and the realm health check.
- **Directory feature ports**, built on top of the admin client: `IUserDirectory` (`Directory/`, phase 5A) and `IUserPreferencesStore` (`MailboxSettings/`, phase 5B). User photos live in Nextcloud, so `IUserPhotoReader` is in `Adapters.Dav/Directory`.

`AddDirectoryAdapter(services, configuration)` registers everything. The host must also register a scoped `IUserContext`; the Api does this.

## Credential strategies

Adapters never read the caller's bearer token directly (ADR 0001 §16). They call:

```csharp
Result<BackendCredential> c = await backendCredentials.ForAsync(Backend.Mail, ct);
request.Headers.Authorization = new AuthenticationHeaderValue(c.Value.Scheme, c.Value.Parameter);
// c.Value.ActAs: the mailbox to impersonate, set by the ServiceAccount strategy
```

| Strategy | Credential | When to use it |
|---|---|---|
| `TokenExchange` (default) | `Bearer <token aud=audience>` | Normal operation. The caller's token (`aud nightware-api`) is exchanged under RFC 8693 for one audienced to the backend. |
| `PassThrough` | `Bearer <caller's token>` | The backend accepts `aud nightware-api` tokens directly. |
| `ServiceAccount` / `Basic` | `Basic base64(user:password)`, `ActAs` = caller's mailbox | Fallback, e.g. a Stalwart master user. |
| `ServiceAccount` / `ClientCredentials` | `Bearer <nightware-api service-account token>`, `ActAs` = caller's mailbox | Fallback for a backend that trusts the façade's service account. |

`Backend.Directory` always uses the `nightware-api` service account (client credentials, no `ActAs`), following ADR 0001 §17. It cannot be configured.

**How to impersonate is the consuming adapter's job.** `ActAs` only names the target mailbox. For example, Stalwart's master-user login format (`user%master`) belongs in `Adapters.Jmap`, not here.

### Token exchange details

- The request is Keycloak 26 *standard token exchange* (V2). It sends `grant_type=urn:ietf:params:oauth:grant-type:token-exchange`, `subject_token`, `subject_token_type=…:access_token`, `requested_token_type=…:access_token` and `audience=<client id>`, and authenticates with `client_secret_basic`. See <https://www.keycloak.org/securing-apps/token-exchange>.
- Results are cached in `IMemoryCache`. The key is `SHA-256(subject token)` + backend + audience; the raw token is never stored as a key. An entry lives until `min(exchanged exp − Cache:ExpirySkew, subject exp)`. Failures are not cached.
- Exchange requests are never retried.

### Error mapping

| Keycloak answer | Result |
|---|---|
| Subject token rejected: `400 invalid_request "Invalid token"` (Keycloak's answer for garbage, expired or logged-out tokens), `invalid_grant`, `invalid_token`, `403 access_denied` | `BackendAuthFailed`. The client must sign in again. |
| Façade misconfigured: wrong secret (`401 unauthorized_client`), unknown or unavailable audience (`invalid_client` / `invalid_request "Requested audience not available"`), any client-credentials 4xx, admin API 401 after one refresh | `BackendUnavailable`, logged at **Error**. Re-authenticating the user cannot fix it. |
| 429, 5xx, timeout, unreachable | `BackendUnavailable` (with `RetryAfterSeconds` when Keycloak sends `Retry-After`) |

Tokens never reach the logs. `BackendCredential.ToString()` and `AccessToken.ToString()` redact them, and the tests check every log category at Trace level.

## Configuration

```jsonc
{
  "Directory": {
    "KeycloakBaseUrl": "https://nightware.localhost/auth", // Keycloak root (with its relative path), not the realm URL; an internal URL is fine
    "Realm": "nightware",
    "ClientId": "nightware-api",
    "ClientSecret": "<from a secret store / Directory__ClientSecret>",
    "RequestTimeout": "00:00:10",          // per attempt
    "MaxRetryAttempts": 2,                 // GETs and client-credentials only
    "RetryBaseDelay": "00:00:00.200",
    "ServiceAccountTokenRefreshSkew": "00:00:30"
  },
  "BackendCredentials": {
    "Mail":      { "Strategy": "TokenExchange", "Audience": "stalwart" },
    "Groupware": { "Strategy": "TokenExchange", "Audience": "nextcloud" },
    "Cache":     { "Enabled": true, "ExpirySkew": "00:00:30" }
  }
}
```

The values shown are the defaults, except `KeycloakBaseUrl`, `Realm` and `ClientSecret`, which have to be set. Here is a Stalwart master-user fallback:

```jsonc
"BackendCredentials": {
  "Mail": {
    "Strategy": "ServiceAccount",
    "ServiceAccount": { "Mode": "Basic", "Username": "admin", "Password": "<secret>" }
  }
}
```

Options are validated the first time they are used. A host that wants to fail fast can add `services.AddOptions<DirectoryOptions>().ValidateOnStart()`.

### Keycloak realm requirements

The reference realm is `deploy/keycloak/nightware-realm.json`.

- `nightware-api` must be a confidential client with `serviceAccountsEnabled` and `standard.token.exchange.enabled=true`.
- `nightware-api` needs audience mappers for every exchange audience (`stalwart`, `nextcloud`). An exchange can only narrow to audiences already available to the requester.
- NightMail's tokens must carry `aud nightware-api`.
- The service account needs `realm-management`: `view-users` and `query-users`. Add `manage-users` for attribute writes.
- Attributes written through the admin client (e.g. `zoneinfo`) must be declared in the realm's user profile, or `unmanagedAttributePolicy` must allow them. Otherwise Keycloak 24+ drops them silently.

## Admin client

`IKeycloakAdminClient` (a typed `HttpClient`, transient) wraps `/admin/realms/{realm}/users`. It authenticates with the cached service-account token. On a 401 it refreshes the token and retries once.

```csharp
Result<KeycloakUser> byId    = await admin.GetUserAsync(id, ct);
Result<KeycloakUser> byEmail = await admin.FindUserByEmailAsync("alice@example.com", ct); // exact; NotFound unless exactly one match
Result<KeycloakUser> byName  = await admin.FindUserByUsernameAsync("alice", ct);

Result<IReadOnlyList<KeycloakUser>> page = await admin.FindUsersAsync(
    new KeycloakUserQuery { Search = "ali", First = 0, Max = 25 }, ct); // "ali" prefix, "*ali*" infix, "\"ali\"" exact
Result<int> total = await admin.CountUsersAsync(new KeycloakUserQuery { Search = "ali" }, ct);
Result<IReadOnlyList<KeycloakUser>> inZone = await admin.FindUsersAsync(
    new KeycloakUserQuery { Attributes = new Dictionary<string, string> { ["zoneinfo"] = "Europe/Berlin" } }, ct); // q=zoneinfo:Europe/Berlin

Result<IReadOnlyDictionary<string, IReadOnlyList<string>>> attrs = await admin.GetUserAttributesAsync(id, ct);
Result<KeycloakUser> updated = await admin.UpdateUserAttributesAsync(id, new Dictionary<string, IReadOnlyList<string>?>
{
    ["zoneinfo"] = ["Europe/Berlin"], // set
    ["locale"] = null,                // remove
}, ct);
```

`UpdateUserAttributesAsync` is a read-modify-write: it GETs the full representation, merges the changes, and PUTs everything back. That way fields the client does not model survive the update. Keycloak has no ETags, so two concurrent writers to the same user can lose an update.

Errors map as follows:

| Status | Result |
|---|---|
| 400 | `Validation` |
| 401 (after the retry) | `BackendUnavailable` |
| 403 | `Forbidden` |
| 404 | `NotFound` |
| 409 | `Conflict` |
| 429 | `Throttled` (with `RetryAfterSeconds`) |
| 5xx, timeout | `BackendUnavailable` |

GETs are retried on transient failures other than 429; PUTs are not.

## User directory (`IUserDirectory`)

`KeycloakUserDirectory` serves `/me`, `/users` and `/users/{id|upn}` as the service account.

- **Lookup.** An address is tried as an exact email, then as an exact username, because a username can look like an address. A UUID is tried as a Keycloak id. Anything else is tried as a username. Service-account users (`service-account-*`) are never people: a lookup of one is `NotFound`.
- **Listing.** Only enabled users are listed, with `briefRepresentation=false` so that attributes come back. The page asks Keycloak for `$top + 1` rows to learn whether another page exists. The `$skiptoken` is the next `first` offset, and it advances by the rows Keycloak returned, not by the rows kept after filtering, so a dropped service account never pushes a person off a page. The default page size is 100, Graph's default. `$filter`, `$search`, `$orderby`, `$skip` and `$expand` are a `Validation` error (400); the contract sends none of them.
- **Mapping.** `displayName` is first name plus last name, falling back to the username and then the email. `userPrincipalName` is the username when it is an address; otherwise it is the email, because Graph's UPN is always an address and the client reads it as one.

Graph profile properties that Keycloak has no field for come from user attributes:

| Attribute | Graph property |
|---|---|
| `phoneNumber` (Keycloak's own, used by the `phone` scope) | `mobilePhone` |
| `nightware_job_title` | `jobTitle` |
| `nightware_department` | `department` |
| `nightware_company_name` | `companyName` |
| `nightware_office_location` | `officeLocation` |
| `nightware_business_phones` (multi-valued) | `businessPhones` |
| `nightware_aliases` (multi-valued) | `proxyAddresses` `smtp:` entries; `mail` is the `SMTP:` entry |

Keycloak 24+ silently drops attributes its realm user profile does not declare. A deployment that wants these properties filled must declare them, admin-editable, the way `deploy/keycloak/configure.sh` declares the mailboxSettings attributes. The dev realm does not declare them, so there these properties are null and `proxyAddresses` is just `SMTP:<mail>`.

## Mailbox preferences (`IUserPreferencesStore`, phase 5B)

`MailboxSettings/KeycloakUserPreferencesStore` finds the mailbox's user by exact email and maps the
attributes of ADR 0001 §29: `zoneinfo` (time zone), `locale` (language), `nightware_working_hours`
(Graph `workingHours` JSON; unreadable JSON counts as unset), `nightware_date_format`,
`nightware_time_format`. Writes change only the patched attributes (read-modify-write through the admin
client). No user with that email is `NotFound`.

The store does **no authorisation**: the service account can read and write every user. Callers must
first establish that the signed-in user may act on the mailbox — the mailbox-settings use cases ask the
mail store (with the caller's credentials) first, and allow time-zone writes only on the caller's own
mailbox.

## Health check

The `keycloak` check (tag `backend`) reports healthy when `GET {KeycloakBaseUrl}/realms/{realm}/.well-known/openid-configuration` returns 200 with an `issuer`. It does not compare the issuer with `KeycloakBaseUrl`.

## Packaging note

The project uses `<FrameworkReference Include="Microsoft.AspNetCore.App" />` for health checks, because `Microsoft.Extensions.Diagnostics.HealthChecks` is not in `Directory.Packages.props`. `Microsoft.Extensions.Http.Resilience` provides the retry and timeout pipeline.
