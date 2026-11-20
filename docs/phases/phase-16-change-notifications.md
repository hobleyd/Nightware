# Phase 16 — Change notifications

> **Status (2026-11-20): planned; option A (façade subscription store).** Decisions: [ADR 0006](../adr/0006-graph-parity.md) D10.

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
