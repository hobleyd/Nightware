# Phase 23 — Chats over Nextcloud Talk

> **Status (2026-11-23): done, local only.** Asked for: chat, teams, channels,
> presence and calling (option 2 of three: Graph's chat surface on Talk rather
> than Talk's own clients alone, or a separate chat server). Decisions in ADR
> 0009. Phases 24–26 follow from this one; their Step 0 is here too, since one
> probe session covered all four. All 22 operations are served; every fixture
> is constructed from Microsoft's reference page (no Teams tenant to capture).

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

## Step 0, completed for this phase (Talk 25.0.8, 2026-11-23)
Probed with alice and bob in a fresh group room; the answers are recorded in
`tests/Nightware.Adapters.Dav.Tests/Fixtures/Chats/` (host rewritten to
`cloud.example.com`).
- **Edits.** `PUT chat/{token}/{id}` `message` answers with the
  `message_edited` system message, whose `parent` is the edited message. The
  message then carries `lastEditActorType/Id/DisplayName` and
  `lastEditTimestamp`.
- **Deletes.** `DELETE chat/{token}/{id}` answers with the `message_deleted`
  system message. The deleted message stays as `messageType: comment_deleted`,
  `deleted: true`, with text "Message deleted by you" and **no deletion time**:
  only the `message_deleted` event (whose `parent` names it) has one.
- **Mentions.** `@"bob"` and `@all` in a sent message come back as
  `{mention-user1}` (`type user`, `id` the uid, `mention-id`) and
  `{mention-call1}` (`type call`, `id` the token, `name` the room name).
  `messageParameters` is `[]` (PHP's empty map) when there are none, and so is
  `reactions`.
- **Files.** Sharing a file into the room (`files_sharing` `shareType=10`,
  `shareWith` the token) posts a **comment** (no `file_shared` system message
  reaches clients) with text `{file}` and a `file` parameter: `id` (the file
  id), `name`, `size`, `path`, `link` (`{public}/f/{id}`), `mimetype`, `etag`.
- **Reactions.** A message carries `reactions` (emoji → count) and, for the
  reader's own, `reactionsSelf`. Who reacted and when is only
  `GET reaction/{token}/{id}`. Each reaction adds a `reaction` system message.
- **Replies.** `replyTo` makes a message with `parent`; its `threadId` is the
  parent's thread.
- **Pins.** `POST chat/{token}/{id}/pin` (moderators only; `pinUntil`
  optional) posts `message_pinned`; `DELETE …/pin` unpins. The room has
  `lastPinnedId`/`hiddenPinnedId`; the list is the shared-items call
  `GET chat/{token}/share?objectType=pinned`, a map keyed by message id with
  `metaData.pinnedAt`/`pinnedActor…`.
- **Archive.** `POST`/`DELETE room/{token}/archive`; an archived room is still
  listed, with `isArchived: true`.
- **Read state.** `POST chat/{token}/read` without `lastReadMessage` moves the
  marker to the newest message; `DELETE chat/{token}/read` marks the chat
  unread. The room's `unreadMessages` leaves out the reader's own messages.
  `GET …/context` does not move the marker.
- **One message.** There is no single-message GET. `GET
  chat/{token}/{id}/context?limit=0` answers the message **or the nearest
  older one** (60 gave 59, 99999 gave the newest), so the id must be checked.
- **End of history.** `GET chat/{token}` past the oldest message answers
  **304 with no body**; so does `…/context` for an id older than the room.
- **Room list.** Talk's room has no creation time. `noStatusUpdate=1` keeps a
  read from setting the caller online. A team's channel can't be told from a
  group chat in the room list (`objectType` is empty); only the team's
  resources (`GET teams/{id}/resources`, provider `talk`) name it.
- **Participants.** `POST room/{token}/participants` answers `[]`; the new
  attendee id must be read back, and it changes each time someone is re-added.
  Types: 1 owner, 2 moderator, 3 user, 4 guest, 5 self-joined, 6 guest
  moderator. `DELETE room/{token}/attendees?attendeeId=` removes someone
  else; the caller leaves with `DELETE …/participants/self`.
- **System messages.** `user_added`/`user_removed` (`actor`, `user`),
  `conversation_renamed` (old and new name only in the rendered text),
  `call_ended` (`user1…n`; the duration only in the text, "(Duration 0:14)",
  and the actor the guest `guest/system` when Talk ends it).

## Operations
`docs/contract/endpoints.json`, phase 23 (22 operations): chats list/get/create/
update, hide/unhide, mark read/unread, members list/get/add/remove, messages
list/get/send/update/softDelete, set/unset reaction, pinned messages
list/add/remove. Every chat route is served under `/chats/{id}`,
`/me/chats/{id}` and `/users/{caller}/chats/{id}`, as Graph's are (the
contract's notes say so); `POST /chats` is root only.

## Done
| Gap | Version |
|---|---|
| Chats over Talk: the caller's chats (one-to-one, group, meeting), create, rename, hide, read state; members; messages with html bodies, mentions, file references, reactions, edits, soft deletes and system events; pinned messages | 1.65.0 |

Verified live on the dev stack (`tests/Nightware.Integration.Tests/Chats/ChatLiveTests.cs`,
in-process façade against Talk): alice opens the one-to-one chat with bob (Talk's
existing room, twice), sends html mentioning bob; bob lists it (with
`lastMessagePreview`) without his read state moving, reacts (`like` → 👍) and
unreacts; alice edits and soft-deletes; `$top=2` pages follow
`@odata.nextLink`. A group room made directly in Talk is listed for bob with
its members and alice as owner, shows the members-added and rename events,
is renamed, pins and unpins, is marked read and unread, hidden and shown,
and bob leaves, is re-added under a new membership and removed. A group chat
made over Graph gets its topic and owners.

## Decisions beyond ADR 0009
- **Code layout for phase 24.** `Adapters.Dav/Chats/Talk/` holds everything
  conversation-generic: `TalkChatClient` (every Talk call, token-checked,
  optional `threadId`/`threadTitle`), the wire records, `TalkMessageCodec`
  (pure: Talk message → Graph `ChatMessage`, draft → Talk Markdown), the
  Markdown/html converters, `TalkMessageFeed` (pages, single messages,
  reactions, identities), `TalkIdentities` and `TalkLinks`. `TalkConversation`
  says chat or channel (`channelIdentity`, `replyToId` = the thread, `subject`
  = the thread title). Chat-only code (which rooms are chats) is in `Chats/`.
- **Paging** (amends ADR 0009 D4). Talk pages include its bookkeeping, so a
  Graph page reads up to five Talk pages of 100 to fill `$top`. `$skiptoken`
  is the id of the last message given (Talk's `X-Chat-Last-Given` when a page
  ends on a Talk page), read as `lastKnownMessageId`. A long run of bookkeeping gives a short page
  with a link. The chat list is one Talk call, paged by offset.
- **Talk's 304** ("nothing more") is a success without data from
  `OcsExchangeAsync` (changed at merge; it first reached the adapter as an
  unknown error), so it reads as an empty page.
- **Times Talk doesn't keep.** `createdDateTime` of a chat is `null`.
  `viewpoint.lastMessageReadDateTime` is the newest message's time when the
  read marker is on it, otherwise `null`. A deleted message's
  `deletedDateTime` is its `message_deleted` event's time when that event is
  in the same read, otherwise its edit or send time.
- **Localised text.** A rename's new name and a call's duration are parsed
  from Talk's English text; another language leaves them `null`.
- **Who.** Talk people become directory identities in one batch per read
  (`IUserAccountNames`, memoised). Talk itself (`guest/system`) and bots are
  `application` identities, guests `anonymousGuest` users. Only the directory
  failing fails a read; an unknown uid is a name without an id.
- **Writes.** A one-to-one chat ignores `roles`; a group chat's owners are
  promoted to Talk moderators. An untitled group chat is named after its
  members (Talk requires a name), and that name is its topic.
  `markChatUnreadForUser`'s `lastMessageReadDateTime` is ignored.
  `setReaction` takes an emoji or Graph's legacy names (`like`, `heart`,
  `laugh`, `surprised`, `sad`, `angry`). `importance` is accepted and
  ignored; it is always `normal`. A chat message's `subject` is ignored.
- **Mentions** of someone the directory doesn't know are a `400`; a mention
  of the conversation is `@all`.
- **Channels by id.** A channel's room is left out of `/me/chats` (by the
  caller's teams' resources; if Teams is unavailable, channels are listed as
  group chats rather than failing the list), but `/chats/{token}` still
  answers for it.
- **webUrl** is `{Talk:PublicBaseUrl}call/{token}` (a message adds
  `#message_{id}`); without `Talk:PublicBaseUrl` it is `null`, and chats
  still work. The Directory adapter's map is optional in the Dav adapter
  (people then have names and no ids), so the adapter composes on its own.
- **Shapes checked against the reference pages.** An added member answers
  `201` with `Location` and no body. `@odata.type` is written on a message's
  sender and reactions' users, on members and pins, and not on an event's
  members or initiator, nor a mentioned conversation (as Graph's examples).
  `isHiddenForAllMembers` is always `false`. A reaction's `displayName` (the
  emoji's name) and `reactionContentUrl` are `null`. `tenantId`, `summary`,
  `locale`, `policyViolation`, `messageHistory`, `onBehalfOf` and hosted
  contents are not emitted.

## Known gaps
- A reply in a chat (Talk's `replyTo`) is listed as a plain message: Graph
  shows it as a `messageReference` attachment, which ADR 0009 D1 leaves out.
- Pinning is a Talk moderator's action: a member who isn't an owner gets 403.
- Reading who reacted costs one Talk call per reacted message in a page.
- Listing chats costs one call per team the caller is in (to leave channels
  out).
- `$filter`, `$orderby` other than newest first, and `$count` on chats and
  messages are `400`.

## Acceptance
- alice creates a one-to-one chat with bob over Graph, sends html with a
  mention, bob lists it over Graph and in Talk, reacts, alice edits and
  soft-deletes it; listings page with `$top`. **Done** (live test).
- A group chat made in Talk's web client appears in `/me/chats` with its
  members. **Done** (live test, made through Talk's OCS API as its client does).
