# Operating Nightware

Nightware is a stateless Graph façade: it stores nothing of record. Operating it means operating
three stateful backends behind it — Keycloak (identity), Stalwart (mail), Nextcloud (calendar,
contacts, tasks, files) — plus the façade container and Caddy in front.

| Document | Read it when |
|---|---|
| [deployment.md](deployment.md) | Standing up a real deployment: domain, TLS, secrets, the façade container |
| [configuration.md](configuration.md) | Looking up any setting: key, environment-variable form, default, whether it is a secret |
| [security.md](security.md) | Reviewing the hardening: auth, CORS, limits, headers, log redaction, cookies |
| [monitoring.md](monitoring.md) | Wiring health checks, OpenTelemetry and alerts |
| [runbook.md](runbook.md) | Something is wrong: 503s, a backend is down or slow, clock skew, 429s |
| [backup-restore.md](backup-restore.md) | Backing up and restoring Stalwart, Nextcloud and Keycloak |
| [upgrades.md](upgrades.md) | Upgrading any image, including Keycloak scope/claim changes |
| [ci.md](ci.md) | The Makefile, the GitHub Actions workflow and the coverage gate |

## The one rule NightMail imposes on operations

**The Keycloak token endpoint must always answer like Keycloak.** NightMail treats *any* HTTP
response to a token refresh that is not a token — including a reverse proxy's 502/503 while
Keycloak restarts, or a maintenance page — as revoked credentials, and makes the user sign in again
on every device. Network errors (no response at all) are retried; HTTP errors are not.

So: run Keycloak highly available, never put a maintenance page or a "service unavailable" response
in front of `auth.<domain>`, restart it only with a second instance serving, and do upgrades
(see [upgrades.md](upgrades.md)) with that in mind. The façade, Stalwart and Nextcloud can all
answer 503 safely — NightMail retries those.
