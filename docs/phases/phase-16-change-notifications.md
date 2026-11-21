# Phase 16 — Change notifications

> **Status (2026-11-21): in progress; option A (façade subscription store)** — 16a (store, CRUD, handshake, SSRF guard) done. Decisions: [ADR 0006](../adr/0006-graph-parity.md) D10.

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

## Deployment (not done: the live stack is ansible's)
This repo's `deploy/docker-compose.yml` gains `subscriptions-db` (Postgres 17) and
the API's `Subscriptions__ConnectionString` / `Subscriptions__SealingKey`;
`init-env.sh` generates `SUBSCRIPTIONS_DB_PASSWORD` and
`NIGHTWARE_SUBSCRIPTION_KEY`. For the live stack, `../ansible` needs:
- the `subscriptions-db` service and volume in
  `roles/docker_apps/files/compose/nightware.yml`, with the API depending on it;
- the two `Subscriptions__*` variables on `nightware-api`, from new vault entries
  for the database password and the sealing key (32 bytes hex);
- `keycloak/configure.sh` run on the VM (it switches on same-session refresh tokens
  for `nightware-api`).
Until then `/subscriptions` answers 501 on live.
