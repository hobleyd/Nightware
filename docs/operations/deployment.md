# Deployment

The reference deployment is `deploy/docker-compose.yml`: Caddy (TLS, reverse proxy) in front of
Keycloak, Stalwart, Nextcloud, Collabora and the Nightware façade, on one Docker network and one
public host name. Compose commands below assume `deploy/` as the working directory, where compose
takes the project name from `.env` (`COMPOSE_PROJECT_NAME`, default `nightware`). `deploy/README.md`
covers the stack in detail; this page covers what changes for a real, internet-facing install.

## 1. Host, mail domain and DNS

Pick one public host name (`PUBLIC_HOST`) and point it at the machine running Caddy. Every service
is a path on it:

| Path | Serves |
|---|---|
| `/auth/` | Keycloak (OIDC issuer, token endpoint, login pages) |
| `/.well-known/jmap`, `/jmap/` | Stalwart JMAP (at the root: Stalwart cannot run under a prefix); `/mail/` for its other paths |
| `/cloud/` | Nextcloud |
| `/office/` | Collabora (Nextcloud Office) |
| `/graph/`, `/.well-known/nightware` | Nightware (users type the host name into NightMail) |
| `/console/` | The admin console (from `ADMIN_ALLOWED_CIDRS`) |
| `/grafana/` | Grafana: the façade's traces, metrics and logs (from `ADMIN_ALLOWED_CIDRS`; global admins, through Keycloak) |

The mail domain (`MAIL_DOMAIN`) is separate: addresses are `user@<MAIL_DOMAIN>` whatever the host is
called, and Stalwart's own SMTP/IMAP name is `mail.<MAIL_DOMAIN>`. Mail delivery additionally needs
MX, SPF, DKIM and DMARC records for that domain — see Stalwart's documentation; the façade is not
involved.

## 2. TLS

Set in `deploy/.env`:

```bash
PUBLIC_HOST=nightware.example.com
MAIL_DOMAIN=example.com
CADDY_TLS=ops@example.com     # an ACME account e-mail: Let's Encrypt certificates
# or: CADDY_TLS="/certs/fullchain.crt /certs/privkey.key"   (your own files in deploy/.certs)
HTTPS_PORT=443
PUBLIC_PORT_SUFFIX=           # empty on 443
BIND_ADDRESS=0.0.0.0          # listen on the public interface
# With either publicly trusted option:
COLLABORA_SSL_VERIFICATION=true
STALWART_ALLOW_INVALID_CERTS=false
NIGHTWARE_SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt
```

With `CADDY_TLS` set to an e-mail address Caddy obtains publicly trusted certificates (inbound 443,
or 80 for the HTTP challenge, must reach the host). With two file paths it serves the certificate
you put in `deploy/.certs` (mounted read-only at `/certs`); `deploy/scripts/fetch-cert.sh`
(`make cert`) copies one from another host over ssh, checks it, and reloads Caddy — rerun it after
each renewal ([runbook](runbook.md#tls-certificate-renewal-certificate-files)). The default,
`internal`, uses Caddy's local CA, which every client device would have to trust — development only.

Docker Desktop on macOS can publish ports on `127.0.0.1` or `0.0.0.0` only, not on one specific
other address such as a WireGuard interface.

Caddy sets no HSTS of its own; the façade sends `Strict-Transport-Security: max-age=31536000` on
HTTPS requests (`SecurityHeaders:HstsMaxAge`). Keycloak and Nextcloud manage their own headers.

## 3. Secrets

`deploy/scripts/init-env.sh` gives a new `.env` a random value for every secret before the first
start (the realm import substitutes them): `KEYCLOAK_ADMIN_PASSWORD`, `KEYCLOAK_DB_PASSWORD`,
`NIGHTWARE_API_CLIENT_SECRET`, `NIGHTWARE_DEV_CLIENT_SECRET`, `NEXTCLOUD_OIDC_CLIENT_SECRET`,
`STALWART_ADMIN_PASSWORD`, `NEXTCLOUD_ADMIN_PASSWORD`, `NEXTCLOUD_DB_PASSWORD` and
`NIGHTWARE_DELTA_LINK_KEY`. It never changes a value already set, because Postgres and the imported
realm keep the one they were bootstrapped with; for an older `.env` still holding the development
passwords this repository used to publish it only warns. Such a stack must not face a network:
bring the deployment up on fresh volumes with a fresh `.env`. Also delete the `nightware-dev`
client (it allows the password grant and exists for scripts only) and remove the test users alice
and bob.

### The façade's root secret: `NIGHTWARE_DELTA_LINK_KEY`

`DeltaLinks:SigningKey` (compose: `NIGHTWARE_DELTA_LINK_KEY`) is the façade's own root secret.
`Directory:ClientSecret` is different: Keycloak holds that one too. The root secret is used for two
things:

- **delta and next links.** It is the HMAC key that signs every `@odata.deltaLink` and
  `@odata.nextLink`.
- **self-authorising URLs.** HKDF derives one AES-256-GCM key per URL kind from it. Those keys seal
  the caller's **full access token** into To Do `uploadUrl`s and `/shares/…/content` download
  redirects (ADR 0001 §39, §43).

Anyone who knows it can forge links and open any sealed URL they see, recovering the token inside.
So:

- It must be random, at least 32 bytes and identical on every instance. Generate it with
  `openssl rand -hex 32`. `deploy/scripts/init-env.sh` writes one into `deploy/.env` when the line
  is absent or empty, and never replaces an existing one. `bootstrap.sh`, `make stack` and
  `make api` run it.
- `docker-compose.yml` has **no default** for it. Every compose command fails with
  `required variable NIGHTWARE_DELTA_LINK_KEY is missing a value` until `.env` has one.
- With `ASPNETCORE_ENVIRONMENT=Production` the façade **refuses to start** in three cases:
  - the key is missing
  - it is shorter than 32 bytes
  - it is one of the development values published in this repository

  The error names `DeltaLinks__SigningKey`. Development and test hosts fall back to a random
  per-process key.
- Keep it out of version control and logs, like the client secrets. `.env` is git-ignored.

**Rotating it** (suspected leak, staff change) is a restart with the new value on every instance.
Do it on all instances at once, because a mixed fleet rejects half of each other's links. It
invalidates:

- **every stored delta and next link.** A link signed with the old key answers `410
  syncStateNotFound`. Clients clear their cursor and do a **full resync** of each folder: correct,
  but a burst of backend load as every device re-pulls.
- **every in-flight upload and download URL.** An upload session `uploadUrl` or a 5-minute
  download redirect sealed with the old key no longer recovers a credential and answers `401`.
  NightMail uses an upload URL right after creating the session and follows a download redirect
  at once, so only operations in flight during the restart are hit. Each fails once: the attachment
  upload errors, or the document preview shows "Authentication required" (the 401 on the redirect
  leg). The user retries.

```bash
cd deploy
new=$(openssl rand -hex 32)
grep -v '^NIGHTWARE_DELTA_LINK_KEY=' .env > .env.tmp && echo "NIGHTWARE_DELTA_LINK_KEY=$new" >> .env.tmp && cat .env.tmp > .env && rm .env.tmp
docker compose --profile api up -d --no-deps --force-recreate nightware-api
```

Nothing else holds state derived from the key. Exchanged-token caches are unaffected.

## 4. The façade container

```bash
cd deploy
NIGHTWARE_ENVIRONMENT=Production docker compose --profile api up -d --build --no-deps nightware-api
```

`ASPNETCORE_ENVIRONMENT` must be `Production` outside development: `Development` loads
`appsettings.Development.json`, which carries a development delta-link key and client secret, and
only Production enforces the signing-key check above. The compose default is `Development`.
Production logs are JSON lines on stdout (one object per record, with scopes).

What the compose service already sets, and why:

- `Auth__Authority` is the **public** issuer (`https://<host>/auth/realms/nightware`), while
  `Auth__MetadataAddress` fetches keys over the internal network (`http://keycloak:8080/auth/...`).
- `Discovery__GraphBaseUrl` is `https://<host>/graph/v1.0`. Caddy strips `/graph` before the façade
  and sends `X-Forwarded-Prefix: /graph`; every link is built on the configured base, so
  `@odata.nextLink`, delta links and sealed upload/download URLs carry `/graph`.
- `ForwardedHeaders__KnownNetworks__0=${DOCKER_SUBNET}` — only Caddy may set `X-Forwarded-*`. If you
  run another proxy in front of Caddy, add its address too and raise `ForwardedHeaders:ForwardLimit`.
- `Cors__AllowedOrigins__0=${NIGHTMAIL_WEB_ORIGIN}` — the NightMail web build's origin. Leave it
  unset if you do not serve the web build.
- The container has a liveness healthcheck on `/healthz/live`.

The image runs as a non-root user on port 8080, needs no volume, and can be scaled horizontally:
it holds no state except a disposable in-memory cache of exchanged tokens. With more than one
instance, every instance must share `DeltaLinks:SigningKey`; per-user rate limits are per instance
(see [security.md](security.md)).

## 5. Verify

```bash
cd deploy && ./smoke.sh             # every backend, token exchange, iMIP, the façade
curl https://nightware.example.com/.well-known/nightware
curl https://nightware.example.com/graph/healthz    # Healthy = every backend reachable (cached 10 s)
```

Then sign in from NightMail with the server address `nightware.example.com`.

## Sizing notes

The façade is CPU-light; Nextcloud and Stalwart dominate. Measured on the dev stack, the façade's
own overhead (liveness, discovery) is a few milliseconds at p95 and `/me` is ~15 ms at p95
(`tests/Nightware.Load/README.md`). Give Nextcloud the most memory (PHP workers) and keep its Redis.
