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
An event names the areas that changed (`mail`, `calendar`, `chat`), never items. The
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

### D5 — Chats (2026-11-25)
`chat` covers the caller's Talk conversations that Graph shows as chats or
channels (ADR 0009 D3). Its state is the one room listing
(`noStatusUpdate=1`, so a watching client does not keep the user "online"):
per conversation, its newest message id, the caller's read marker and unread
count, and its name. A message posted, a conversation read elsewhere, renamed,
created, joined or left is a signal. An edit, a deletion or a reaction is not:
Talk posts those without moving the room's last message, and finding them
costs a read per conversation (what the Graph subscriptions do, ADR 0009 D11),
too much every 10 s per open stream. A client's open conversation is polled
anyway.

Chats belong to the signed-in user, so a stream for a shared mailbox has no
`chat` area. Talk refusing the caller (a user Nextcloud has no account for)
drops the area instead of ending the stream, so mail and calendar signals
carry on.

## Consequences
- Signals arrive within ~10 s instead of at NightMail's next poll.
- Backend load per open stream: one JMAP request, one PROPFIND and one Talk
  room listing per interval.
- Contacts and To Do can join as further probes without changing the wire; a
  client ignores an area it does not know.
