# Runbook

Symptoms are what NightMail users see; each entry gives the likely cause, how to confirm it, and the
fix. Every façade error carries a `request-id` (response header and `innerError`); search the logs
for it first.

## Everyone gets 503 "The service is misconfigured…" (token exchange)

**Cause.** The façade's exchange of the user's token for a Stalwart/Nextcloud token is rejected by
Keycloak for a reason that is the *façade's* fault: wrong `Directory:ClientSecret`, token exchange
disabled on `nightware-api`, or a missing audience mapper/target client (`stalwart`, `nextcloud`).
These map to 503 (ADR 0001 §34) so that users are not all forced to sign in again.

**Confirm.** Error-level log `Keycloak <operation> rejected the façade's own request — check
Directory/BackendCredentials configuration and the realm`, with Keycloak's `error` and
`error_description`. Reproduce:
`deploy/get-token.sh --exchange stalwart alice`.

**Fix.** Correct the secret (`NIGHTWARE_API_CLIENT_SECRET` must match the Keycloak client), enable
"Standard token exchange" on `nightware-api`, restore the audience mappers (`deploy/keycloak/
nightware-realm.json`), restart the façade. Exchanged tokens are cached for their lifetime; a restart
clears the cache.

## The façade does not start: `DeltaLinks:SigningKey`

**Symptoms.** The container restarts in a loop. Its log has an `OptionsValidationException` naming
`DeltaLinks:SigningKey (environment variable DeltaLinks__SigningKey; compose: NIGHTWARE_DELTA_LINK_KEY …)`.
Or `docker compose` itself refuses with `required variable NIGHTWARE_DELTA_LINK_KEY is missing a value`.

**Cause.** Production needs the root secret: set, at least 32 bytes, and not a development value.

**Fix.** Run `deploy/scripts/init-env.sh`, or set `NIGHTWARE_DELTA_LINK_KEY=$(openssl rand -hex 32)`
in `deploy/.env` yourself. Then recreate the façade:
`docker compose --profile api up -d --no-deps nightware-api` (from `deploy/`).
On a multi-instance deployment use the key the other instances already have, not a new one; a new
key forces every client to resync ([deployment.md](deployment.md#the-façades-root-secret-nightware_delta_link_key)).

## Everyone gets 401

**Cause.** Tokens fail validation: issuer mismatch (`Auth:Authority` ≠ the token's `iss`, e.g.
after changing `PUBLIC_HOST` or `PUBLIC_PORT_SUFFIX`), the façade cannot fetch signing keys
(`Auth:MetadataAddress` unreachable), the `nightware-api` audience mapper was removed, or **clock
skew** (below). NightMail answers a 401 by refreshing the token and retrying once, then shows the
re-auth banner.

**Confirm.** Raise `Logging__LogLevel__Microsoft.AspNetCore.Authentication` to `Information`
(the default for `Microsoft.AspNetCore` is `Warning`); JwtBearer then names the failure: `IDX10205` issuer, `IDX10214` audience,
`IDX10223` lifetime, `IDX10500/10503` signature/keys. Compare `iss`/`aud`:
`deploy/get-token.sh --claims alice`.

**Fix.** Align `Auth:Authority` with Keycloak's `KC_HOSTNAME` realm URL, make the metadata address
reachable from the container, restore the audience mapper.

## Clock skew

**Cause.** The host clock (or the Docker VM's, after a laptop sleep) drifted. Tokens look "not yet
valid" or expired (`IDX10223`, `IDX10222`); backends may reject exchanged tokens as well.

**Confirm.** `docker run --rm alpine date -u` vs. a trusted time source; Keycloak `iat` vs. now.

**Fix.** Run NTP/chrony on the host; on Docker Desktop restart the VM. The validator's default
clock skew tolerance is 5 minutes; do not raise it to hide a broken clock.

## Stalwart is down

**Symptoms.** Mail calls answer `503 serviceNotAvailable` with `Retry-After`; NightMail retries up
to 5 times, then shows the error; its delta poller counts a strike and resyncs after three.
Calendar/contacts keep working. `/healthz` reports the JMAP check unhealthy; `/healthz/live` stays
healthy (do not restart the façade).

**Confirm.** `docker compose ps stalwart`, `docker compose logs stalwart` (from `deploy/`).

**Fix.** Restart Stalwart (`docker compose restart stalwart`). The façade recovers on
the next request — no restart, nothing cached to clear. Clients' delta links stay valid.

## Nextcloud is slow

**Symptoms.** Calendar/contacts/tasks calls take seconds; beyond 25 s reads answer 503 +
`Retry-After` and writes 504 (`nightware.request.timeouts` rises). `http.client.request.duration`
for `IDavClient` p95 climbs.

**Confirm.** Nextcloud `occ status`, PHP-FPM/Apache worker saturation, Postgres slow queries,
Redis reachability (`docker compose logs nextcloud nextcloud-redis`), background jobs
(`nextcloud-cron` running?).

**Fix.** Give Nextcloud more workers/memory, make sure Redis is up (file locking and caching fall
back to the database without it), run `occ db:add-missing-indices`. Do not raise the façade's budget
above ~25 s: NightMail gives up at 30 s anyway, and a 503 it can retry is better than a transport
error. Maintenance mode (`occ maintenance:mode --on`) answers 503 — safe for NightMail.

## Keycloak down or restarting

**Symptoms.** New sign-ins fail; **every token refresh in that window signs the user out on that
device** — NightMail treats any HTTP response to a refresh (including Caddy's 502) as revoked
credentials. Graph calls with still-valid tokens keep working until a token exchange is needed
(cached exchanges keep working until they expire), then answer 503.

**Fix.** Restart Keycloak immediately; afterwards, run it with two instances so restarts and
upgrades never leave the token endpoint answering errors. Never put a maintenance page in front of
`auth.<domain>`. If you must take it down, prefer dropping connections (no HTTP response — NightMail
retries network errors) over any HTTP error response.

## Users see 429 "Too many requests"

**Cause.** One user exceeded the per-user limits (300 burst, 10/s, 32 in flight) — usually a client
bug or a script, sometimes a very large outbox drain across several devices.

**Confirm.** `aspnetcore.rate_limiting.requests{result="rejected"}`; logs by request id show the
`sub`-partitioned rejections.

**Fix.** NightMail waits `Retry-After` and continues; nothing to do for occasional 429s. Raise
`RateLimiting:*` if legitimate heavy use trips it.

## Mail "sent" but never delivered (Stalwart inbound throttle)

**Symptoms.** `sendMail`/draft send returns `202 Accepted`, the message is in Sent Items, but the
recipient never receives it — typically after a burst of mail to one person (a meeting series,
a script, a busy colleague). The façade cannot see this: submission succeeded, delivery is
Stalwart's.

**Cause.** Stalwart's default `MtaInboundThrottle` "sender domain to recipient" allows 25 messages
an hour per (sender domain, recipient). In a single-domain organisation that caps *all colleagues
together* at 25 messages an hour to any one person.

**Confirm.** Stalwart's log shows `smtp.rate-limit-exceeded` for the recipient
(`docker compose logs stalwart | grep rate-limit-exceeded`).

**Fix.** `deploy/stalwart/configure.sh` (run by `bootstrap.sh`, idempotent) scopes that throttle to
**unauthenticated inbound only** (`is_empty(authenticated_as)`); authenticated submissions —
everything sent through the façade — are no longer counted. Re-run it on stacks bootstrapped before
that change. Still subject to the throttle: external senders and **Nextcloud's iMIP invitations**,
which arrive unauthenticated on port 25 — a burst of more than 25 invitations an hour from
Nextcloud to one attendee is throttled. Raise or re-key that throttle in Stalwart's settings
(*Settings → MTA → Inbound → Throttles*) if your organisation schedules at that rate.

## 429 on mail calls with several devices (Stalwart's concurrent-request limit)

**Cause.** Stalwart limits JMAP requests in flight per account (`maxConcurrentRequests`, Stalwart's
default 4). Every device of a user reaches it as that user, so a burst above the limit gets a JMAP
`limit` problem, which the façade answers as `429 TooManyRequests` with `Retry-After: 5` and the
message "The mail server's maxConcurrentRequests limit was reached."

**Default.** `deploy/stalwart/configure.sh` sets it to 32, matching the façade's own per-user limit
(`RateLimiting:ConcurrentRequestLimit`), so the façade's limit is the one users meet. Override with
`STALWART_JMAP_MAX_CONCURRENT_REQUESTS`. Stalwart applies this limit only at startup (a settings
reload is not enough), so the script restarts Stalwart when it changes the value. If you change it
in Stalwart's web UI, restart Stalwart too.

**Confirm.** `curl` a dozen parallel `…/mailFolders/inbox/messages/delta` calls with one user's
token: every one should be 200. If some are 429 with the message above, the limit in force is
lower than the burst (check the value, then whether Stalwart was restarted since it was set).

## TLS errors after the host slept (dev stack, `CADDY_TLS=internal`)

**Cause.** Caddy's local CA issues 12-hour certificates and renews them from a 10-minute
maintenance timer. While a laptop sleeps, Docker's VM is paused, so a certificate can expire with
no renewal attempt; for up to 10 minutes after waking, clients see `certificate has expired` on
every path of `PUBLIC_HOST`. ACME certificates (90 days, renewed a month ahead) and certificate
files are not affected.

**Fix.** Wait for the next maintenance run, or `docker compose restart caddy`, which
renews expired certificates on start (its volumes, and so the root CA clients trust, are kept).

## TLS certificate renewal (certificate files)

With `CADDY_TLS="/certs/fullchain.crt /certs/privkey.key"` Caddy serves exactly the files in
`deploy/.certs` and never renews them itself. When the source (another host's Let's Encrypt
client, say) renews, copy the new files over:

```bash
make cert          # deploy/scripts/fetch-cert.sh: ssh copy, checks, Caddy reload
```

**When.** Once a month, forced: a Let's Encrypt certificate lives 90 days and is renewed with
about 30 left, so a monthly run always picks up the renewal well before expiry. The script refuses
a certificate that expires within 7 days, does not match its key, or does not cover `PUBLIC_HOST`,
and leaves the installed files alone then.

**Confirm.** `echo | openssl s_client -connect <host>:443 -servername <host> 2>/dev/null | openssl
x509 -noout -enddate`.

## 413 on sending large attachments

`Limits:MaxRequestBodyBytes` (≈ 67 MB) or Stalwart's `maxSizeUpload`/message size limit. Raise them
together; base64 makes the JSON body 4/3 of the attachment size.

## 400 "Resource not found for the segment …"

A route the façade does not implement (Graph's own answer for unknown paths). NightMail falls back
where it can. If a feature that should work answers this, the deployed image predates it.
