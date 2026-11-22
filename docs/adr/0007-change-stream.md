# ADR 0007 — A change stream for NightMail

Status: accepted (2026-11-22). Builds on ADR 0006 D10 (change notifications).

## Context

NightMail polls each account on a timer. Graph change notifications (phase 16)
call a public HTTPS `notificationUrl`, which a desktop behind NAT or a phone
cannot offer: the SSRF guard refuses private and loopback addresses, the
validation handshake must be answered within 10 s, and a sleeping laptop misses
deliveries. They also keep a sealed refresh token per subscription and need one
subscription per mail folder.

## Decisions

### D1 — The client holds the connection
`GET /nightware/v1/changes` is a Server-Sent Events stream the client opens
with its own bearer token. It lives beside `/v1.0`, not in it: it is not Graph
surface, so it is not in `endpoints.json`, the Graph fallback and rate limiter
never see it, and a generic Graph client is unaffected. Discovery advertises it
as the optional `changesUrl` (derived from `graphBaseUrl` unless configured).

### D2 — Signals, not data
An event names the areas that changed (`mail`, `calendar`), never items. The
client already has delta sync for each; a signal only says when to run it. That
keeps the stream free of Graph shapes, ids and `$select`, and a missed signal
costs only latency, since the client still polls on a long timer.

### D3 — Poll backend state as the caller, store nothing
Each `ChangeStream:PollInterval` (10 s) the stream reads one opaque state per
area (`IChangeStateProbe`): mail is the JMAP `Email` and `Mailbox` states from a
`/get` of no ids, one request; calendar is the event calendars' sync tokens
from the calendar-home listing. A state that moves is a signal. The first read
is a baseline, not a signal. A transient failure keeps the last state; an area
the mailbox lacks is dropped; a refused credential ends the stream.

Rejected: proxying Stalwart's JMAP EventSource (push, but mail only, and a
second long-lived backend connection per client); reusing the subscription
store (needs a stored credential and is per folder).

### D4 — Bounded by the token
Every read uses the caller's token, so the stream ends 30 s before its `exp`
(and after `ChangeStream:MaxDuration`, 30 min) with `event: end`; the client
reconnects with a refreshed token. The request budget does not apply to it
(`LongLivedRequest` metadata). A user may hold `ChangeStream:MaxStreamsPerUser`
(8) streams per instance.

## Consequences
- Signals arrive within ~10 s instead of at NightMail's next poll.
- Backend load per open stream: one JMAP request and one PROPFIND per interval.
- Contacts and To Do can join as further probes without changing the wire.
