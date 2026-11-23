# ADR 0009 — Chats, teams, presence and calls over Nextcloud Talk

Status: accepted (2026-11-23). Amends ADR 0006 (Context: Teams, presence and
online meetings are no longer out of scope) and ADR 0008 (a channel's files
folder is in scope).

## Context

ADR 0006 left Teams, presence and online meetings out as Microsoft-only. A Graph
client pointed at Nightware gets a 400 for `/me/chats`, `/me/joinedTeams` and
`/me/presence`. The stack already runs three Nextcloud apps that cover most of
this:

- **Talk** (`spreed` 25) has one-to-one and group conversations, chat with
  replies, threads, reactions, edits, deletes, pins and read markers, and calls
  through our own signaling and TURN servers (phase 22).
- **Teams** (`circles` 35) are Nextcloud's teams: members with levels. A team
  gets a team folder (`groupfolders`) and lists the Talk conversations that
  include it as its resources.
- **User status** (`user_status`) holds online, away, busy, do not disturb,
  invisible and offline, plus a status message with an expiry. Talk sets
  "busy, In a call" while a user is in a call.

The Step 0 findings are in `docs/phases/phase-23-chats.md`.

## Decisions

### D1 — Scope
Four phases, all `"source": "graph-parity"`:

| Phase | Graph surface | Backend |
|---|---|---|
| 23 | Chats: `/me/chats`, `/chats/{id}` with members, messages, reactions, pins and read state | Talk conversations |
| 24 | Teams and channels: `/me/joinedTeams`, `/teams/{id}` with members, standard channels, channel messages and replies, a channel's `filesFolder`, and team deletion through `/groups/{id}` | Teams (circles), Talk, team folders |
| 25 | Presence: `/me/presence`, `/users/{id}/presence`, `getPresencesByUserId`, setting and clearing presence, the preferred presence and the status message | User status |
| 26 | Online meetings (`/me/onlineMeetings`, their recordings and transcripts) and delegated calls (`/communications/calls`) that ring people | Talk |

The following stay out of scope:

- Private and shared channels. Talk has no anchor for a conversation that
  belongs to a team without including the whole team, and refuses custom room
  object types.
- Team templates other than `standard`, cloning, archiving, tabs, apps,
  installed apps, tags and schedules (Shifts).
- Hosted contents, adaptive cards and message attachments other than file
  references.
- `/chats/getAllMessages` and other application-only listings.
- The calling bot API: answering, transferring, prompts and media.
- Chat, channel and presence change notifications. They can join the phase 16
  poller later, and Talk's chat history is a usable feed for them. The phase
  that adds them amends this ADR.

These stay `501 notImplemented` where Graph has the route, and `400` where it
doesn't.

### D2 — Ids
Every id is the backend's own, so the façade stays stateless (architecture
rule 2):

- A **chat** id and a **channel** id are Talk room tokens (`^[a-z0-9]{4,30}$`).
- A **team** id is the circle id (31 alphanumerics). It never collides with a
  Keycloak group id, which is a UUID.
- A **message** id is Talk's message id in decimal. Graph's are decimal too.
  The id is unique within its conversation, and every route that names one
  also names the conversation.
- A **chat membership** id is `m.` plus the Talk attendee id. A **team
  membership** id is `m.` plus the circle member id.
- A **user** is named by Keycloak id, as `/users` does (phase 21). A Nextcloud
  uid is the user's Keycloak username (`user_oidc --mapping-uid=preferred_username`).
  The Dav adapter maps uid ↔ id through the directory (`IUserDirectory`, with
  the service account), memoised in a disposable `IMemoryCache` for 10
  minutes. An actor the directory doesn't know (a guest, a bot, a federated
  user) is emitted with its Talk display name and no `id`.

### D3 — Chats are Talk conversations
- `/me/chats` lists the caller's conversations: one-to-one (type 1) is
  `oneOnOne`, group and public (types 2 and 3) are `group`, and a room bound to
  an event (`objectType` `event`, phase 22) is `meeting`. Note to self (type 6),
  changelog (type 4) and the sample conversation are left out.
- A channel's room includes its team (D5). It is left out of `/me/chats` as
  Teams leaves channels out of chats.
- `POST /chats` creates a one-to-one room (Talk returns the existing one, as
  Graph does) or a group room with the named users. `topic` is the room name.
- Members are the room's participants of type `users`. Adding one is Talk's
  "add participant", removing is "remove attendee", and the caller leaving is
  `participants/self`. Owner and moderator participants carry
  `roles: ["owner"]`.
- `hideForUser`/`unhideForUser` are Talk's archive and unarchive.
- `markChatReadForUser` sets the read marker to the newest message, and
  `markChatUnreadForUser` is Talk's "mark unread".

### D4 — Messages are Talk chat messages
- **Body.** Talk keeps Markdown. Graph returns `html`: the façade renders the
  Markdown subset Talk's clients render (bold, italic, code, links, lists,
  quotes) and replaces `{mention-…}` and `{file…}` placeholders from
  `messageParameters`. A sent `text` body is sent as is. A sent `html` body is
  converted to Markdown: `<at id="n">` becomes Talk's `@"uid"` mention from the
  request's `mentions`, unknown tags keep their text.
- **Mentions** come from `messageParameters` of type `user` and `call`
  (`@all`, which Graph names `conversation`).
- **Files.** A shared file (`file_shared` with a `file` parameter) is an
  attachment with `contentType: "reference"`, its `name`, and `contentUrl`, the
  file's link in Nextcloud. The message's `id` addresses the driveItem through
  `/me/drive/items/{fileid}` (ADR 0008 D2).
- **Reactions** are `reactions` (`reactionType` is the emoji, as Graph's
  `unicode` reactions). `setReaction`/`unsetReaction` call Talk's reaction API.
- **Edits.** `lastEditedDateTime` is Talk's `lastEditTimestamp`. `PATCH`
  changes the body only, as Graph lets a sender do.
- **Deletes.** `softDelete` is Talk's delete. Talk keeps the message as
  `comment_deleted`, which the façade reports with `deletedDateTime` and an
  empty body, as Graph does. Talk can't restore a message, so `undoSoftDelete`
  is out of scope.
- **System messages.** `user_added`, `user_removed`,
  `conversation_renamed`, `call_ended` and `call_ended_everyone` become
  `systemEventMessage` with the matching `eventDetail`
  (`membersAddedEventMessageDetail` and so on). Every other system message
  (reactions, edits, deletes, thread creation, read-only changes) is left out
  of listings, as Teams has no counterpart.
- **Paging.** Lists are newest first. `$top` is at most 50 (Graph's limit), and
  `@odata.nextLink`'s `$skiptoken` is a Talk message id read on from with
  `lookIntoFuture=0`: the last message the page gave, or Talk's
  `X-Chat-Last-Given` when the page ends where a Talk page did. (Amended in
  phase 23: Talk's pages include bookkeeping the façade leaves out, so a Graph
  page can end inside a Talk page.)
- **Reading never moves the read marker** (`setReadMarker=0`,
  `markNotificationsAsRead=0`). Graph reads don't, and a client listing chats
  must not clear its user's notifications.

### D5 — Teams are Nextcloud teams; channels are their Talk rooms
- A **team** is a circle the caller is a member of. `/me/joinedTeams` lists
  them, `displayName` and `description` are the circle's, and `PATCH` renames
  and redescribes it. `visibility` is `public` when the circle is visible and
  open to join, otherwise `private`.
- **Creating** (`POST /teams`, `standard` template only) makes the circle, adds
  the `members` (an owner's level is admin), then creates the **General**
  channel. It answers Graph's `202 Accepted`, with `Location` naming a
  `teamsAsyncOperation` that is already `succeeded`. `GET
  /teams/{id}/operations/{opId}` reports it, with the op id encoding the team
  id, as drive copies do (ADR 0008 D4).
- **Deleting** is `DELETE /groups/{id}`, as in Graph, when the id is a team id.
  It deletes the circle and its channels' rooms. Any other group id is still
  read-only (phase 21).
- **Members.** Circle level 1 or 4 is `roles: []`, and level 8 or 9 is
  `["owner"]`. `PATCH` with `["owner"]` sets level 8, and `[]` sets level 1.
  The circle's owner (9) can't be demoted or removed. That is Graph's "last
  owner" rule, answered `400`.
- A **channel** is a group room whose participants include the team. The
  team's channels are its `talk` resources (`ocs/v2.php/teams/{id}/resources`).
  Creating one creates the room with the team as participant, so Talk adds and
  removes members as the team changes. Everything is `membershipType:
  standard`. A channel's `email` is empty.
- The **primary channel** is the team's room with the lowest Talk room id: the
  General channel made with the team, or the oldest room if General was made
  outside Nightware. It can't be deleted (`400`, as Graph).
- **Posts and replies are Talk threads.** A post is a thread's first message,
  and `subject` is the thread title. Posting without a subject uses the first
  line of the body as the title, which Talk needs, and reports `subject: null`
  (D4 body rules apply). A reply is a message with that `threadId`. A
  top-level message that is not a thread (sent from Talk's own clients) is
  listed as a post without replies.
- **`filesFolder`** is the folder named after the channel inside the team
  folder, created on first use. Its id resolves through the drive (ADR 0008 D2),
  because the team folder is mounted in each member's home.
- **Channel message delta** is a Talk chat read from the token's last message
  id. New posts and replies are upserts. Edits and deletes arrive as system
  messages naming their parent, which is re-read and reported. The first page
  lists recent posts, as Graph's does.
- *Amended (phase 24):* Talk keeps a team's rooms when its circle is deleted,
  and a team member can make a channel room the owner doesn't moderate, so
  deleting a team needs its owner moderating every channel and is `403`
  before anything is deleted otherwise. Talk refuses a thread reply to a
  top-level message that didn't start a thread, so replying to such a post is
  `400`. A subject defaulted from the body is recognised on reading by
  comparing the title with the body's first line (mentions left out), and a
  deleted post reports none. The team folder keeps the team's first name, so
  `filesFolder` is in the folder's mount point, not one named after the team.

### D6 — Presence is user status
| User status | `availability` | `activity` |
|---|---|---|
| `online` | `Available` | `Available` |
| `away` (or the `be-right-back` message) | `Away` (`BeRightBack`) | `Away` (`BeRightBack`) |
| `busy` with Talk's `call` message | `Busy` | `InACall` |
| `busy` with the `meeting` message | `Busy` | `InAMeeting` |
| `busy` | `Busy` | `Busy` |
| `dnd` | `DoNotDisturb` | `DoNotDisturb` |
| `invisible`, `offline`, or no status | `Offline` | `Offline` (`OffWork` for the caller's own `invisible`) |

- Another user's presence is read with `statuses/{uid}`, which never shows
  `invisible`.
- `setUserPreferredPresence` and `setPresence` both set the user-defined status
  (Nextcloud has one status, not a status per application). `Busy` with
  `InACall` or `InAMeeting` sets the matching predefined message. An
  `expirationDuration` sets `clearAt` on the message. Without a message it is
  not kept: Nextcloud can't expire a bare status.
- `clearUserPreferredPresence` and `clearPresence` set `online` and clear a
  predefined message the façade set.
- `setStatusMessage` sets a custom message (`content` as text, `expiryDateTime`
  as `clearAt`).
- `statusMessage.publishedDateTime` is not known and is left out.
- `outOfOfficeSettings` reads the auto-reply state from mailbox settings
  (JMAP `VacationResponse`, which the façade already serves).
- *Amended (phase 25):* another user's status (`statuses/{uid}`) carries no
  message id and its text is in the reader's language, so the `call`,
  `meeting` and `be-right-back` messages are recognised by their icons there.
  `outOfOfficeSettings` is read for the caller only: another user's mailbox is
  not the caller's to read, so others are reported not out of office. An
  expiring message clears the status with it (Nextcloud deletes the row), so
  the user then reads Offline.

### D7 — Online meetings are Talk rooms; calls ring through Talk
- An **online meeting** is a public Talk room, made as phase 22 makes an
  event's room. It is bound to its span when it has one, and named after its
  `subject`.
- Its id is `t.` plus the room token. `joinWebUrl` is `{Talk:PublicBaseUrl}call/{token}`.
  `chatInfo.threadId` is the token, which is the meeting's chat id (D3).
- `participants.attendees` that are directory users are added as room
  participants, so the meeting shows in their chats. Others still join by the
  link.
- `GET /me/onlineMeetings?$filter=JoinWebUrl eq '…'` finds a meeting by its
  link, including those made for calendar events.
- `PATCH` renames the room. A changed span unbinds it, as phase 22 does.
  *Amended (phase 26):* Talk refuses to rename a room bound to its span too, so
  a rename unbinds it first; an unbound meeting reports no span.
- `DELETE` closes the room.
- **Recordings and transcripts** are the files Talk's recording server and
  transcription upload to the meeting's folder in the organiser's files.
  `content` streams them through the drive's download path.
  *Amended (phase 26):* the files go to whoever started the recording
  (`{attachment folder}/Recording/{token}/`), so the caller's own folder is
  read; `content` answers 200 with the bytes (Graph's shape) rather than the
  drive's 302, streamed as the drive streams.
- *Amended (2026-11-23, asked for: rename and re-time a meeting and keep it
  in Talk): **a meeting's room is driven by a calendar event.*** Talk's own
  calendar integration (`CalDavEventListener`) renames a room bound to an
  event and moves its span whenever the **owner's** calendar event whose
  `LOCATION` holds the room's `/call/{token}` link changes (verified live);
  that is why Talk refuses direct renames. So:
  - Every Talk meeting has such an event in the organiser's calendar. A
    calendar event made online (phase 22) gets the link in `LOCATION`, after
    any physical location (`Boardroom, {link}`: Talk takes the token from the
    last path segment); reading strips it again, so Graph's `location` is
    unchanged. `URL` keeps the link as before (`onlineMeeting.joinUrl`, iMIP).
  - `POST /me/onlineMeetings` with a span creates the room bound to it and an
    event in the organiser's default calendar (subject, span, attendees not
    invited: the event is the meeting's record, not an invitation). Without a
    span the room is unbound and there is no event, as before.
  - `PATCH` (subject, span) updates the event; Talk renames and re-times the
    room itself. The façade never unbinds a room for a rename or a move. A
    series still unbinds (Talk's rule for `RRULE`).
  - `DELETE` deletes the event and closes the room. Deleting the event in
    any client leaves the room (Talk makes it read-only); Nightware closes it
    only when the deletion comes through Nightware, as before.
  - A meeting's span and subject are read from the room (`objectId`, name),
    which Talk keeps in step with the event.
- *Amended (2026-11-23, asked for: recordings available to everyone):*
  **recordings and transcripts belong to the conversation.** Talk keeps them in
  the files of whoever recorded and offers "Share to chat", a room share
  (`IShare::TYPE_ROOM`) every participant can open, only as a button in the
  recorder's notification; it raises no event and has no setting. A small
  Nextcloud app shipped with the stack (`deploy/nextcloud/apps/nightware_talk`)
  makes that share as soon as a recording or transcript lands in a
  `{attachment folder}/Recording/{token}/` folder, read-only, once per file.
  The recordings and transcripts routes list the conversation's shared items
  (any participant's view), so every participant gets them, whoever recorded;
  `content` streams the share as the participant sees it.
- **Calls** (`POST /communications/calls`) are delegated. Graph requires an
  application permission for them; Nightware accepts the caller's own token,
  because there is no bot.
  - The façade opens a Talk session as the caller, starts the call in the
    caller's one-to-one or group room with the targets, and rings each target
    (Talk's call notification).
  - `callbackUri` is accepted and never called.
  - Media flows only between Talk's own clients. The call carries the room's
    join link (`@nightware.joinWebUrl`, an instance annotation) for the
    caller's client to open.
  - The call id seals the room token and the Talk session (cookie and session
    id, AES-GCM under `Calls:SealingKey`), so `GET` and `DELETE` work without
    stored state. *Amended (phase 26):* only the session's two cookies (never
    Nextcloud's `nc_token` login) and a hash of the session id are sealed, bound
    to the caller's id; the call starts silently and only the targets are rung.
  - `GET` reports `establishing` while only the façade's session is in the
    call, `established` once anyone else has joined, and `terminated` after it
    ends.
  - `DELETE` leaves the call and ends it for everyone when the caller moderates
    the room. A session nobody hangs up is dropped by Talk.
    *Amended (phase 26):* Talk drops it when anything lists the room's
    participants more than 100 s after the join, ending a call nobody answered,
    so reading a call never lists them (phase 26 Step 0).

### D8 — The OCS client grows
`IDavClient` (frozen `Core/`) gains `OcsExchangeAsync(OcsRequest)`. It takes a
JSON or form body, query, and extra request headers, and returns `ocs.data`
with the response's headers, its HTTP status and `Set-Cookie` values. An OCS
failure keeps its `data` (Talk puts the reason there, for example `{"error":
"permissions"}`) in the error message. `OcsGetAsync` and `OcsSendAsync` stay
as thin wrappers.

### D9 — Deployment
`deploy/nextcloud/configure.sh` enables `circles`, `user_status` and
`notifications` alongside `spreed` and `groupfolders`. Phases 23–25 need only
Talk's OCS API. Phase 26's calls also need the HPB the stack already runs.

## Consequences
- The contract gains phases 23–26, with every fixture constructed from
  Microsoft's reference pages, as phase 20's were. Capture scenarios need a
  Teams-licensed test user.
- Chats and channels a user makes in Talk's own clients appear over Graph, and
  the reverse, because there is no façade copy.
- Presence reflects Talk's call state with no extra plumbing.
- A Graph client written for Teams will find standard channels only, no
  apps or tabs, and calls whose media opens in Talk.
