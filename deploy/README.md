# Nightware deployment stack (dev)

A Docker Compose stack in which every backend trusts one OIDC issuer
(Keycloak). The Nightware Graph façade runs in front of it:

Everything is served on one host, `https://${PUBLIC_HOST}`, with a path per
backend:

| Public URL | Service | Image (linux/arm64 + linux/amd64 manifests verified) |
|---|---|---|
| `/auth/` | Keycloak, realm `nightware` (+ Postgres) | `quay.io/keycloak/keycloak:26.8.2`, `postgres:17.12-alpine` |
| `/.well-known/jmap`, `/jmap/` (and `/mail/` for the rest) | Stalwart: JMAP, WebDAV, management API | `stalwartlabs/stalwart:v1.1.0` |
| `/cloud/` | Nextcloud: CalDAV/CardDAV/WebDAV/OCS (+ Postgres, Redis, cron) | `nextcloud:35.0.3-apache`, `redis:8.6.8-alpine` |
| `/office/` | Collabora Online (Nextcloud Office) | `collabora/code:26.04.5.2.1` |
| `/standalone-signaling/`, and `${TALK_TURN_HOST}:3478` tcp+udp | Talk's High-Performance Backend (signaling, Janus, NATS) and TURN/STUN (eturnal) | `ghcr.io/nextcloud-releases/aio-talk:20261119_161514` |
| (internal only, `talk-recording:1234`) | Talk's call recording server (headless Firefox + ffmpeg) | `ghcr.io/nextcloud-releases/aio-talk-recording:20261119_161514` |
| `/graph/` and `/.well-known/nightware` | Nightware API (`api` profile) | built from `src/Nightware.Api/Dockerfile` |
| `/console/` | Nightware admin console (`api` profile) | built from `src/Nightware.Admin/Dockerfile` |
| (all of the above) | Caddy reverse proxy, TLS | `caddy:2.11.9-alpine` |

- `PUBLIC_HOST` defaults to `nightware.localhost`. Browsers and curl resolve
  `*.localhost` to 127.0.0.1, so no `/etc/hosts` entry is needed.
- `MAIL_DOMAIN` (default `nightware.localhost`) is the domain of every mail
  address (alice@, bob@, the rooms, Nextcloud's sender). It is independent of
  the host: a stack at `nightware.example.com` can serve `@example.org`.
- Stalwart cannot run under a path prefix (its router matches the first path
  segment), so its JMAP endpoints are at the host root and its session
  advertises `https://${PUBLIC_HOST}/jmap/...`. Everything else it serves
  (`/healthz`, WebDAV) is reachable under the stripped `/mail/` alias.
- `/` redirects to `/cloud/`; `/.well-known/caldav` and `/.well-known/carddav`
  redirect to `/cloud/remote.php/dav/`.
- `BASE_DOMAIN`, the old five-hostname setting (`auth.`/`mail.`/`cloud.`/
  `office.`/`graph.${BASE_DOMAIN}`), is deprecated. `scripts/init-env.sh`
  copies it into `PUBLIC_HOST` and `MAIL_DOMAIN` when those are missing, but a
  stack bootstrapped with the old layout needs fresh volumes (see Known
  limitations).
- `COMPOSE_PROJECT_NAME` (default `nightware`) names the containers, volumes
  and network. Every script, `make` target and the k6 runner read it from
  `.env`, so a second checkout with its own name and `DOCKER_SUBNET` keeps a
  separate stack.

### TLS

`CADDY_TLS` picks where the certificate comes from:

| `CADDY_TLS` | Certificate | Clients |
|---|---|---|
| `internal` (default) | Caddy's local CA | must trust `.certs/caddy-root.crt` (`bootstrap.sh` exports it; the scripts use it) |
| an e-mail address | Let's Encrypt (ACME); needs a public DNS name and inbound 443 or 80 | system trust |
| `"/certs/fullchain.crt /certs/privkey.key"` | your own files in `deploy/.certs` (mounted read-only into Caddy) | system trust |

With a publicly trusted certificate (either of the last two) also set
`COLLABORA_SSL_VERIFICATION=true`, `STALWART_ALLOW_INVALID_CERTS=false` and
`NIGHTWARE_SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt` (the façade
container's own bundle instead of Caddy's root).

`scripts/fetch-cert.sh` (`make cert`) copies a certificate chain and key from
another host over ssh: `CERT_SOURCE_HOST`, `CERT_SOURCE_CERT` and
`CERT_SOURCE_KEY` in `.env` say where from. A root-only key is read by a
throwaway `alpine` container on that host (removed again if the script pulled
it). It checks that the key matches, that the certificate covers
`PUBLIC_HOST` and is valid for 7 more days, only then replaces the files
(mode 600), and reloads a running Caddy. Rerun it after the source host
renews (docs/operations/runbook.md, "TLS certificate renewal").

The scripts reach `PUBLIC_HOST` through DNS. For a name that does not resolve
yet, set `NIGHTWARE_CONNECT_ADDRESS=127.0.0.1` in the environment: `curl
--resolve` in the scripts and a connect hook in the live integration tests
send its connections there.

Nextcloud apps installed by the bootstrap: calendar 6.6.5, contacts 8.10.0,
tasks 0.18.1, user_oidc 8.13.0, calendar_resource_management 1.0.0,
richdocuments 12.0.2.

## Bring-up

```bash
cd deploy
./scripts/init-env.sh                    # .env from .env.example, every secret random
                                         # (then set PUBLIC_HOST, MAIL_DOMAIN, CADDY_TLS ... as needed)
docker compose up -d --wait              # ~1-2 min until Keycloak + Nextcloud are healthy
./bootstrap.sh                           # one-time, idempotent (~1 min on a fresh volume)
./smoke.sh                               # exit status = number of failed checks
```

Every compose command needs `NIGHTWARE_DELTA_LINK_KEY` and the backend secrets in `.env`; the
compose file has no defaults for them. `scripts/init-env.sh` generates every one that is absent or
empty (hex, so nothing needs quoting) and is idempotent. It never replaces a value: the backends
store theirs when they are bootstrapped. An older `.env` still holding the passwords this repository
used to publish (`nightware-kc-admin` …) gets a warning only. That is acceptable on loopback; before
binding a stack to a network (`BIND_ADDRESS=0.0.0.0`) start it from fresh volumes with a fresh
`.env`. `bootstrap.sh` and the other scripts (through `scripts/lib.sh`), `make stack` and `make api`
run it for you.

`bootstrap.sh` runs, in order:

1. With `CADDY_TLS=internal`, exports Caddy's local root CA to
   `.certs/caddy-root.crt`. All scripts then use it with `curl --cacert`. Trust
   it in your OS or browser if you want to open the URLs without warnings. On
   macOS:
   `sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain .certs/caddy-root.crt`
2. `keycloak/configure.sh` declares the mailboxSettings attributes in the
   user profile (see "User → backend mapping"), makes `email` and `username`
   admin-editable only, and keeps the `UPDATE_EMAIL` required action disabled.
   It merges into the existing user profile and changes only what differs.
   It seeds the attributes for alice and bob through the `nightware-api`
   service account.
3. `stalwart/configure.sh` runs Stalwart's first-start Bootstrap through the
   API and restarts the container. It then pre-creates alice and bob, creates
   the Keycloak OIDC directory and makes it the default, sets the trusted
   network for iMIP, and finally reloads.
4. `nextcloud/configure.sh` installs the apps and runs the `occ` setup:
   user_oidc with bearer validation, Nextcloud Office on Collabora, mail
   through Stalwart, and rooms.
5. Has alice and bob sign in once on each backend, which provisions them.

The façade itself (`api` profile; only this container is built and replaced, the backends are
left alone):

```bash
docker compose --profile api up -d --build --no-deps nightware-api   # or: make api
```

It listens on 8080 inside its container, behind Caddy at `https://${PUBLIC_HOST}/graph` (Caddy strips
`/graph` and sends `X-Forwarded-Prefix: /graph`; links are built on `Discovery:GraphBaseUrl`,
`https://${PUBLIC_HOST}/graph/v1.0`), and serves `/.well-known/nightware` at the host root. It has a
liveness healthcheck on `/healthz/live`. `/healthz` is readiness (every backend, cached for 10 s). `smoke.sh` checks
`/healthz` and `GET /v1.0/me` with alice's token. Settings it takes from `.env`:

| `.env` | Façade setting | Default |
|---|---|---|
| `NIGHTWARE_ENVIRONMENT` | `ASPNETCORE_ENVIRONMENT` | `Development` (use `Production` outside dev) |
| `NIGHTWARE_DELTA_LINK_KEY` | `DeltaLinks:SigningKey`: the façade's root secret (delta links and sealed upload/download URLs) | **required**, no default; `scripts/init-env.sh` generates one |
| `NIGHTWARE_API_CLIENT_SECRET` | `Directory:ClientSecret` | required |
| `DOCKER_SUBNET` | `ForwardedHeaders:KnownNetworks:0` (only Caddy may set `X-Forwarded-*`) | `172.31.250.0/24` |
| `NIGHTWARE_SSL_CERT_FILE` | `SSL_CERT_FILE` (trust for calls via the public URL) | `/certs/caddy-root.crt`; `/etc/ssl/certs/ca-certificates.crt` with a publicly trusted certificate |
| `NIGHTMAIL_WEB_ORIGIN` | `Cors:AllowedOrigins:0` | `http://localhost:34572` |
| `NIGHTWARE_RATE_LIMITING` | `RateLimiting:Enabled` | `true` |
| `NIGHTWARE_OTLP_ENDPOINT` | `Telemetry:Otlp:Endpoint` | `http://grafana:4317` (the `grafana` service); empty turns export off |

Every other key, production deployment, monitoring, backups and the runbook are in
[`docs/operations/`](../docs/operations/README.md). Building images fills Docker's build cache;
reclaim it with `docker builder prune` (never prune volumes).

Tear down with `docker compose down`. Add `-v` to also delete
all data. A re-bootstrap from empty volumes is fully scripted.

## Test users

| Keycloak username | Password (`TEST_USER_PASSWORD`) | Email / Stalwart mailbox | Nextcloud uid | zoneinfo |
|---|---|---|---|---|
| `alice` | `Nightware-Dev-1!` | `alice@nightware.localhost` | `alice` | Australia/Sydney |
| `bob` | `Nightware-Dev-1!` | `bob@nightware.localhost` | `bob` | Europe/London |

Admin consoles (credentials are in `.env`):

- Keycloak: `https://nightware.localhost/auth/admin` (master realm `admin`).
- Stalwart WebUI: `https://nightware.localhost/admin/`. Type a Keycloak
  user's address (alice's, on a seeded stack) and Stalwart sends the sign-in to
  Keycloak: with a Keycloak session already open (the console, Grafana) it
  comes straight back, otherwise password + OTP, the console's MFA. An OTP
  counts for `ADMIN_MFA_MAX_AGE` (8 hours) across all three. Its code is
  built here with a patch (`stalwart/webui`): `/admin/login?login_hint=<address>`
  goes straight on, which is how the console's Stalwart link signs an admin in
  without typing anything. The client
  is Keycloak's `https://<host>/admin/oauth-client.json` (the web admin's own
  id, which `stalwart/configure.sh` pins and `keycloak/configure-console.sh`
  creates), and Caddy drops the `prompt=login` the web admin always sends for
  it. Administering needs Stalwart's Admin role on the account
  (`STALWART_ADMIN_ACCOUNTS`). The recovery admin (`STALWART_ADMIN_USER`)
  still signs in on Stalwart's own page. It and its sign-in page live at the
  host root, and its OAuth endpoints are a few exact paths under `/auth/`
  that Caddy sends to Stalwart rather than Keycloak (`caddy/Caddyfile`). The
  management API it drives is JMAP (`/jmap/`), which `stalwart/configure.sh`
  also uses.
- Nextcloud: `https://nightware.localhost/cloud`. Log in as `admin`, or use
  "Log in with Keycloak".

## Identity model

### Clients in realm `nightware`

| Client | Type | Purpose |
|---|---|---|
| `nightmail` | public | The NightMail app. Auth code + PKCE S256 (enforced), `response_mode=query`, no secret. Redirect URIs are exactly `nightmail://auth-callback` (macOS/iOS/Android), `http://localhost:34571` (Windows/Linux, one port only) and `${NIGHTMAIL_WEB_ORIGIN}/callback.html` (web, default `http://localhost:34572`). That origin is allowed for CORS on discovery and the token endpoint. The device flow is enabled (it also needs PKCE). An audience mapper adds `nightware-api`. |
| `nightware-api` | confidential | The façade. It is the resource server for NightMail tokens, which carry `aud: nightware-api`. It has **standard token exchange (RFC 8693, V2)** enabled, with audience mappers for `stalwart` and `nextcloud`. Its **service account** holds realm-management `view-users`, `query-users`, `manage-users` and `view-organizations` (the people typeahead's groups and shared mailboxes; `keycloak/configure.sh` grants it on an existing realm), plus the optional `kc-admin-api` scope. |
| `nightware-dev` | confidential | **Dev only.** Password grant, so scripts and integration tests can get a NightMail-shaped user token (`aud: nightware-api`) without a browser. |
| `stalwart` | audience only | Stalwart accepts only tokens with `aud: stalwart`. |
| `nextcloud` | confidential | user_oidc's provider client, used for browser login. Nextcloud requires `aud` to contain `nextcloud` on bearer requests. |
| `nightware-admin` | confidential | The admin console (see "Admin console"); created by `keycloak/configure-console.sh`. |
| `https://<host>/admin/oauth-client.json` | public | Stalwart's web admin (see "Admin consoles" above): PKCE, `aud: stalwart`, the console's step-up flow and minimum acr. |
| `nightware-grafana` | confidential | Grafana at `/grafana` (`NIGHTWARE_GRAFANA_CLIENT_SECRET`), created by `keycloak/configure-console.sh` with the console's step-up flow and minimum acr. A realm-role mapper puts `roles` in its ID token and userinfo; Grafana makes `nightware-global-admin` an Admin and refuses everyone else. |

### Token flow

NightMail's token is **not** accepted by Stalwart or Nextcloud: both return
401 to `aud: nightware-api`, and `smoke.sh` asserts this. The façade
exchanges the inbound user token once per backend and caches it until
`exp`:

```bash
curl -u nightware-api:$NIGHTWARE_API_CLIENT_SECRET \
  https://nightware.localhost/auth/realms/nightware/protocol/openid-connect/token \
  -d grant_type=urn:ietf:params:oauth:grant-type:token-exchange \
  -d subject_token=$USER_ACCESS_TOKEN \
  -d subject_token_type=urn:ietf:params:oauth:token-type:access_token \
  -d requested_token_type=urn:ietf:params:oauth:token-type:access_token \
  -d audience=stalwart            # or: audience=nextcloud
```

The exchanged token has `aud` set to exactly the requested client and `azp`
set to `nightware-api`. It keeps `sub`, `email`, `preferred_username` and
the profile claims. No refresh token is issued, so exchange again on expiry.
The subject token must contain `nightware-api` in `aud`, which every
`nightmail` and `nightware-dev` token does.

### Access-token claims (NightMail, auth code + PKCE)

| Claim | Value |
|---|---|
| `iss` | `https://nightware.localhost/auth/realms/nightware` (fixed by `KC_HOSTNAME`; the same inside and outside Docker) |
| `aud` | `["nightware-api","account"]` (`account` comes from Keycloak's default roles) |
| `azp` | `nightmail` |
| `typ` | `Bearer` (header `typ: JWT`, RS256) |
| `scope` | `openid email profile [offline_access]` |
| claims | `sub` (UUID), `email`, `email_verified`, `preferred_username`, `name`, `given_name`, `family_name`, `zoneinfo`, `locale` |
| not present | `nonce`, `at_hash` |

Lifetime is 15 minutes (`accessTokenLifespan` 900). Refresh with
`grant_type=refresh_token&scope=openid email profile offline_access`.
`smoke.sh` verifies this.

### User → backend mapping

- **Stalwart account = `email` claim** (`claimUsername: email`). For example,
  alice's JMAP `username` is `alice@nightware.localhost`. The email is the
  identity, so only admins can change it: the realm user profile gives
  `email` and `username` `edit: ["admin"]`, and a user's change through the
  account console or account REST API is refused (`keycloak/configure.sh`).
- **Nextcloud uid = `preferred_username`** (user_oidc `--unique-uid=0
  --mapping-uid=preferred_username`). The uid for alice is `alice`, so her
  DAV paths are `/remote.php/dav/.../alice/`.
- **Keycloak user lookups** (`/users`, `/users/{id}`) use the `nightware-api`
  service account. Get its token with
  `grant_type=client_credentials&scope=kc-admin-api` (Basic
  `nightware-api:<secret>`). Then call
  `https://${PUBLIC_HOST}/auth/admin/realms/nightware/users?email=...`, adding
  `exact=true` and `briefRepresentation` as needed. Both the Caddy URL and
  `http://keycloak:8080/auth` work.
  > **Without `scope=kc-admin-api` the admin API answers 401**, even though
  > the token's `aud` includes `realm-management` and it carries
  > `view-users`/`query-users`/`manage-users`. Keycloak 26.8 accepts
  > admin-API calls only from tokens that carry its new `kc-admin-api` client
  > scope, whose `oidc-admin-api-audience-mapper` adds the admin-API audience.
  > It is an *optional* scope on `nightware-api`, so exchanged user tokens
  > never get it. `smoke.sh` calls `GET /admin/realms/nightware/users?max=1`
  > with this token.
- **mailboxSettings** is backed by Keycloak user attributes. All of them are
  declared in the realm's user profile by `keycloak/configure.sh`, because
  Keycloak 26 silently drops undeclared attributes. Users can view them but
  not edit them; only admins and the service account (`manage-users`) can:

  | Attribute | Graph property | Example (alice) |
  |---|---|---|
  | `zoneinfo` | `timeZone` (IANA name) | `Australia/Sydney` |
  | `locale` | `language.locale` | `en` |
  | `nightware_working_hours` | `workingHours` (JSON string) | `{"daysOfWeek":["monday",…],"startTime":"09:00:00.0000000","endTime":"17:30:00.0000000","timeZone":{"name":"Australia/Sydney"}}` |
  | `nightware_date_format` | `dateFormat` | `dd/MM/yyyy` |
  | `nightware_time_format` | `timeFormat` | `h:mm tt` |

  To write them, `GET /admin/realms/nightware/users/{id}`, change
  `attributes` (every value is an array of strings), and `PUT` the whole
  representation back. `zoneinfo` and `locale` also appear as token claims
  after the next refresh.

## Admin console

`https://${PUBLIC_HOST}/console/` (the `api` profile, service `nightware-admin`)
manages **entities** (organisations owning mail domains), their **users** and
those users' accounts in Keycloak, Stalwart and Nextcloud, so nobody has to sign
in to the three backends to set someone up. Design: `docs/adr/0002-admin-console.md`.

```bash
./scripts/init-env.sh                       # the console's Keycloak client secret and Nextcloud password
./keycloak/configure-console.sh             # client, roles, MFA flow, default entity (bootstrap.sh runs it)
./stalwart/configure.sh                     # the console's Stalwart account and its API key (-> .env)
./nextcloud/configure.sh                    # the console's Nextcloud account
docker compose --profile api up -d --build nightware-admin
```

All three scripts are idempotent and were run end to end against an isolated
stack (Keycloak 26.8.2, Stalwart 1.1.0, Nextcloud 35); they are safe to re-run
on a live realm (nothing needs a re-import or fresh volumes). Read their
output: anything they cannot do is printed as a WARNING.

- **Who can sign in.** Global administrators (realm role
  `nightware-global-admin`; the first ones from `ADMIN_BOOTSTRAP_USERS`, alice
  on a seeded dev stack) and entity administrators (members of an entity's
  `admins` organization group). Sign-in needs a second factor: the console has
  its own step-up flow (password, then a passkey or OTP for `acr=mfa`), and a
  first sign-in with neither asks to set up an authenticator app. "Add a
  passkey" in the console's header registers one (Face ID, Touch ID, Windows
  Hello, a security key) and makes it the default; the code stays the
  fallback. `ADMIN_REQUIRED_ACR=` (empty) turns MFA off for a
  loopback dev stack only; Production refuses to start without it.
- **NightMail's sign-in is unchanged.** Turning Organizations on would make
  Keycloak's login page identity-first (username, then password on a second
  page); `configure-console.sh` disables that step in the realm's browser flow.
- **Linking, not recreating.** The new-user wizard looks each person up by
  their keys first (Keycloak e-mail and username, the Stalwart address, the
  Nextcloud uid), shows what will be **created** and what **linked**, and stops
  on a **conflict** (an account with the key that disagrees) instead of merging.
  Linking never changes an existing person's password. Existing users with an
  `@MAIL_DOMAIN` address are already members of the default entity
  (`NIGHTWARE_DEFAULT_ENTITY_NAME`); **Repair** creates only what is missing.
- **Off-limits accounts.** The console never links, resets or deletes
  administrators: Keycloak admins and service accounts, privileged Stalwart
  accounts (its own included) and Nextcloud administrators. Reserved addresses
  (`postmaster@`, `noreply@`, `nightware-admin@`, …) are never given to a person.
- **First password.** A generated one-time password shown once (changed at
  first sign-in), or Keycloak's set-password e-mail to the new address (the realm
  sends through Stalwart when it has no SMTP of its own).
- **Resetting a password.** On a user's page, **Password** generates a new
  one-time password (shown once, changed at next sign-in) or e-mails a
  set-password link. Not for offboarded users, administrators outside your
  scope, Keycloak administrators, or yourself.
- **Your own password.** **Change password** in the console header sends you to
  Keycloak's own change-password screen (`kc_action=UPDATE_PASSWORD`) and back;
  the console never sees the password. Locked out entirely: another global
  administrator resets it from your user page.
- **Offboarding** disables Keycloak sign-in and ends sessions, blocks every
  sign-in to the mailbox (app passwords and API keys made in Stalwart included;
  mail is still delivered and kept) and disables the Nextcloud account. An
  offboarded administrator loses the console within a minute. Deleting mail or
  Nextcloud data is a separate action for a disabled user, global administrators
  only (`ADMIN_ENTITY_ADMINS_MAY_DELETE_DATA=true` extends it to entity admins),
  after a fresh MFA sign-in and retyping the address.
- **New domains.** Adding a domain to an entity creates it in Stalwart with DKIM
  keys; the entity's *DNS records* page lists the MX, SPF, DKIM, DMARC, SRV and
  CNAME records to publish. Stalwart rejects reserved TLDs such as `.example`.
  SPF and DMARC are Stalwart's unless a global administrator sets the domain's
  own on that page (kept as Keycloak organization attributes); start DMARC at
  `p=none` while other services still send as the domain.
- **A new domain's public mail discovery is not automatic.** The console
  publishes `mta-sts`, `autoconfig`, `autodiscover` and `ua-auto-config` CNAMEs,
  an `_mta-sts` TXT and `mail.<domain>` for every domain. The production proxy
  only serves the names it lists, though, and Stalwart serves **one** MTA-STS
  policy for all domains. So for each domain added to production:
  1. Add its names to `conf.d/mail-discovery.conf` in the ansible repo
     (`roles/docker_apps/files/config/proxy/nginx/`). Copy the bitcheeky.com
     server blocks. Until then those names get the proxy's default 403, and
     senders report "no policy found".
  2. Make sure the proxy's certificate covers `*.<domain>`
     (`proxy/letsencrypt/renew.sh` in the ansible repo, which issues it
     through Cloudflare DNS). Stalwart's STARTTLS uses the same certificate
     file, so `mail.<domain>` needs it too.
  3. Make Stalwart's MTA-STS policy list the domain's MX. With `x:MtaSts`
     `mxHosts` empty (the default), the policy's only `mx:` is Stalwart's
     hostname, `mail.<MAIL_DOMAIN>`. Add `mail.<domain>` to `mxHosts`, or point
     the domain's MX at that hostname. A missing MX is harmless while `mode`
     is `testing` (failures only show in TLS reports), but under `enforce`
     senders refuse to deliver.
  4. Run `scripts/verify-mail-discovery.sh <domain>` in the ansible repo.
  DMARC and TLS reports need nothing per domain: the console publishes
  `rua=mailto:postmaster@<domain>`, Stalwart analyses `postmaster@*`, and the
  console's DMARC reports page lists every entity domain.
- **DNS hosting.** A global administrator can connect an entity's DNS host on
  its DNS hosting page: Cloudflare (an API token from the "Edit zone DNS"
  template), DigitalOcean, Gandi, Hetzner, Amazon Route 53 (an access key as
  `KEYID:SECRET`), Azure DNS (a service principal and the zones' resource group)
  or Google Cloud DNS (a service account's JSON key), kept sealed. Each DNS page then lists what publishing would
  add or correct in the zone and publishes it. Needs
  `NIGHTWARE_SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt` so the console
  trusts the host's certificate.
- **Its credentials** are its own:
  - Keycloak client `nightware-admin` (`NIGHTWARE_ADMIN_CLIENT_SECRET`); its
    service account holds `manage-users` and friends, the organization roles,
    `query-clients` and the read-only `view-realm` (needed to read and list the
    global-admin role), never `view-clients` (which would expose client secrets).
  - Stalwart account `nightware-admin@MAIL_DOMAIN` with a custom role extending
    `User` (no impersonation, no domain deletion). Stalwart refuses passwords on
    internal accounts while Keycloak is the directory, so `stalwart/configure.sh`
    mints it an **API key usable only from the console's address**
    (`STALWART_MANAGEMENT_API_KEY`, `_ID`), and on re-minting destroys older
    keys. Fallback, full access: `STALWART_MANAGEMENT_LOGIN=admin` and
    `STALWART_MANAGEMENT_PASSWORD` = the recovery admin's, with the key empty.
  - Nextcloud local administrator `nightware-admin`
    (`NEXTCLOUD_PROVISIONING_PASSWORD`; Nextcloud has no narrower role).
- **Reach.** Caddy admits `ADMIN_ALLOWED_CIDRS` (default anywhere). The console
  shares its origin with Nextcloud, Stalwart's UI and Keycloak (see
  `docs/known-issues.md`); narrow `ADMIN_ALLOWED_CIDRS` to a management network
  or VPN if you have one.

## Backend facts for adapter developers

### Stalwart (JMAP)

- Session: `GET https://nightware.localhost/.well-known/jmap` returns
  `307` to `/jmap/session`. Inside Docker, `http://stalwart:8080/jmap/session`
  also works without TLS. Health: `https://nightware.localhost/mail/healthz/live`.
- The session's `apiUrl`, `downloadUrl`, `uploadUrl` and `eventSourceUrl`
  are **public** URLs (`https://nightware.localhost/jmap/...`, from
  `STALWART_PUBLIC_URL`, the host root). A façade container reaches them
  because `PUBLIC_HOST` is a network alias of Caddy; by default it rewrites
  them to the session URL's origin, `http://stalwart:8080`
  (`Jmap:UseSessionUrlOrigin`).
- Account ids are short, opaque and per-install (alice is `c`, bob is `d` on
  a fresh volume). Always read them from
  `primaryAccounts["urn:ietf:params:jmap:mail"]`.
- Capabilities include mail, submission, vacationresponse, sieve, blob,
  quota, calendars, contacts, principals(+availability), filenode, metadata
  and websocket. See `recordings/jmap/01-session.response.json`.
- Default mailboxes are named `Inbox`, `Drafts`, `Sent Items`,
  `Junk Mail` and `Deleted Items`, with the matching JMAP roles.
- Sending works through JMAP `EmailSubmission/set`. The account needs an
  `Identity`; none exists until one is created
  (`Identity/set {email: <address>}`). `scripts/record.sh` shows the full
  sequence.
- Mail ports on the host: SMTP `127.0.0.1:2525` (25), submission
  `127.0.0.1:1465` (465, implicit TLS, self-signed) and IMAPS
  `127.0.0.1:1993` (993). **There is no port 587**, because Stalwart v1's
  default listeners don't include it. Keycloak users authenticate with
  `XOAUTH2`/`OAUTHBEARER` and an exchanged `aud: stalwart` token
  (`user=alice@nightware.localhost^Aauth=Bearer <token>^A^A`). This is
  verified: SMTP on 465 answered `235 2.7.0 Authentication succeeded` and
  then sent mail alice→bob, and IMAP `AUTHENTICATE XOAUTH2` plus
  `SELECT INBOX` succeeded. Users have no passwords.
- DKIM keys (ed25519 and RSA) are generated for the domain. No DNS is
  published, so SPF and DMARC evaluate to `none`.

### Nextcloud (DAV)

| What | URL (relative to `https://nightware.localhost/cloud`, or `http://nextcloud` inside Docker) |
|---|---|
| DAV root | `/remote.php/dav/` (`/.well-known/caldav` and `/.well-known/carddav` redirect here) |
| User principal | `/remote.php/dav/principals/users/{uid}/` |
| Calendar home | `/remote.php/dav/calendars/{uid}/`; the default calendar is `personal/` |
| Scheduling inbox / outbox | `/remote.php/dav/calendars/{uid}/inbox/`, `.../outbox/` (POST VFREEBUSY for free/busy) |
| Address book home | `/remote.php/dav/addressbooks/users/{uid}/`; the default book is `contacts/` |
| Files | `/remote.php/dav/files/{uid}/` |
| Room principals | `/remote.php/dav/principals/calendar-rooms/calendar_resource_management-{room-uid}/` |

- `calendar-user-address-set` for users is `mailto:{email}`.
- Rooms created by the bootstrap:

  | Room | Principal suffix | Address | Type | Capacity | Features |
  |---|---|---|---|---|---|
  | Boardroom | `calendar_resource_management-nightware-boardroom` | `boardroom@nightware.localhost` | Meeting room | 12 | VIDEO-CONFERENCING, TV, WHITEBOARD, WHEELCHAIR-ACCESSIBLE |
  | Focus Room 1 | `calendar_resource_management-nightware-focus-1` | `focus1@nightware.localhost` | Phone booth | 2 | PHONE |

  Both are in the building "Nightware HQ", Level 1, at 1 Example Street,
  Sydney NSW 2000.
- Nextcloud's public URL overwrites (`overwritehost`, `overwriteprotocol`,
  `overwritewebroot=/cloud`) apply only to requests from Caddy's pinned address
  (`CADDY_ADDRESS`, `overwritecondaddr` in `nextcloud/configure.sh`). A request
  through Caddy gets hrefs under `/cloud/remote.php/dav/`; the façade's direct
  `http://nextcloud/` calls get `/remote.php/dav/`, which is what its
  `Dav:BaseUrl` expects. Widening the condition (or dropping it) makes every
  internal href carry `/cloud` and the façade rejects them as outside its DAV
  root.
- Room metadata uses the namespace `http://nextcloud.com/ns`, in the
  properties `room-type`, `room-seating-capacity`, `room-building-address`
  and `room-features`. Note that this is `.com`, while Nextcloud's other
  properties use `http://nextcloud.org/ns`.
- `principal-property-search` on `/remote.php/dav/principals/calendar-rooms/`,
  matching on `displayname`, finds rooms.
- Rooms **auto-schedule**. Adding a room as `ATTENDEE;CUTYPE=ROOM` to an
  event gets `PARTSTAT=ACCEPTED` when the room is free and `DECLINED` when it
  is busy. The busy time shows in an outbox free/busy POST.
- Room principals come from calendar_resource_management through a DAV
  background job (`UpdateCalendarResourcesRoomsBackgroundJob`, normally
  hourly). `nextcloud/configure.sh` runs it straight away. Rooms added later
  appear after the next cron run, or after
  `occ background-job:execute <id> --force-execute`.
- Bearer validation runs in user_oidc's self-encoded JWT validator. It checks
  the signature against Keycloak's JWKS, the `iss` against discovery, and that
  `aud` contains `nextcloud`. Unknown users are provisioned on their first
  bearer request (`--bearer-provisioning=1`).

### Documents (Collabora)

Nextcloud Office (`richdocuments`) edits documents in the browser through the
`collabora` service, and the façade asks the same Collabora for PDFs of Office
files when NightMail previews a Nextcloud link (Phase 7). Collabora serves
under `/office` (`net.service_root`), on its internal listener too. Three URLs
are involved, because `*.localhost` names do not work between containers:

| Who | Calls | Configured by |
|---|---|---|
| Nextcloud (discovery, server-side) | `http://collabora:9980/office` | `richdocuments:setup --wopi-url` |
| The browser (editor iframe) | `https://${PUBLIC_HOST}/office` | Collabora `server_name` + service root; discovery's `urlsrc` carries it |
| Collabora (WOPI callbacks) | `http://nextcloud` | `richdocuments:setup --callback-url`; Collabora's `aliasgroup1` allows only this host |
| The façade (`convert-to/pdf`) | `http://collabora:9980/office/` (`Collabora__BaseUrl`); `http://127.0.0.1:9980/office/` from the host | Collabora's default `net.post_allow` (private addresses) |

- Nextcloud accepts WOPI requests only from the compose subnet
  (`wopi_allowlist = ${DOCKER_SUBNET}`), which is where Collabora's callbacks
  come from.
- Caddy answers `403` for `/office/cool/convert-to` — through the proxy
  every client would arrive from Caddy's private address and pass `post_allow`.
- Collabora also fetches richdocuments' settings and templates on the public
  `/cloud` URL. It does not trust Caddy's local CA, so its outgoing TLS
  verification is **off** by default (`COLLABORA_SSL_VERIFICATION=false`);
  turn it on with a publicly trusted certificate (ACME or certificate files).
  With verification on and an untrusted certificate the editor closes with
  `configfailed` right after loading.
- `occ richdocuments:activate-config` resets the callback URL to autodetect
  (`wopi_callback_url` empty), after which Collabora is told to call back on
  the public URL, outside its alias group. Rerun `nextcloud/configure.sh`
  afterwards.
- The image has no shell or curl, so there is no compose healthcheck; the
  image's own reports unhealthy. `nextcloud/configure.sh` waits for discovery.
- **The façade is exempt from Nextcloud's brute-force throttling.** It reads
  `/s/{token}` links anonymously from its own address, so without this a few
  password-protected or dead links (any users, any links) would make Nextcloud
  delay, then refuse, every public share (`MaxDelayReached`). The api container
  has a pinned address (`NIGHTWARE_API_ADDRESS`, default `172.31.250.250`,
  which must sit inside `DOCKER_SUBNET`), and `nextcloud/configure.sh`
  allow-lists exactly that `/32` as `bruteForce` `whitelist_1000`. Never widen
  it to the subnet: Caddy is on it and carries every browser's sign-in
  attempts. Check with `occ security:bruteforce:attempts 172.31.250.250`
  (`bypass-listed: true`). The cost: nothing slows a caller guessing share
  tokens or passwords *through the façade*; it already needs a Keycloak
  sign-in to do so, and the façade's per-user rate limit bounds it.
  Share *creation* is also rate-limited per user (OCS 429), which
  only matters to repeated live-test runs.
- Port 9980 is published on `COLLABORA_BIND_ADDRESS` (default `127.0.0.1`,
  `COLLABORA_PORT`) so a façade run on the host (`dotnet run`, the live
  integration tests) can convert. Not on `BIND_ADDRESS`: on a LAN interface it
  would offer `convert-to` to every client there, around Caddy's 403.
- Verified (2026-11-15, CODE 26.04.5.2 on arm64): alice's `.docx` opened
  through richdocuments' direct-editing session, loaded in Collabora over
  `wss://nightware.localhost/office` (WOPI CheckFileInfo/GetFile as alice,
  `perm: edit`), was typed into and saved back (PutFile) — the file in
  Nextcloud changed size and contains the typed text.

### Online meetings (Talk)

An event created with `isOnlineMeeting` gets a Nextcloud Talk room (phase 22,
`docs/phases/phase-22-online-meetings.md`). Calls go through the `talk`
service, Nextcloud AIO's bundle of Talk's High-Performance Backend and a TURN
server, and calls are recorded by the `talk-recording` service;
`nextcloud/configure.sh` registers all three with Talk on every run, and
replaces Talk's default public STUN server with ours.

| Who | Calls | Configured by |
|---|---|---|
| Browsers and Talk apps (signaling WebSocket) | `wss://${PUBLIC_HOST}/standalone-signaling/spreed` | `talk:signaling:add`; Caddy strips the prefix |
| Nextcloud (room notifications, signed) | `https://${PUBLIC_HOST}/standalone-signaling/api/v1/room/…`, through Caddy's alias | the same; the shared `TALK_SIGNALING_SECRET` |
| The signaling server (ticket checks) | the Nextcloud URL the client names, e.g. `https://${PUBLIC_HOST}/cloud/…` | `NC_DOMAIN`: its backend list |
| Clients and Janus (media) | TURN at `${TALK_TURN_HOST}:3478` (Janus: `talk:3478`, its own container) | `talk:turn:add` / `talk:stun:add`; `TALK_TURN_SECRET` |
| Nextcloud (start/stop a recording, signed) | `TALK_RECORDING_URL` (`http://talk-recording:1234`) | app config `spreed recording_servers` (Talk has no occ command for it); the shared `TALK_RECORDING_SECRET` |
| The recording server (status, upload) and its Firefox (the call page) | `https://${PUBLIC_HOST}/cloud`, through Caddy's alias | its `NC_DOMAIN`; it joins the call on the signaling server as an internal client (`TALK_INTERNAL_SECRET`) |
| Nextcloud (a recording's transcript and summary) | `${NEXTCLOUD_AI_TRANSCRIPTION_URL:-$NEXTCLOUD_AI_URL}/audio/transcriptions`, and `${NEXTCLOUD_AI_URL}/chat/completions` | integration_openai, `NEXTCLOUD_AI_*` |

- Talk signs each room notification with the URL Nextcloud believes it has:
  the public one for requests through Caddy, `http://nextcloud/` for the
  façade's direct calls (`overwritecondaddr`). Both are in the signaling
  server's backend list (`NC_DOMAIN: …, http://nextcloud`); without the second,
  every room the façade opens fails with "Authentication check failed".
- Media only ever uses TURN on 3478: the clients and Janus all allocate relays
  on the same eturnal, which forwards between them inside the container. So
  3478 tcp+udp is the one port to forward; no UDP range is published.
- Janus reaches eturnal by the container's own name (`TURN_DOMAIN: talk`),
  never 127.0.0.1: its sockets are bound to the container's address, and the
  kernel drops eturnal's loopback-sourced replies to that address. With
  127.0.0.1 Janus got no relay, and calls connected only after a failed first
  attempt (seen live 2026-11-23).
- Docker Desktop and colima on macOS don't forward UDP from the host reliably:
  on this stack TURN over TCP works from the Mac, UDP only from containers.
- **`PUBLIC_HOST` must not end in `.localhost` for Talk.** curl resolves
  `*.localhost` to loopback without asking DNS, so inside the containers
  Nextcloud can't reach the signaling server: creating a room fails with a
  500 (and the recorder, which calls back the same way, would fail too). Use another name (say
  `nightware.test`) with `NIGHTWARE_CONNECT_ADDRESS=127.0.0.1` for the host's
  scripts.
- **Recording.** A call's moderators can start a recording. The recorder
  opens `${PUBLIC_HOST}/cloud/index.php/call/{token}/recording`
  in Firefox, captures it at 1920x1080 with ffmpeg (VP8/Opus `.webm`, CPU
  encoding) into the `talk-recordings` volume, uploads it to the starter's
  `Talk/Recording/{token}/` folder, and Talk notifies them ("Call recording
  now available") to share it in the chat. It matches Talk's requests by
  the exact backend URL, so `NC_DOMAIN` carries `/cloud`. Each concurrent
  recording runs its own Firefox and ffmpeg encode, so size the host for
  them (`shm_size: 2g` is Firefox's). Recording consent is not required
  (Talk's default; app config `spreed recording_consent`).
- **Recordings belong to the conversation (`nightware_talk`).** Talk leaves
  a recording, its transcript and summary in the recorder's files and only
  offers "Share to chat" in the recorder's notification. Nightware's own
  Nextcloud app, `nextcloud/apps/nightware_talk`, makes that share itself: when
  a recording (`.ogg`, `.webm`, any format Talk accepts), a transcript (`.md`)
  or a summary (`… - summary.md`) lands directly in a user's
  `{recording folder}/{token}/` (Talk's own setting, `Talk/Recording` by
  default) and the user takes part in that conversation, it creates a
  read-only room share of the file with the conversation, once. Talk posts it
  into the chat as the recorder (audio under "Audio", video under "Media",
  transcripts under "Files" in the conversation's shared items), and every
  participant has it at `Talk/{name}`, as with any file shared there.
  It reacts to Nextcloud's file events (created, written, renamed), which
  cover the recording server's chunked upload (through a temporary public
  link), its direct upload and the transcription's background job; the share
  is posted as the owner even there (no one is signed in), through the active
  user and Talk's session override for its system messages. A failure is
  logged (app `nightware_talk`), never raised into the upload.
  `docker-compose.yml` mounts the app read-only into `custom_apps` of both
  `nextcloud` and `nextcloud-cron` (transcripts are written by cron), and
  `configure.sh` enables it. Its path rules are tested with plain PHP:
  `nextcloud/apps/nightware_talk/tests/run.sh` (runs them in the
  `nextcloud:35.0.3-apache` image; `NIGHTWARE_TALK_PHP=local` uses the host's
  `php`). The recorder's notification still offers "Share to chat"; once the
  app has shared the file, Talk answers it with an error ("Already shared")
  and nothing else happens: dismiss it.
- Verified (2026-11-23, Talk 25.0.8, recording server v0.3.1): a real audio
  recording by alice (chunked upload through the public link: created and
  written events, both with the full file) and a transcript and summary
  written from the CLI as the transcription writes them were each shared
  once, posted as alice, and bob read them at `Talk/Recording … .ogg`, with
  the same file ids as alice's.
- **Audio-only recordings.** `TALK_RECORDING_AUDIO_ONLY=true` makes every
  recording an Opus `.ogg` (at most ~45 MB an hour, libopus's default 96 kbit/s stereo, against ~400 MB of
  video), which is all a transcript needs. Talk's API can ask for audio only
  (status 2), but its web client always asks for video, so
  `talk-recording/run.sh` (the service's command, after the image's
  `/start.sh` has written `/conf/recording.conf`) rewrites the video settings
  instead: the screen is grabbed at 1 frame a second, not encoded (`-vn`), and
  the audio is always Opus in `.ogg`. (Where the image finds a GPU encoder it
  sets AAC in `.m4a` for audio, which Talk's upload refuses; its allowed
  formats are ogg, ogv, mp4, webm and mkv.) Talk still says "video recording" in the
  chat. Firefox still renders the call, so the saving is the encode and the
  file, not the browser.
- **Transcripts.** With `NEXTCLOUD_AI_URL` or `NEXTCLOUD_AI_TRANSCRIPTION_URL`
  set, `configure.sh` enables `integration_openai` as Nextcloud's
  speech-to-text provider and turns on Talk's `call_recording_transcription`.
  Once a recording is stored, Talk queues a `core:audio2text` task.
  Nextcloud's cron (every 5 minutes) runs it, sending the whole file to
  `audio/transcriptions` with `NEXTCLOUD_AI_TRANSCRIPTION_MODEL`. The
  transcript is saved beside the video as `Recording … .md` (with "Transcript
  is AI generated and may contain mistakes"), and Talk notifies the starter
  ("Transcript now available", or "Failed to transcript call recording").
  With `NEXTCLOUD_AI_SUMMARY_MODEL` it also summarises through
  `chat/completions` on `NEXTCLOUD_AI_URL`. Image, speech and translation
  providers stay off, and so does the `core:text2speech` task type, which the
  app registers regardless. Without either URL the app is disabled: its
  built-in URL is OpenAI's.
- **The transcription server must take webm and Ogg**, since Talk sends the
  recording as stored and `integration_openai` labels it `file.mp3`. OpenAI
  and Whisper servers do (speaches 0.8.3 transcribed the webm correctly).
  Ollama 0.40.17 with `lfm3-audio` accepted only WAV: webm, Ogg, MP3, FLAC
  and M4A were refused. Use Ollama for summaries only.
- Transcripts verified (2026-11-23, integration_openai 5.0.0, speaches
  0.8.3-cpu with `Systran/faster-whisper-small`): a webm of synthesised
  speech uploaded as a recording through the recorder's own client was
  transcribed word for word (bar a name), saved as `.md` beside it, and
  notified. Whisper over a real call's audio is untested, as is the size at
  which a recording no longer fits through PHP (`getContent()` loads the
  whole file).
- Verified (2026-11-22, aio-talk 20261119_161514, signaling 2.2.0, Janus
  1.4.3, on arm64): Nextcloud's notifications accepted from both URLs; a
  signaling `hello` with alice's Talk ticket answered as alice (a forged ticket
  refused); a TURN Allocate with Talk's short-lived credentials relayed (a
  wrong password refused, 401). No browser call was placed.
- Recording verified (2026-11-23, aio-talk-recording 20261119_161514,
  recording server v0.3.1, `PUBLIC_HOST=nightware.test`): alice joined a
  call over OCS and started a video recording. It reached "recording" (1),
  and once stopped a 14 s 1920x1080 VP8/Opus webm of the call page was in
  her `Talk/Recording/{token}/`, with the notification. Nobody in that call
  sent audio or video, so media reaching the recorder is unverified. From
  inside the network `PUBLIC_HOST` resolves to Caddy, not TURN, so the
  recorder's Firefox can only reach Janus directly on its compose-network
  address.
- Audio-only verified (2026-11-23, same stack): with
  `TALK_RECORDING_AUDIO_ONLY=true` the same OCS video recording uploaded a
  15.8 s `.ogg` holding one Opus stream and no video, which Talk accepted and
  queued for transcription (speaches returned an empty transcript of the
  silent call). An Opus `.ogg` of synthesised speech, sent as integration_openai
  sends it, came back word for word from
  `deepdml/faster-whisper-large-v3-turbo-ct2`.

### iMIP (meeting invitations by mail)

- **Nextcloud sends with Stalwart SMTP, signed in:** `mail_smtphost=stalwart`,
  port 25, STARTTLS with Stalwart's certificate (peer verification off), as
  Stalwart's internal account `__calendar__@<MAIL_DOMAIN>`
  (`NEXTCLOUD_MAIL_USER`; the console reserves the name). The envelope and
  `From:` are that address ("Alice Anderson via Nextcloud"), and `Reply-To:` is
  the organizer.
- **Why XOAUTH2 with an API key:** with Keycloak as Stalwart's default
  directory an internal account can't hold a password, and SMTP PLAIN/LOGIN
  with an API key fail (`Unsupported credentials type for OIDC backend`). XOAUTH2
  with the key as the bearer token succeeds. `stalwart/configure.sh` creates
  the account, mints its key (usable from `DOCKER_SUBNET` only) into `.env` as
  `NEXTCLOUD_SMTP_API_KEY`/`_ID` (kept while it exists, re-minted otherwise),
  and makes port 25 offer `DOCKER_SUBNET` XOAUTH2/OAUTHBEARER **only**:
  Nextcloud's mailer (Symfony) tries LOGIN and PLAIN first, and each failure
  would count toward Stalwart's auth ban. Everyone else gets no AUTH on 25, as
  before. `nextcloud/configure.sh` then sets `mail_smtpauth`, `mail_smtpname`
  and `mail_smtppassword`.
- **Signed in, Stalwart relays to other domains.** Unauthenticated (before
  2026-11-23, or without the key) Nextcloud was just another inbound client:
  local attendees got invitations, external ones were refused ("Relay not
  allowed"). `MtaStageData.enableSpamFilter` still skips the stack's subnet
  (`is_empty(authenticated_as) && !is_ip_in_cidr(remote_ip, '<DOCKER_SUBNET>')`),
  for Keycloak's unauthenticated mail.
- Nextcloud mails **every** attendee, Nextcloud users included. It also
  delivers iTIP into an attendee's Nextcloud scheduling inbox when that
  attendee is a Nextcloud user. Invitations arrive as `multipart/mixed` with
  a `text/calendar` part (`METHOD:REQUEST`).
- **No Accept/Decline links in the mail** (`dav invitation_link_recipients=no`).
  Nextcloud builds them from the requesting host, and the façade's writes
  reach it as `http://nextcloud/`, so they pointed inside the Docker network.
  Attendees answer from their mail client (an iTIP `REPLY` to the organizer).
- `smoke.sh` tests this end to end. Alice PUTs an event inviting bob and the
  Boardroom, and the script polls bob's Inbox with JMAP for the invitation.
- Mail to external domains goes out through Stalwart's normal outbound queue
  (live delivers on port 25; SPF for the mail domain must list the host's
  public address).

## Service-account fallback

The primary design is that the façade acts **as the user**, with an
exchanged token for each backend. The fallbacks below are for work done
without a user token: background jobs, or a delegated or shared mailbox the
backend's own ACLs don't cover.

**Keycloak (directory).** Use the `nightware-api` service account, with
client credentials and `scope=kc-admin-api`. See "User → backend mapping".

**Stalwart (JMAP, IMAP, SMTP) has a master user.** Stalwart v1 has **no
`authentication.master` setting**; that was the v0.x TOML key. v1 keeps all
configuration in its data store and manages it through the JMAP API. The v1
equivalent is an account with the `impersonate` permission, and in this
stack that account is the **recovery administrator** from
`STALWART_RECOVERY_ADMIN` (`${STALWART_ADMIN_USER}:${STALWART_ADMIN_PASSWORD}`,
random per `.env`). Two login forms are verified, and
`smoke.sh` checks the first:

- **Master-user login.** The username is `<target address>%<master user>`
  and the password is the master user's own:

  ```bash
  curl -u "alice@nightware.localhost%admin:$STALWART_ADMIN_PASSWORD" \
       https://nightware.localhost/jmap/session
  ```

  The session that opens belongs to alice (`username` =
  `alice@nightware.localhost`). The same `alice@nightware.localhost%admin`
  works as an IMAP `LOGIN` or SASL `PLAIN` username. App passwords and API
  keys cannot impersonate.
- **Admin plus `accountId`.** Authenticate with Basic `admin:<password>` and
  put the target's id in each method call, for example
  `["Mailbox/get",{"accountId":"c"},"0"]`. The target's id comes from a
  master-user session's `primaryAccounts`.

Only the recovery admin can use a password while Keycloak is the default
directory, because OIDC accounts have none. A dedicated internal
impersonator account (`x:Account/set` with a password credential and the
`impersonate` permission) was **not tested**. Password sign-in for accounts
other than the recovery admin is not expected to work in this setup, since
the Community edition has no per-domain directories. Treat the recovery
admin as a dev-only, full-access credential (see Known limitations).

**Nextcloud has no fallback configured.** Token exchange V2 cannot
impersonate, and Nextcloud's admin cannot read another user's DAV data.
See Known limitations.

## Getting a token

`get-token.sh` wraps all the flows. Add `--claims` to print the decoded
payload.

```bash
./get-token.sh alice                      # password grant via nightware-dev (aud nightware-api)
./get-token.sh --pkce bob                 # nightmail client: auth code + PKCE, redirect
                                          # http://localhost:34571, login form driven by curl
./get-token.sh --pkce --json alice        # full token response incl. refresh_token
./get-token.sh --exchange stalwart alice  # what the façade sends to Stalwart
./get-token.sh --exchange nextcloud alice # ... and to Nextcloud
./get-token.sh --service                  # nightware-api service account (kc-admin-api)
```

The password grant by hand:

```bash
curl --cacert .certs/caddy-root.crt \
  https://nightware.localhost/auth/realms/nightware/protocol/openid-connect/token \
  -d grant_type=password -d client_id=nightware-dev -d "client_secret=$NIGHTWARE_DEV_CLIENT_SECRET" \
  -d username=alice -d 'password=Nightware-Dev-1!' -d scope=openid
```

For an interactive login without a redirect, use the device flow on
`nightmail`: POST to `.../protocol/openid-connect/auth/device` with
`client_id=nightmail&scope=openid&code_challenge=…&code_challenge_method=S256`.
PKCE is enforced here too. Open the returned `verification_uri`, then poll
the token endpoint with
`grant_type=urn:ietf:params:oauth:grant-type:device_code` and the
`code_verifier`.

## Recordings

`deploy/recordings/` holds real JMAP and CalDAV request/response pairs from
this stack, for WireMock fixtures. `recordings/README.md` describes the
format. Regenerate them with `./scripts/record.sh`, which needs a
bootstrapped stack and seeds a little more test data on each run.

## Known limitations

1. **The backends reject NightMail's own token.** This is by design (see
   "Token flow"): the façade must exchange. If pass-through is ever needed,
   add `audience stalwart` and `audience nextcloud` mappers to `nightmail`,
   so one token carries all three audiences. Keycloak supports this, and
   Stalwart and Nextcloud would accept it unchanged.
2. **Keycloak token exchange V2 has no impersonation.** It cannot mint a
   user token from the service account alone. The verified fallback for
   Stalwart is the recovery-admin master user (see "Service-account
   fallback"). Nextcloud
   has no equivalent configured: no service-account path to a user's DAV
   data exists yet. Options are the Nextcloud admin with per-user app
   passwords, or Keycloak legacy token exchange V1 with FGAP v1, which is
   deprecated.
3. **`STALWART_RECOVERY_ADMIN` is a standing full-access credential**
   (dev only). Its password is in `.env`.
4. **Stalwart's OIDC directory runs with `allowInvalidCerts: true`** by
   default. It fetches discovery and JWKS from `https://${PUBLIC_HOST}/auth`
   through Caddy, whose local CA it does not trust. With a publicly trusted
   certificate set `STALWART_ALLOW_INVALID_CERTS=false` (applied by
   `stalwart/configure.sh`). The directory also uses
   `unscopedTokens: "interoperable"`, because Keycloak issues
   `openid email profile`, not the `urn:ietf:params:oauth:scope:*` scopes.
   The strict audience check (`acceptedAudiences: {stalwart}`) is what
   limits which tokens get in.
5. **Nextcloud reaches Keycloak on the backchannel** (`http://keycloak:8080`,
   with `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`), not on
   `https://nightware.localhost/auth`. PHP's libcurl resolves every
   `*.localhost` name to its own 127.0.0.1 (RFC 6761) and never asks Docker
   DNS, so the request failed with
   `cURL error 7: Failed to connect to nightware.localhost port 443`.
   The `issuer` still matches the tokens. Stalwart (Rust) does use Docker DNS
   for these names, and so does `getent hosts` (glibc) in both containers;
   only libcurl short-circuits. Check this for the .NET
   façade too: if it fails, call Keycloak at `http://keycloak:8080` the same
   way, or set a non-`.localhost` `PUBLIC_HOST`.
6. **Stalwart cannot look up recipients in OIDC.** An address exists only
   once its account exists. The bootstrap pre-creates alice and bob through
   the API. A new Keycloak user receives no mail (mail to the address
   bounces) until they sign in once or are created with `x:Account/set`.
   SCIM provisioning would remove this, but it is Enterprise-only.
7. **Phones and other devices** cannot reach `*.localhost`. For device
   testing, set `PUBLIC_HOST` to a name that resolves on the LAN,
   `BIND_ADDRESS=0.0.0.0`, and either install `.certs/caddy-root.crt` on the
   device or use a publicly trusted certificate (ACME, or certificate files
   via `scripts/fetch-cert.sh`). NightMail needs valid TLS for the Keycloak
   endpoints that `/.well-known/nightware` advertises. Docker Desktop on macOS
   can bind only `127.0.0.1` or `0.0.0.0`, not one specific other address
   (a WireGuard interface, say).
8. `HTTPS_PORT` other than 443 requires `PUBLIC_PORT_SUFFIX=:<port>`. That
   path is wired through Keycloak, Stalwart and Nextcloud but **not
   tested**. Changing `PUBLIC_HOST`, `MAIL_DOMAIN` or the port after the
   first bootstrap needs `down -v`: Stalwart and Keycloak store the host names
   and the domain. That includes a stack bootstrapped with the old
   five-hostname layout (`BASE_DOMAIN`): start it again from fresh volumes, or
   keep it under its old compose file.
9. Stalwart downloads its WebUI and spam rules from GitHub on first start,
   so outbound HTTPS is required.

## Resource usage (Apple Silicon, Docker 29)

- Collabora adds about 1.7 GB of image and about 500 MB of RAM.
- Images: about 4.0 GB as `docker images` reports them. Nextcloud is
  2.14 GB, Keycloak 750 MB, Stalwart 474 MB, Postgres 416 MB (shared by both
  databases), Redis 134 MB and Caddy 88 MB.
- Volumes after bootstrap: about 1.45 GB, most of it Nextcloud's webroot and
  app copies.
- RAM while idle: about 1.2 GB in total. Keycloak takes about 670 MB,
  Nextcloud about 260 MB and Stalwart about 105 MB.
