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

## Everyone gets 401

**Cause.** Tokens fail validation: issuer mismatch (`Auth:Authority` ≠ the token's `iss`, e.g.
after changing `BASE_DOMAIN` or `PUBLIC_PORT_SUFFIX`), the façade cannot fetch signing keys
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

**Confirm.** `docker compose -p nightware ps stalwart`, `docker compose -p nightware logs stalwart`.

**Fix.** Restart Stalwart (`docker compose -p nightware restart stalwart`). The façade recovers on
the next request — no restart, nothing cached to clear. Clients' delta links stay valid.

## Nextcloud is slow

**Symptoms.** Calendar/contacts/tasks calls take seconds; beyond 25 s reads answer 503 +
`Retry-After` and writes 504 (`nightware.request.timeouts` rises). `http.client.request.duration`
for `IDavClient` p95 climbs.

**Confirm.** Nextcloud `occ status`, PHP-FPM/Apache worker saturation, Postgres slow queries,
Redis reachability (`docker compose -p nightware logs nextcloud nextcloud-redis`), background jobs
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
(`docker compose -p nightware logs stalwart | grep rate-limit-exceeded`).

**Fix.** `deploy/stalwart/configure.sh` (run by `bootstrap.sh`, idempotent) scopes that throttle to
**unauthenticated inbound only** (`is_empty(authenticated_as)`); authenticated submissions —
everything sent through the façade — are no longer counted. Re-run it on stacks bootstrapped before
that change. Still subject to the throttle: external senders and **Nextcloud's iMIP invitations**,
which arrive unauthenticated on port 25 — a burst of more than 25 invitations an hour from
Nextcloud to one attendee is throttled. Raise or re-key that throttle in Stalwart's settings
(*Settings → MTA → Inbound → Throttles*) if your organisation schedules at that rate.

## 429 on mail calls with several devices (Stalwart's concurrent-request limit)

**Cause.** Stalwart allows `maxConcurrentRequests` (4 by default) JMAP requests in flight per
account. When more than four devices of one user (or a device's parallel fetches) hit mail at the
same moment, Stalwart answers a JMAP `limit` problem, which the façade reports as
`429 TooManyRequests` with `Retry-After: 5`. NightMail waits and retries, so users rarely notice;
the k6 run with ten devices per account saw 4 of 80 delta calls throttled this way.

**Confirm.** The façade's own limiter does not log; Stalwart's limit shows as `Result: '400'` on the
`Nightware.Jmap-jmap-api` pipeline (raise `Logging:LogLevel:Polly` to `Information`) with
`maxConcurrentRequests` in the error.

**Fix.** Raise Stalwart's concurrent-request limit for JMAP (Settings → JMAP → Protocol → request
limits) to cover the devices per user you expect (e.g. 16).

## 413 on sending large attachments

`Limits:MaxRequestBodyBytes` (≈ 67 MB) or Stalwart's `maxSizeUpload`/message size limit. Raise them
together; base64 makes the JSON body 4/3 of the attachment size.

## 400 "Resource not found for the segment …"

A route the façade does not implement (Graph's own answer for unknown paths). NightMail falls back
where it can. If a feature that should work answers this, the deployed image predates it.
