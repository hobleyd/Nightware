# Nightware deployment stack (dev)

A Docker Compose stack in which every backend trusts one OIDC issuer
(Keycloak). The Nightware Graph façade runs in front of it:

| Public URL | Service | Image (linux/arm64 + linux/amd64 manifests verified) |
|---|---|---|
| `https://auth.${BASE_DOMAIN}` | Keycloak, realm `nightware` (+ Postgres) | `quay.io/keycloak/keycloak:26.8.2`, `postgres:17.12-alpine` |
| `https://mail.${BASE_DOMAIN}` | Stalwart: JMAP, WebDAV, WebUI, management API | `stalwartlabs/stalwart:v1.1.0` |
| `https://cloud.${BASE_DOMAIN}` | Nextcloud: CalDAV/CardDAV/WebDAV/OCS (+ Postgres, Redis, cron) | `nextcloud:35.0.3-apache`, `redis:8.6.8-alpine` |
| `https://graph.${BASE_DOMAIN}` | Nightware API (`api` profile) | built from `src/Nightware.Api/Dockerfile` |
| (all of the above) | Caddy reverse proxy, TLS | `caddy:2.11.9-alpine` |

`BASE_DOMAIN` defaults to `nightware.localhost`. Browsers and curl resolve
`*.localhost` to 127.0.0.1, so no `/etc/hosts` entries are needed.

Nextcloud apps installed by the bootstrap: calendar 6.6.5, contacts 8.10.0,
tasks 0.18.1, user_oidc 8.13.0, calendar_resource_management 1.0.0.

## Bring-up

```bash
cd deploy
cp .env.example .env                     # dev defaults; change secrets for anything shared
docker compose -p nightware up -d        # ~1-2 min until Keycloak + Nextcloud are healthy
./bootstrap.sh                           # one-time, idempotent (~1 min on a fresh volume)
./smoke.sh                               # exit status = number of failed checks
```

`bootstrap.sh` runs, in order:

1. Exports Caddy's local root CA to `.certs/caddy-root.crt`. All scripts use it
   with `curl --cacert`. Trust it in your OS or browser if you want to open
   the URLs without warnings. On macOS:
   `sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain .certs/caddy-root.crt`
2. `keycloak/configure.sh` declares the mailboxSettings attributes in the
   user profile (see "User → backend mapping"). It seeds them for alice and
   bob through the `nightware-api` service account.
3. `stalwart/configure.sh` runs Stalwart's first-start Bootstrap through the
   API and restarts the container. It then pre-creates alice and bob, creates
   the Keycloak OIDC directory and makes it the default, sets the trusted
   network for iMIP, and finally reloads.
4. `nextcloud/configure.sh` installs the apps and runs the `occ` setup:
   user_oidc with bearer validation, mail through Stalwart, and rooms.
5. Has alice and bob sign in once on each backend, which provisions them.

The façade itself:

```bash
docker compose -p nightware --profile api up -d --build   # once src/Nightware.Api/Dockerfile exists
```

The API should listen on 8080 inside its container and serve `/healthz` and
`/v1.0/*`. `smoke.sh` checks both when the container is running. The
`Auth__*`, `Discovery__*`, `DeltaLinks__*`, `Directory__*` and
`BackendCredentials__*` variables in `docker-compose.yml` are the façade's
real configuration (see `docs/api-plumbing.md`); `Jmap__*` and `Dav__*` are
proposals for the JMAP/DAV adapters to adopt or rename.

Tear down with `docker compose -p nightware down`. Add `-v` to also delete
all data. A re-bootstrap from empty volumes is fully scripted.

## Test users

| Keycloak username | Password (`TEST_USER_PASSWORD`) | Email / Stalwart mailbox | Nextcloud uid | zoneinfo |
|---|---|---|---|---|
| `alice` | `Nightware-Dev-1!` | `alice@nightware.localhost` | `alice` | Australia/Sydney |
| `bob` | `Nightware-Dev-1!` | `bob@nightware.localhost` | `bob` | Europe/London |

Admin consoles (credentials are in `.env`):

- Keycloak: `https://auth.nightware.localhost/admin` (master realm `admin`).
- Stalwart WebUI: `https://mail.nightware.localhost/admin`, as the recovery
  admin `admin`.
- Nextcloud: `https://cloud.nightware.localhost`. Log in as `admin`, or use
  "Log in with Keycloak".

## Identity model

### Clients in realm `nightware`

| Client | Type | Purpose |
|---|---|---|
| `nightmail` | public | The NightMail app. Auth code + PKCE S256 (enforced), `response_mode=query`, no secret. Redirect URIs are exactly `nightmail://auth-callback` (macOS/iOS/Android), `http://localhost:34571` (Windows/Linux, one port only) and `${NIGHTMAIL_WEB_ORIGIN}/callback.html` (web, default `http://localhost:34572`). That origin is allowed for CORS on discovery and the token endpoint. The device flow is enabled (it also needs PKCE). An audience mapper adds `nightware-api`. |
| `nightware-api` | confidential | The façade. It is the resource server for NightMail tokens, which carry `aud: nightware-api`. It has **standard token exchange (RFC 8693, V2)** enabled, with audience mappers for `stalwart` and `nextcloud`. Its **service account** holds realm-management `view-users`, `query-users` and `manage-users`, plus the optional `kc-admin-api` scope. |
| `nightware-dev` | confidential | **Dev only.** Password grant, so scripts and integration tests can get a NightMail-shaped user token (`aud: nightware-api`) without a browser. |
| `stalwart` | audience only | Stalwart accepts only tokens with `aud: stalwart`. |
| `nextcloud` | confidential | user_oidc's provider client, used for browser login. Nextcloud requires `aud` to contain `nextcloud` on bearer requests. |

### Token flow

NightMail's token is **not** accepted by Stalwart or Nextcloud: both return
401 to `aud: nightware-api`, and `smoke.sh` asserts this. The façade
exchanges the inbound user token once per backend and caches it until
`exp`:

```bash
curl -u nightware-api:$NIGHTWARE_API_CLIENT_SECRET \
  https://auth.nightware.localhost/realms/nightware/protocol/openid-connect/token \
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
| `iss` | `https://auth.nightware.localhost/realms/nightware` (fixed by `KC_HOSTNAME`; the same inside and outside Docker) |
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
  alice's JMAP `username` is `alice@nightware.localhost`.
- **Nextcloud uid = `preferred_username`** (user_oidc `--unique-uid=0
  --mapping-uid=preferred_username`). The uid for alice is `alice`, so her
  DAV paths are `/remote.php/dav/.../alice/`.
- **Keycloak user lookups** (`/users`, `/users/{id}`) use the `nightware-api`
  service account. Get its token with
  `grant_type=client_credentials&scope=kc-admin-api` (Basic
  `nightware-api:<secret>`). Then call
  `https://auth.../admin/realms/nightware/users?email=...`, adding
  `exact=true` and `briefRepresentation` as needed. Both the Caddy URL and
  `http://keycloak:8080` work.
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

## Backend facts for adapter developers

### Stalwart (JMAP)

- Session: `GET https://mail.nightware.localhost/.well-known/jmap` returns
  `307` to `/jmap/session`. Inside Docker, `http://stalwart:8080/jmap/session`
  also works without TLS.
- The session's `apiUrl`, `downloadUrl`, `uploadUrl` and `eventSourceUrl`
  are **public** URLs (`https://mail.nightware.localhost/jmap/...`, from
  `STALWART_PUBLIC_URL`). A façade container reaches them because
  `mail.${BASE_DOMAIN}` is a network alias of Caddy. It must trust
  `.certs/caddy-root.crt`, or rewrite the host to `http://stalwart:8080`.
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

| What | URL (relative to `https://cloud.nightware.localhost`, or `http://nextcloud` inside Docker) |
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

### iMIP (meeting invitations by mail)

- **Nextcloud sends with Stalwart SMTP:** `mail_smtphost=stalwart`, port 25,
  STARTTLS with Stalwart's self-signed certificate (peer verification off),
  **no SMTP AUTH**. SMTP AUTH is not possible because every account lives in
  Keycloak and has no password. The envelope and `From:` are
  `calendar@nightware.localhost` ("Alice Anderson via Nextcloud"), and
  `Reply-To:` is the organizer.
- **Stalwart accepts this the way any MX accepts inbound mail:** port 25
  takes unauthenticated mail **for local recipients only**, which is not open
  relay. The compose network has a pinned subnet (`DOCKER_SUBNET`, default
  `172.31.250.0/24`), and `stalwart/configure.sh` sets
  `MtaStageData.enableSpamFilter` to
  `is_empty(authenticated_as) && !is_ip_in_cidr(remote_ip, '<DOCKER_SUBNET>')`.
  Without that rule, invitations were filed in **Junk** (no SPF or DKIM for
  `*.localhost`). Mail from the trusted subnet now lands in the Inbox.
- Nextcloud mails **every** attendee, Nextcloud users included. It also
  delivers iTIP into an attendee's Nextcloud scheduling inbox when that
  attendee is a Nextcloud user. Invitations arrive as `multipart/mixed` with
  a `text/calendar` part (`METHOD:REQUEST`).
- `smoke.sh` tests this end to end. Alice PUTs an event inviting bob and the
  Boardroom, and the script polls bob's Inbox with JMAP for the invitation.
- Mail to external domains would be relayed by Stalwart's normal outbound
  queue. That is not configured or tested here (no outbound port 25).

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
default `admin:nightware-stalwart-admin`). Two login forms are verified, and
`smoke.sh` checks the first:

- **Master-user login.** The username is `<target address>%<master user>`
  and the password is the master user's own:

  ```bash
  curl -u 'alice@nightware.localhost%admin:nightware-stalwart-admin' \
       https://mail.nightware.localhost/jmap/session
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
  https://auth.nightware.localhost/realms/nightware/protocol/openid-connect/token \
  -d grant_type=password -d client_id=nightware-dev -d client_secret=nightware-dev-secret \
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
4. **Stalwart's OIDC directory runs with `allowInvalidCerts: true`.** It
   fetches discovery and JWKS from `https://auth.${BASE_DOMAIN}` through
   Caddy, whose CA it does not trust. Turn the flag off with a publicly
   trusted certificate (`CADDY_TLS=<acme email>`). The directory also uses
   `unscopedTokens: "interoperable"`, because Keycloak issues
   `openid email profile`, not the `urn:ietf:params:oauth:scope:*` scopes.
   The strict audience check (`acceptedAudiences: {stalwart}`) is what
   limits which tokens get in.
5. **Nextcloud reaches Keycloak on the backchannel** (`http://keycloak:8080`,
   with `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`), not on
   `https://auth.nightware.localhost`. PHP's libcurl resolves every
   `*.localhost` name to its own 127.0.0.1 (RFC 6761) and never asks Docker
   DNS, so the request failed with
   `cURL error 7: Failed to connect to auth.nightware.localhost port 443`.
   The `issuer` still matches the tokens. Stalwart (Rust) does use Docker DNS
   for these names, and so does `getent hosts` (glibc) in both containers;
   only libcurl short-circuits. Check this for the .NET
   façade too: if it fails, call Keycloak at `http://keycloak:8080` the same
   way, or set a non-`.localhost` `BASE_DOMAIN`.
6. **Stalwart cannot look up recipients in OIDC.** An address exists only
   once its account exists. The bootstrap pre-creates alice and bob through
   the API. A new Keycloak user receives no mail (mail to the address
   bounces) until they sign in once or are created with `x:Account/set`.
   SCIM provisioning would remove this, but it is Enterprise-only.
7. **Phones and other devices** cannot reach `*.localhost`. For device
   testing, set `BASE_DOMAIN` to a name that resolves on the LAN, and either
   install `.certs/caddy-root.crt` on the device or use
   `CADDY_TLS=<acme email>` with a public domain. NightMail needs valid TLS
   for the Keycloak endpoints that `/.well-known/nightware` advertises.
8. `HTTPS_PORT` other than 443 requires `PUBLIC_PORT_SUFFIX=:<port>`. That
   path is wired through Keycloak, Stalwart and Nextcloud but **not
   tested**. Changing `BASE_DOMAIN` or the port after the first bootstrap
   needs `down -v`: Stalwart and Keycloak store the hostnames.
9. Stalwart downloads its WebUI and spam rules from GitHub on first start,
   so outbound HTTPS is required.

## Resource usage (Apple Silicon, Docker 29)

- Images: about 4.0 GB as `docker images` reports them. Nextcloud is
  2.14 GB, Keycloak 750 MB, Stalwart 474 MB, Postgres 416 MB (shared by both
  databases), Redis 134 MB and Caddy 88 MB.
- Volumes after bootstrap: about 1.45 GB, most of it Nextcloud's webroot and
  app copies.
- RAM while idle: about 1.2 GB in total. Keycloak takes about 670 MB,
  Nextcloud about 260 MB and Stalwart about 105 MB.
