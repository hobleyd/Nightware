# Configuration reference

The façade reads `appsettings.json`, then `appsettings.<Environment>.json`, then environment
variables. Every key below can be set as an environment variable by replacing `:` with `__`
(`RateLimiting:TokenLimit` → `RateLimiting__TokenLimit`; list items by index,
`Cors:AllowedOrigins:0` → `Cors__AllowedOrigins__0`). Durations are `hh:mm:ss` or `d.hh:mm:ss`.

Every section is validated at start-up: a missing required value or a malformed one stops the
process with a message naming the key, instead of failing on the first request.

**Secrets** (marked 🔒) must come from the environment or a secret store, never a committed file.

## Identity and discovery

| Key | Default | Meaning |
|---|---|---|
| `Auth:Authority` | `https://nightware.localhost/auth/realms/nightware` | Public OIDC issuer; tokens' `iss` must match it (or `ValidIssuers`). |
| `Auth:MetadataAddress` | empty | Where to fetch OIDC metadata/keys when the public URL does not resolve inside the deployment, e.g. `http://keycloak:8080/auth/realms/nightware/.well-known/openid-configuration`. |
| `Auth:Audience` | `nightware-api` | Required `aud`. Empty disables the audience check — never do that in production. |
| `Auth:RequireHttpsMetadata` | `true` | `false` only for an internal `http://` metadata address. |
| `Auth:ValidIssuers:N` | none | Extra accepted issuers. |
| `Discovery:GraphBaseUrl` | `https://nightware.localhost/graph/v1.0` | Public v1.0 root, path prefix included (the compose stack serves the façade under `/graph`). Every `@odata.nextLink`/`deltaLink` and sealed upload/download URL is built on it; NightMail ignores links on any other scheme/host/port. Required. |
| `Discovery:GraphBetaBaseUrl` | `…/beta` | Public beta root. Required. |
| `Discovery:AuthorizationEndpoint` | Keycloak `…/auth` | Published to NightMail. Required. |
| `Discovery:TokenEndpoint` | Keycloak `…/token` | Published to NightMail. Required. Must be highly available (see README). |
| `Discovery:ClientId` | `nightmail` | The public client NightMail signs in with. |
| `Discovery:Scopes:N` | `openid email profile offline_access` | Requested scopes. |
| `DeltaLinks:SigningKey` 🔒 | empty (random per process outside Production) | The façade's root secret: HMAC key for delta/next links, and (through HKDF) the AES-GCM keys that seal callers' tokens into upload/download URLs. **Production refuses to start** without one of at least 32 bytes (`openssl rand -hex 32`). Must be identical on every instance and stable across restarts. Compose: `NIGHTWARE_DELTA_LINK_KEY`, required. Rotation: [deployment.md](deployment.md#the-façades-root-secret-nightware_delta_link_key). |
| `DeltaLinks:Lifetime` | `30.00:00:00` | How long a delta link is accepted; older ones get 410 and the client resyncs. |

## Backends

| Key | Default | Meaning |
|---|---|---|
| `Jmap:SessionUrl` | `https://nightware.localhost/.well-known/jmap` | Stalwart JMAP session resource; internal URL in compose (`http://stalwart:8080/jmap/session`). |
| `Jmap:UseSessionUrlOrigin` | `true` | Rewrite the session's advertised public URLs to the session URL's origin. |
| `Jmap:SessionCacheDuration` | `00:05:00` | Session reuse per credential. |
| `Jmap:RequestTimeout` / `AttemptTimeout` | `00:00:30` / `00:00:10` | Total and per-attempt time for a JMAP call. |
| `Jmap:BlobTimeout` | `00:05:00` | Upload/download time. |
| `Jmap:MaxRetryAttempts` | `2` | Retries for safe JMAP requests only. |
| `Jmap:MaxObjectsInGetFallback` / `MaxCallsInRequestFallback` | `500` / `16` | Used when the session omits its limits. |
| `Jmap:MasterUserSeparator` | `%` | Stalwart master-user login separator (service-account fallback). |
| `Dav:BaseUrl` | `https://nightware.localhost/cloud/` | Nextcloud root, path included (internal `http://nextcloud/` in compose, where Nextcloud's `/cloud` overwrite does not apply). |
| `Dav:DavRoot` / `OcsRoot` | `/remote.php/dav` / `/ocs/v2.php` | Paths under the base URL. |
| `Dav:AttemptTimeout` / `TotalTimeout` | `00:00:30` / `00:01:30` | Per attempt / with retries. |
| `Dav:MaxRetryAttempts` | `2` | GET/HEAD/OPTIONS/PROPFIND/REPORT only. |
| `Dav:DiscoveryCacheDuration` | `00:10:00` | Principal discovery cache. |
| `Dav:HealthCheckTimeout` | `00:00:05` | `/healthz` probe. |
| `Directory:KeycloakBaseUrl` | `https://nightware.localhost/auth` | Keycloak root including its relative path, not the realm URL (internal `http://keycloak:8080/auth` in compose). |
| `Directory:Realm` | `nightware` | Realm. |
| `Directory:ClientId` | `nightware-api` | Confidential client used for token exchange and the admin API. |
| `Directory:ClientSecret` 🔒 | empty | Its secret. Required. |
| `Directory:ServiceAccountScope` | `kc-admin-api` | Scope the service-account token requests; Keycloak 26.8+ rejects admin-API calls without it. |
| `Directory:RequestTimeout` | `00:00:10` | Per attempt (there is no total; see runbook). |
| `Directory:MaxRetryAttempts` / `RetryBaseDelay` | `2` / `00:00:00.200` | Safe calls only; token exchange is never retried. |
| `Directory:ServiceAccountTokenRefreshSkew` | `00:00:30` | Refresh margin. |
| `BackendCredentials:Mail:Strategy` | `TokenExchange` | `PassThrough`, `TokenExchange` or `ServiceAccount`. |
| `BackendCredentials:Mail:Audience` | `stalwart` | Exchange target. |
| `BackendCredentials:Groupware:Strategy` / `Audience` | `TokenExchange` / `nextcloud` | Same for Nextcloud. |
| `BackendCredentials:<Backend>:ServiceAccount:Mode` | `Basic` | `Basic` (master user) or `ClientCredentials`. |
| `BackendCredentials:<Backend>:ServiceAccount:Username` / `Password` 🔒 | none | Basic credential. |
| `BackendCredentials:Cache:Enabled` / `ExpirySkew` | `true` / `00:00:30` | Exchanged-token cache. |

Backend settings outside the façade that change its observable behaviour: Stalwart's
`MtaInboundThrottle` "sender domain to recipient" (25/hour per sender domain and recipient) is
scoped to unauthenticated inbound mail by `deploy/stalwart/configure.sh`; Nextcloud iMIP and external
senders remain subject to it — see the [runbook](runbook.md#mail-sent-but-never-delivered-stalwart-inbound-throttle).

## Hardening (phase 6)

| Key | Default | Meaning |
|---|---|---|
| `RateLimiting:Enabled` | `true` | Per-user limiter on `/v1.0` and `/beta`. Compose: `NIGHTWARE_RATE_LIMITING`. |
| `RateLimiting:TokenLimit` | `300` | Burst per user. |
| `RateLimiting:TokensPerPeriod` / `ReplenishmentPeriod` | `10` / `00:00:01` | Sustained rate (600/min per user). |
| `RateLimiting:ConcurrentRequestLimit` | `32` | In-flight requests per user; `0` disables. |
| `RequestTimeouts:Default` | `00:00:25` | Budget per request; overrun → 503 + `Retry-After` (reads) or 504 (writes). |
| `RequestTimeouts:Transfer` | `00:02:00` | Budget for attachments, `$value`, `content`, upload sessions, sendMail, and large bodies. |
| `RequestTimeouts:TransferBodyThresholdBytes` | `1000000` | A body larger than this gets the transfer budget on any route. |
| `Limits:MaxRequestBodyBytes` | `67666666` | Largest body: Stalwart's `maxSizeUpload` (50 MB) × 4/3 for base64 + 1 MB. Raise with Stalwart's limit. |
| `Kestrel:Limits:*` | see `appsettings.json` | Kestrel's own limits (`MaxRequestHeadersTotalSize` 32 KB, `MaxRequestHeaderCount` 100, `MaxRequestLineSize` 8 KB, `KeepAliveTimeout` 2 min, `RequestHeadersTimeout` 30 s). `Kestrel:AddServerHeader` defaults to `false`. |
| `ForwardedHeaders:KnownProxies:N` | none | Proxy IPs allowed to set `X-Forwarded-*`. |
| `ForwardedHeaders:KnownNetworks:N` | none | Proxy networks (CIDR). Both empty → loopback only. Compose: the stack subnet. |
| `ForwardedHeaders:ForwardLimit` | `1` | Proxy hops to unwind. |
| `Cors:AllowedOrigins:N` | none | Exact origins of the NightMail web build. None → no cross-origin access. Compose: `NIGHTMAIL_WEB_ORIGIN`. |
| `SecurityHeaders:HstsMaxAge` | `365.00:00:00` | HSTS on HTTPS requests; `00:00:00` disables. |
| `Telemetry:ServiceName` | `nightware-api` | OpenTelemetry `service.name`. |
| `Telemetry:Otlp:Endpoint` | empty (off) | OTLP collector; setting it turns on trace, metric and log export. Compose: `NIGHTWARE_OTLP_ENDPOINT`. |
| `Telemetry:Otlp:Protocol` | `Grpc` | `Grpc` (4317) or `HttpProtobuf` (4318). |
| `OTEL_EXPORTER_OTLP_HEADERS` 🔒 (env only) | none | Collector authentication headers, read by the exporter itself. |

## Host

| Key | Default | Meaning |
|---|---|---|
| `AllowedHosts` | `*` | Host-header filter. Set it to `graph.<domain>` in production (semicolon-separated list), so a request for any other host name is refused. |
| `ASPNETCORE_ENVIRONMENT` (env only) | `Production` | `Development` loads `appsettings.Development.json` (dev-only secrets, human-readable logs). |
| `ASPNETCORE_HTTP_PORTS` (env only) | `8080` (image) | Listening port inside the container. |

## Logging

| Key | Default | Meaning |
|---|---|---|
| `Logging:LogLevel:Default` | `Information` | |
| `Logging:LogLevel:Microsoft.AspNetCore` | `Warning` | |
| `Logging:LogLevel:System.Net.Http.HttpClient` | `Warning` | Per-backend-call lines; `Information` logs two per call. |
| `Logging:LogLevel:Polly` | `Warning` | Adapter resilience events; `Information` logs every attempt of every backend call. |
| `Logging:Console:FormatterName` | `json` (`simple` in Development) | JSON lines with scopes (`graph.request_id`, `graph.client_request_id`, trace/span ids). |

`Microsoft.AspNetCore.Hosting.Diagnostics` (the request-line log) is pinned to `Warning` in code
regardless of configuration, because URLs can carry credentials — see [security.md](security.md).

## Health endpoints

| Path | Meaning |
|---|---|
| `/healthz/live` | Liveness: the process answers. Never consults a backend. |
| `/healthz` | Readiness: every backend check (Stalwart session, Nextcloud DAV, Keycloak realm). The result is cached for 10 s and shared by all callers, so hitting it in a loop costs the backends at most one round of checks per 10 s. |

## Admin console (`Nightware.Admin`, ADR 0002)

The console reads the same way (`appsettings.json`, then environment variables). Compose sets every key
from `deploy/.env`.

| Key | Default | Meaning |
|---|---|---|
| `Admin:Authority` | — | Keycloak realm URL as tokens name it (`iss`). Required. |
| `Admin:MetadataAddress` | — | Discovery URL when it differs (inside compose: `http://keycloak:8080/auth/...`). |
| `Admin:RequireHttpsMetadata` | `true` | `false` only for the internal compose discovery URL. |
| `Admin:ClientId` / `Admin:ClientSecret` 🔒 | `nightware-admin` / — | The console's own confidential client. |
| `Admin:AcceptedAcrValues:0` | `mfa` | `acr` a sign-in must carry. Empty turns MFA off; refused in Production, warned about otherwise. |
| `Admin:IdleTimeout` / `Admin:AbsoluteLifetime` | `00:20:00` / `08:00:00` | Session limits. |
| `Admin:DestructiveActionMaxAge` | `00:05:00` | How recent a sign-in must be to delete a user's data. |
| `Admin:EntityAdminsMayDeleteData` | `false` | Lets entity admins delete data of their own entity's disabled users. |
| `Admin:ActionEmailLifespan` | `3.00:00:00` | Validity of a set-password e-mail link. |
| `Admin:AccessTokenLifespan` | `00:15:00` | Keep equal to the realm's `accessTokenLifespan`; the console tells the admin an offboarding is fully effective after this long. |
| `Admin:ScopeCacheLifetime` | `00:01:00` | How long an admin's rights, read from Keycloak, are reused. |
| `Directory:*` | as the façade | Keycloak admin API, bound to `nightware-admin`'s service account. |
| `StalwartManagement:ApiUrl` | — | Stalwart management (JMAP) endpoint, e.g. `http://stalwart:8080/jmap/`. |
| `StalwartManagement:ApiKey` 🔒 | — | The console account's API key (minted by `stalwart/configure.sh`). Or, for the recovery-admin fallback, `Username` + `Password` 🔒. Exactly one of the two. |
| `StalwartManagement:AttemptTimeout` / `MaxRetryAttempts` | `00:00:10` / `2` | Per-attempt time; retries for read-only calls. |
| `StalwartManagement:MaxAccountsScanned` / `AccountPageSize` | `10000` / `250` | Bound on the alias lookup's account scan. |
| `NextcloudProvisioning:BaseUrl` | — | Nextcloud base URL (compose: `http://nextcloud/`). |
| `NextcloudProvisioning:Username` / `Password` 🔒 | `nightware-admin` / — | The console's Nextcloud administrator. |
| `NextcloudProvisioning:OidcProviderId` | `keycloak` | user_oidc provider (identifier or numeric id) for pre-provisioning. |
| `NextcloudProvisioning:IdentityProviderBackends:0` | `user_oidc` | User backends that sign in through Keycloak. |

