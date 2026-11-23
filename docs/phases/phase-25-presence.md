# Phase 25 — Presence over Nextcloud user status

> **Status (2026-11-23): done, local only.** A Graph client reads anyone's
> presence and sets the caller's own over Nextcloud's user status: 8 operations,
> decisions in ADR 0009 D6 (amended below). No tenant captures were possible:
> every fixture is constructed from Microsoft's reference page and says so.

## Step 0 findings (local stack, Nextcloud 35, Talk 25.0.8, 2026-11-23)
The probes of phase 23 (`docs/phases/phase-23-chats.md`) plus these, with bob's
token through OCS and a read of `apps/user_status` and Talk's `lib/Status`:
- **Two views.** `GET user_status` (the caller's own) has `messageId`,
  `messageIsPredefined` and `statusIsUserDefined`. `GET statuses/{uid}`
  (anyone's, also the caller's) has only `status`, `message`, `icon`,
  `clearAt`, never `invisible` (it reads `offline`), and no `messageId`. A
  predefined message reaches another user as text in the reader's language
  plus its icon: 💬 call, 📅 meeting, ⏳ be right back.
- **No status.** An unknown uid gets 404 from `statuses/{uid}` ("No status
  for the requested userId"). A real user with no row (never set) takes the same
  path in the controller and is read as 404 too; that is from the code, as no
  never-signed-in user was available to probe. The façade reads either as no
  status (Offline).
- **"In a call".** Talk sets `busy` + `call` (or keeps `dnd`, and never
  overrides `invisible`) when a participant's in-call flag goes up, backing up
  the previous status (`_uid` row); it restores the backup the moment the
  participant leaves the call or the call ends for everyone (verified: bob's
  status went back to offline on `DELETE call/{token}`). No background job
  reverts it. A session that never leaves keeps the status: alice's stuck
  "In a call" came from three calls whose sessions had not pinged for up to
  ten hours (they leave only when someone lists the room's participants, or
  after 24 h when `CleanupStaleSessions` deletes them without events, i.e.
  without a revert), and from a backup row that itself held `busy`/`call`, so
  each revert restored "In a call" again. Setting a status, predefined or
  custom message through the API deletes the backup row.
- **Automatic away.** Nextcloud's web client sends `PUT heartbeat`
  (`online`/`away`); that status is not user-defined. A non-user-defined
  status, and a user-defined `online`, decays to `offline` 15 minutes after the
  last heartbeat (`ClearOldStatusesBackgroundJob` and the read path; read from
  the code, not waited out). `away`, `busy`, `dnd` and `invisible` set by the
  user stay.
- **`clearAt`** applies to predefined messages too (`PUT message/predefined`
  `messageId=meeting&clearAt=…` kept it); without one, a predefined message has
  no expiry (the predefined list's default periods are the client's to apply).
  When `clearAt` passes, Nextcloud clears the message **and the status**: bob's
  `dnd` with an expiring custom message read `offline` with no message right
  after it. The first read after expiry rewrites the row (offline, no message,
  no `clearAt`); the 60-second job deletes rows only if nothing read them first,
  and in two probes something always had, so the row stayed as `offline`.
- Set responses carry the raw row (`message: null` for a predefined message),
  so the façade re-reads rather than parsing them.

## Operations
All `"source": "graph-parity"`, `{mailbox}` routes under `/me` and `/users/{id}`.

| Operation | Route | Over |
|---|---|---|
| `presence.get` | `GET {mailbox}/presence` | `user_status` (caller) or `statuses/{uid}` |
| `presence.get-by-id` | `GET /communications/presences/{id}` | as above |
| `presence.get-by-ids` | `POST /communications/getPresencesByUserId` | one read per id |
| `presence.set` | `POST {mailbox}/presence/setPresence` | `PUT status` (+ predefined message) |
| `presence.clear` | `POST {mailbox}/presence/clearPresence` | `PUT status online` (+ `DELETE message`) |
| `presence.set-preferred` | `POST {mailbox}/presence/setUserPreferredPresence` | as `setPresence` |
| `presence.clear-preferred` | `POST {mailbox}/presence/clearUserPreferredPresence` | as `clearPresence` |
| `presence.set-status-message` | `POST {mailbox}/presence/setStatusMessage` | `PUT message/custom` (or `DELETE message`) |

## Done
| Gap | Version |
|---|---|
| Presence: read (three routes and by ids), set and clear (session and preferred), status message | 1.64.0 |

Verified live on the dev stack (`tests/Nightware.Integration.Tests/Presence/PresenceLiveTests.cs`,
`NIGHTWARE_INTEGRATION=1`): bob sets DoNotDisturb (`/users/{his id}`) and a
status message with an expiry over Graph; bob's own presence carries the
expiry; alice reads DoNotDisturb and the message (no expiry) through
`/users/{id}/presence`, `/communications/presences/{id}` and
`getPresencesByUserId` (an unknown id left out, her own id included); Nextcloud's
own `user_status` reads `dnd` with the same message; alice setting bob's
presence is 403; bob's `Busy`/`InAMeeting` reads as such to alice (by the
icon); clearing reads Available with no message. In a Talk call bob started
(room created, session joined, `POST call`), alice reads bob Busy/InACall with
no status message, and not InACall once he leaves. bob is reset to online with
no message in `finally`. Bob's always-on automatic replies read
`isOutOfOffice: true` with the internal message on his own presence and
`false` to alice; his previous auto-reply setting is restored in `finally`.
Run with `NIGHTWARE_INTEGRATION=1 NIGHTWARE_DEPLOY_DIR=<main checkout>/deploy
NIGHTWARE_CONNECT_ADDRESS=127.0.0.1` (the host name does not resolve).

## Decisions
See ADR 0009 D6. Beyond it:
- **Who is "own".** The caller's status is read from their private view
  however the route names them (`/me`, `/users/{own id or address}`,
  `/communications/presences/{own id}`, or their id in `getPresencesByUserId`):
  that is the view with the message id and `invisible`. Anyone else is resolved
  id → uid → directory user (`IUserAccountNames`) and read publicly; the
  presence `id` is the directory id.
- **Another user's activity message is recognised by its icon** (D6 amended),
  since the public view has no `messageId` and its text is localised. A custom
  message with one of those icons reads as that activity for other users; the
  caller's own uses the message id.
- **Status message.** Any message that doesn't stand for an activity (call,
  meeting, be right back) reads as `statusMessage` (`contentType: text`), with
  its icon leading the text when it has one. `expiryDateTime` is `clearAt` in
  UTC, for the caller only, as Graph omits it for others.
- **`outOfOfficeSettings`** is the caller's own auto-reply (`IAutoReplyStore`,
  JMAP): on when always enabled, or scheduled and now inside the window;
  `message` is the internal reply. Another user's mailbox is not the caller's
  to read, so others are reported `isOutOfOffice: false` (D6 amended). A mail
  server that can't answer leaves the caller "not out of office" rather than
  failing presence.
- **Accepted pairs** for both set operations: the union of Graph's
  `setPresence` and `setUserPreferredPresence` tables plus `Busy`/`InAMeeting`;
  `InAConferenceCall` sets the `call` message, `Presenting` is `dnd`,
  `Offline`/`OffWork` is `invisible`. Others are 400. `setPresence` needs
  `sessionId` and takes `expirationDuration` from PT5M to PT4H, as Graph;
  `clearPresence` needs `sessionId` (never checked against anything).
- **Setting** reads the caller's status first: a pair without a message
  clears an activity message an earlier pair (or Talk, or the calendar) set,
  and leaves a custom message alone. Clearing sets `online` the same way.
- **`setStatusMessage`** with empty content clears the message; an `html`
  body is reduced to text; a past `expiryDateTime` is 400.
- Write routes for anyone but the caller are 403 before any request.
- `getPresencesByUserId` reads eight ids at a time, keeps the request's order,
  drops duplicates, leaves out ids the directory doesn't know, takes at most
  650 ids, and fails on any other error.

## Known gaps
- Nextcloud has one status per user, so a Graph "presence session" is not
  separate from the user's own choice in Nextcloud, and `clearPresence` can't
  404 for a session that doesn't exist.
- A status set without a Nextcloud client open: `online` decays to `offline`
  after 15 minutes (Nextcloud's heartbeat rule); other types stay.
- An expiring message (`expirationDuration` on an activity pair, or a status
  message's `expiryDateTime`) takes the status with it when it expires: the
  user reads Offline, not their previous status.
- A bare status can't expire (no message to carry `clearAt`), so
  `expirationDuration` is accepted and not applied then.
- Graph's default expiry (5 minutes for `setPresence`, a day or a week for the
  preferred presence) is not applied: without `expirationDuration` a status, and
  an activity message such as `InACall`, stays until it is changed or cleared
  (no Talk event reverts a façade-set "In a call").
- Another user's out-of-office state isn't reported.
- `sequenceNumber`, `workLocation` and `statusMessage.publishedDateTime` are
  not emitted.
- A Talk session that never leaves its call keeps "In a call" (see Step 0).
