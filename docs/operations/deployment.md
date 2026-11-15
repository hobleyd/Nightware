# Deployment

The reference deployment is `deploy/docker-compose.yml`: Caddy (TLS, reverse proxy) in front of
Keycloak, Stalwart, Nextcloud and the Nightware façade, on one Docker network. `deploy/README.md`
covers the stack in detail; this page covers what changes for a real, internet-facing install.

## 1. Domain and DNS

Pick a base domain and point four names at the host running Caddy:

| Name | Serves |
|---|---|
| `auth.<domain>` | Keycloak (OIDC issuer, token endpoint, login pages) |
| `mail.<domain>` | Stalwart (JMAP, WebUI, SMTP/IMAP names in certificates) |
| `cloud.<domain>` | Nextcloud |
| `graph.<domain>` | Nightware (the address users type into NightMail) |

Mail delivery additionally needs MX, SPF, DKIM and DMARC records for the domain — see Stalwart's
documentation; the façade is not involved.

## 2. TLS

Set in `deploy/.env`:

```bash
BASE_DOMAIN=example.com
CADDY_TLS=ops@example.com     # an ACME account e-mail: Let's Encrypt certificates
HTTPS_PORT=443
PUBLIC_PORT_SUFFIX=           # empty on 443
BIND_ADDRESS=0.0.0.0          # listen on the public interface
```

With `CADDY_TLS` set to an e-mail address Caddy obtains publicly trusted certificates (inbound 443,
or 80 for the HTTP challenge, must reach the host). The default, `internal`, uses Caddy's local CA,
which every client device would have to trust — development only.

Caddy sets no HSTS of its own; the façade sends `Strict-Transport-Security: max-age=31536000` on
HTTPS requests (`SecurityHeaders:HstsMaxAge`). Keycloak and Nextcloud manage their own headers.

## 3. Secrets

Change **every** secret in `.env.example` before the first start (the realm import substitutes
them): `KEYCLOAK_ADMIN_PASSWORD`, `KEYCLOAK_DB_PASSWORD`, `NIGHTWARE_API_CLIENT_SECRET`,
`NIGHTWARE_DEV_CLIENT_SECRET` (and delete the `nightware-dev` client — it allows the password
grant and exists for scripts only), `NEXTCLOUD_OIDC_CLIENT_SECRET`, `STALWART_ADMIN_PASSWORD`,
`NEXTCLOUD_ADMIN_PASSWORD`, `NEXTCLOUD_DB_PASSWORD`, and remove the test users alice and bob.

For the façade, set a stable delta-link signing key shared by every instance:

```bash
NIGHTWARE_DELTA_LINK_KEY=$(openssl rand -base64 48)
```

Without it each process signs with a random key, so every restart invalidates every client's
delta links (clients recover with a full resync — correct, but expensive).

## 4. The façade container

```bash
cd deploy
NIGHTWARE_ENVIRONMENT=Production docker compose -p nightware --profile api up -d --build --no-deps nightware-api
```

`ASPNETCORE_ENVIRONMENT` must be `Production` outside development: `Development` loads
`appsettings.Development.json`, which carries a development delta-link key and client secret.
Production logs are JSON lines on stdout (one object per record, with scopes).

What the compose service already sets, and why:

- `Auth__Authority` is the **public** issuer (`https://auth.<domain>/realms/nightware`), while
  `Auth__MetadataAddress` fetches keys over the internal network (`http://keycloak:8080/...`).
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
curl https://graph.example.com/.well-known/nightware
curl https://graph.example.com/healthz          # Healthy = every backend reachable
```

Then sign in from NightMail with the server address `graph.example.com`.

## Sizing notes

The façade is CPU-light; Nextcloud and Stalwart dominate. Measured on the dev stack, the façade's
own overhead (liveness, discovery) is a few milliseconds at p95 and `/me` is ~15 ms at p95
(`tests/Nightware.Load/README.md`). Give Nextcloud the most memory (PHP workers) and keep its Redis.
