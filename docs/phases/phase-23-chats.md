# Phase 23 — Chats over Nextcloud Talk

> **Status (2026-11-23): planned.** Asked for: chat, teams, channels, presence
> and calling (option 2 of three: Graph's chat surface on Talk rather than
> Talk's own clients alone, or a separate chat server). Decisions in ADR 0009.
> Phases 24–26 follow from this one; their Step 0 is here too, since one probe
> session covered all four.

## Step 0 findings (local stack, Nextcloud 35, Talk 25.0.8, 2026-11-23)
- **The caller's bearer reaches every API needed**: Talk
  (`apps/spreed/api/v4/room`, `api/v1/chat`), Teams (`apps/circles/circles`),
  core `teams/{id}/resources`, `apps/user_status/api/v1` and
  `apps/notifications/api/v2`. Talk's OpenAPI document is in the container at
  `custom_apps/spreed/openapi.json`; `apps/user_status/openapi.json` likewise.
- **One-to-one.** `POST room` `roomType=1&invite=bob` → 201 with the room
  (`type 1`, `name` the other uid, `displayName` their name). A second call
  returns the same room.
- **Chat.** `POST chat/{token}` `message`, `referenceId`, `replyTo`, `silent`,
  `threadTitle`, `threadId` → 201 with the message: `id`, `actorType`/`actorId`/
  `actorDisplayName`, `timestamp` (Unix s), `message` (Markdown, `markdown:
  true`), `messageParameters`, `systemMessage`, `messageType` (`comment`,
  `system`, `comment_deleted`), `reactions` (emoji → count), `threadId`.
  `GET chat/{token}?lookIntoFuture=0&limit=n&lastKnownMessageId=` pages history
  newest first; `X-Chat-Last-Given` is the next `lastKnownMessageId`. Pass
  `setReadMarker=0&markNotificationsAsRead=0` to leave read state alone.
- **Threads** (channels, phase 24): `threadTitle` on a send creates a thread
  whose id is the message id (`isThread: true`, `threadTitle`). Replies send
  `threadId`. `GET chat/{token}?threadId=` lists a thread (root, a
  `thread_created` system message, replies; each with `parent`).
  `GET chat/{token}/threads/recent` lists threads with first and last message
  and `numReplies`. Every message carries `threadId` (its own id when not in a
  thread); `isThread` tells them apart.
- **Teams.** `POST apps/circles/circles` `{"name"}` (JSON; the `personal`/`local`
  flags are refused) → the circle; `id` is 31 alphanumerics; the creator is
  level 9. `POST circles/{id}/members` `{"userId","type":1}` adds a user
  (level 1). A team gets a team folder (`groupfolders`, "Team spaces") mounted
  in each member's home (`nc:mount-type` `group`, own `oc:fileid`).
- **Channels.** `POST room` (JSON) `{"roomType":2,"roomName","participants":{"teams":[id]}}`
  creates a room the team's members join (bob saw it at once).
  `GET teams/{id}/resources` lists the team folder (provider `groupfolders`) and
  the room (provider `talk`, `id` the token). A custom `objectType` is refused
  (400), so a room can't be tied to a team any other way: no private channels.
- **Presence.** `GET user_status` is 404 until a status is set.
  `PUT user_status/status` `statusType` takes `online`, `away`, `dnd`, `busy`,
  `invisible`, `offline`. `PUT user_status/message/custom` `message`,
  `statusIcon`, `clearAt` (Unix s). `GET statuses/{uid}` reads another user's
  (`status`, `message`, `icon`, `clearAt`). Predefined messages: `meeting`,
  `commuting`, `be-right-back`, `remote-work`, `sick-leave`, `vacationing`.
- **Calls.** Joining needs the Talk session: `POST room/{token}/participants/active`
  sets a cookie, and `POST call/{token}` (`flags`) without it is 404. With the
  cookie: the call starts (`hasCall`, `callStartTime`), the caller's status
  becomes `busy`, "In a call", and `POST call/{token}/ring/{attendeeId}` gives
  the callee a `call` notification ("would like to talk with you"). Right after
  leaving, the status still read "In a call" (Talk reverts it later; phase 25
  must check when).
- **Users.** A Nextcloud uid is the Keycloak username; Graph user ids are
  Keycloak ids (ADR 0009 D2).

## Operations
`docs/contract/endpoints.json`, phase 23 (22 operations): chats list/get/create/
update, hide/unhide, mark read/unread, members list/get/add/remove, messages
list/get/send/update/softDelete, set/unset reaction, pinned messages
list/add/remove.

## Build notes
- Feature folder `Chats/` in every project. Talk wire types and the message
  codec (Markdown ↔ html, mentions, files, system events) live in
  `Adapters.Dav/Chats/Talk/`, because phase 24's channels reuse them.
- uid ↔ Keycloak id: a small port in `Application/Chats` (`IChatUserIds`)
  implemented in `Adapters.Directory`, memoised (ADR 0009 D2).
- Step 0 left for this phase: deleted and edited messages' exact shapes
  (`lastEditTimestamp`, the `message_deleted` system message), mention and file
  `messageParameters`, pins (`lastPinnedId`, the pin list), archive.

## Acceptance
- alice creates a one-to-one chat with bob over Graph, sends html with a
  mention, bob lists it over Graph and in Talk, reacts, alice edits and
  soft-deletes it; listings page with `$top`.
- A group chat made in Talk's web client appears in `/me/chats` with its
  members.
