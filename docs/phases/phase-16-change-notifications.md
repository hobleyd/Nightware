# Phase 16 — Change notifications

> **Status (2026-11-21): done on the local stack; option A (façade subscription store)** (v1.29.0–v1.31.0). Live needs the deployment steps below. Decisions: [ADR 0006](../adr/0006-graph-parity.md) D10.

**Goal:** a client subscribes to messages, events, contacts or To Do tasks and
receives Graph notifications at its own HTTPS endpoint.

## Step 0
- Stalwart `PushSubscription`: does it verify the push URL, and how? What is its
  maximum expiry? Can it push to the façade on the compose network?
- A background credential per user: Keycloak token exchange for a refresh
  token (the "allow refresh token" client setting, offline session), or
  a service credential per backend. Can Stalwart and Nextcloud accept one for
  an arbitrary user?
- Polling cost: `sync-collection` per subscription per interval on the local
  stack.

## Operations
| id | Request |
|---|---|
| `subscriptions.create` (+ `-events`, `-contacts`, `-tasks`) | `POST /subscriptions` |
| `subscriptions.list/get/update/delete` | `/subscriptions[/{id}]` |
| `subscriptions.validation` | Nightware → `notificationUrl?validationToken=…` (outbound) |
| `subscriptions.notification` | Nightware → `notificationUrl` (outbound) |

## Acceptance
- A subscription to the Inbox gets a `created` notification within 30 s of a
  delivery, with `clientState` echoed and the message id resolvable over Graph.
- A `notificationUrl` that resolves to a private address is refused at create.

## Findings (Step 0)
- **Background credential (decided 2026-11-21).** `nightware-api` now has
  `standard.token.exchange.enableRefreshRequestedTokenType = SAME_SESSION` (realm
  import and `keycloak/configure.sh`). Probed on the local stack: exchanging the
  subscriber's access token with `requested_token_type=…:refresh_token` returns a
  refresh token (24 h idle, the user's SSO session); a refresh grant then returns an
  access token with `aud [stalwart, nextcloud]`, which exchanges cleanly for a
  single-audience token for either. So a background scope needs only an
  `IUserContext` carrying the refreshed token; `BackendCredentials` does the rest.
  The refresh token is stored after every refresh, sealed with AES-256-GCM under
  `Subscriptions:SealingKey` (env, never the database), bound to the subscription id.
  Deleting a subscription discards the sealed token; Keycloak's revoke endpoint is
  not called, since revoking a same-session token could end the user's SSO session.
- **Lifetimes** (Graph's subscription resource page): messages, events and contacts
  up to 10,080 minutes; To Do tasks 4,230; anything under 45 minutes is raised to 45.
- **The SSRF guard** is the webhook client's `ConnectCallback`: it resolves the host,
  keeps only public unicast addresses and connects to the checked one; redirects
  off, cookies off, 5 s connect and 10 s overall. HTTPS only.
  `Subscriptions:TestOnlyAllowedOrigins` exempts exact origins for the integration
  test's loopback receiver; no deployment sets it.
- The two outbound captures (`subscriptions.validation`, `.notification`) are not
  replayed by the conformance harness (`"direction": "outbound"`); their shapes are
  tested in `Nightware.Adapters.Subscriptions.Tests`.
- Configuration is read when the store is first resolved, not at registration
  (test hosts add settings afterwards); unconfigured, `/subscriptions` is 501.

- **The notifier** (`SubscriptionPoller`, every `Subscriptions:PollInterval`,
  10 s by default) leases due subscriptions (`FOR UPDATE SKIP LOCKED`, 2-minute
  lease), renews each subscriber's session from the sealed refresh token (storing
  the new one), and polls as them in a scope whose `IUserContext` is the subscriber.
  Changes come from the same delta ports clients use; the state advances only after
  the endpoint answers 2xx (at-least-once). Failures back off (interval × 2ⁿ, at
  most an hour) without losing state.
- At creation the baseline state is read with the caller's own credential, so what
  already exists is never announced, and `last_poll` starts then.
- **created vs updated** is a heuristic: delta does not say. An item whose
  `createdDateTime` is later than the previous poll, less 2 seconds (Stalwart keeps
  whole seconds), is `created`; otherwise `updated`. An item updated within those 2
  seconds of its creation can be reported as `created` twice.
- Notifications name items `Users/{address}/Messages/{id}` (and `…/Events/`,
  `…/Contacts/`, `…/todo/lists/{list}/tasks/`), which resolve over Graph. They carry
  no `tenantId` and no etag in `resourceData`.
- `{mailbox}/messages` (every folder) is 501: mail delta is per folder, so a
  subscription names one with `mailFolders('{id}')/messages`.

- **Events** follow `sync-collection` on the default calendar (no window, no
  expansion): one change per resource under its series id, created when its
  `CREATED` is after the previous poll. New events written by the façade now carry
  `CREATED` and `LAST-MODIFIED` (Nextcloud adds neither); an event another client
  wrote without `CREATED` is reported as updated.
- **Lifecycle** notifications go only to a `lifecycleNotificationUrl`, as Graph's:
  `reauthorizationRequired` once when the subscriber's session has ended (or the
  sealing key changed), after which a PATCH with a fresh token reauthorizes;
  `missed` when a delta state has expired, after which the subscription starts again
  from now (the gap is not replayed). Expired subscriptions are removed with their
  credentials each round, without a notification (Graph sends none on expiry).

- **Polling cost.** A subscriber's renewed session is cached (under a hash of the
  refresh token) until its access token is 30 s from expiring, so a poll reuses
  the access token, and the backend tokens already exchanged from it: one refresh
  grant and one or two token exchanges per subscription per access-token lifetime
  (15 minutes), plus the backend's own delta call each `PollInterval`. The cache
  is per instance and follows `BackendCredentials:Cache` (`Enabled`,
  `ExpirySkew`). **Side effect:** the refresh grant still keeps the subscriber's
  SSO session active, so while a subscription lives the session never reaches its
  24-hour idle timeout; only the 10-day maximum (`ssoSessionMaxLifespan`) ends it,
  and then `reauthorizationRequired` follows (up to 15 minutes later, while the
  cached access token lasts). An admin who ends a session in Keycloak sees the
  same delay.
- The integration tests for the store use a throwaway Postgres (Testcontainers).
  On the colima dev machine they need `DOCKER_HOST=unix://$HOME/.colima/default/docker.sock`
  and `TESTCONTAINERS_RYUK_DISABLED=true` besides `NIGHTWARE_INTEGRATION=1`.

## Deployment
This repo's `deploy/docker-compose.yml` gains `subscriptions-db` (Postgres 17) and
the API's `Subscriptions__ConnectionString` / `Subscriptions__SealingKey`;
`init-env.sh` generates `SUBSCRIPTIONS_DB_PASSWORD` and
`NIGHTWARE_SUBSCRIPTION_KEY`. The live stack's `../ansible` has the same
(2026-11-21): `nightware-subscriptions-db` in `compose/nightware.yml`, the two
variables in `api.env` from the vault (`nightware_subscriptions_db_password`,
`nightware_subscription_key`), and both in its templated `deploy/.env`, so
`init-env.sh` generates none there. The playbook's run of `keycloak/configure.sh`
switches on same-session refresh tokens for `nightware-api`. Until the playbook
has run with a checkout that has this phase, `/subscriptions` answers 501 on live.
