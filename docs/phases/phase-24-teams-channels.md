# Phase 24 — Teams and channels over Nextcloud Teams and Talk

> **Status (2026-11-23): done, local only.** Decisions in ADR 0009 D5 (amended
> below for what Step 0 showed); the shared Step 0 is in phase 23. All 34
> operations are served; every fixture is constructed from Microsoft's
> reference page (no Teams tenant to capture).

## Operations
`docs/contract/endpoints.json`, phase 24 (34 operations): joined teams, team
get/create/update/delete (`DELETE /groups/{id}`), the create operation's status,
members list/get/add/update/remove, channels list/get/create/update/delete,
the primary channel, a channel's `filesFolder` and members, channel messages
list/get/send/update/softDelete/reactions/delta, replies list/get/send/update/
softDelete/reactions. The create operation is also served at Graph's own
`Location` form, `/teams('{id}')/operations('{opId}')`.

## Step 0 findings (Nextcloud 35, circles 35, Talk 25.0.8, 2026-11-23)
Probed as alice and bob with throwaway teams, since deleted. Answers recorded
in `tests/Nightware.Adapters.Dav.Tests/Fixtures/Teams/` (host rewritten to
`cloud.example.com`).
- **Circles API** (`apps/circles/appinfo/routes.php`): `POST circles` `{name,
  createTeamFolder}`; `PUT circles/{id}/name|description|config` `{value}`;
  `GET|POST circles/{id}/members` (`{userId, type: 1}`); `PUT
  …/members/{memberId}/level` `{level}`; `DELETE …/members/{memberId}`; `PUT
  circles/{id}/leave`; `DELETE circles/{id}`. There is no single-member GET.
  A member id is 31 alphanumerics, like a circle id. The creation answer has
  `creation: 0` and `population: 0`; the listing has the real time.
- **The caller's membership** is each listed circle's `initiator` (`id`,
  `userId`, `level`).
- **Errors.** A circle the caller isn't in (or one deleted) answers **403**, and
  the controllers throttle 403s (brute-force protection), so the adapter finds
  a team in the caller's own list. A member editing the team gets 403.
  Demoting the level-9 owner answers 400 "Member cannot be Owner"; removing it
  as an admin answers 403.
- **Visibility.** `config` flags `CFG_VISIBLE` (8) and `CFG_OPEN` (16):
  `PUT …/config {"value": 24}` makes a team visible and open (`public`); 0 is
  the default (`private`). `CFG_SINGLE`/`PERSONAL`/`SYSTEM`/`HIDDEN`/`BACKEND`
  mark circles that are not teams.
- **Team folder.** `GET apps/circles/teams/{id}/folder` gives the group folder
  (`id`, `mountPoint`); `teams/{id}/resources` lists it too (provider
  `groupfolders`, `id` the group folder id, not a file id). The folder is
  mounted at `/{mountPoint}` in every member's home (`nc:mount-type group`,
  its own `oc:fileid`), and the drive's file-id `SEARCH` finds it for both
  alice and bob, so `/me/drive/items/{fileid}` resolves. **Renaming the team
  does not rename the folder**: the mount point keeps the creation-time name.
- **Deleting a circle** keeps its Talk rooms (with their participants) but
  removes its team folder (the group folder is gone from
  `groupfolders:list`). The façade deletes the rooms first.
- **Rooms.** `POST room` takes `description` with `participants.teams`; `PUT
  room/{token}/description` changes it. A team **member** can create a team
  room; the team's owner is then only a user (participant type 3) in it and
  can't delete it.
- **Threads.** `threadId` on a message that is not a thread answers **400
  `{"error": "reply-to"}`**, and `GET chat/{token}?threadId=` of such a message
  is 404: Talk doesn't make it a thread. A `replyTo` outside threads gives the
  reply the parent's id as `threadId` but no `isThread`. A message in a thread
  has `isThread: true` and the thread's `threadTitle`.
- **Renaming a thread** is `PUT chat/{token}/threads/{threadId}` `threadTitle`;
  it answers the thread (`thread`, `first`, `last`) and posts a
  `thread_renamed` system message (parent: the first message).
- **Deleting a thread's first message** works like any delete; replies can
  still be posted to the thread.
- **Thread titles** longer than 200 characters are accepted and cut to 200
  and an ellipsis.
- **Newer messages.** `GET chat/{token}?lookIntoFuture=1&timeout=0` answers at
  once, oldest first, or 304 when nothing is newer. `message_edited`,
  `thread_renamed` and reactions carry the changed message as `parent` (with
  `isThread`).

## Done
| Gap | Version |
|---|---|
| Teams and channels over Nextcloud Teams and Talk: joined teams, create (202 + operation), update, delete through `/groups/{id}`; members; standard channels with the primary one, files folder and members; posts and replies (Talk threads) with subjects, edits, soft deletes, reactions and a delta | set at merge |

Verified live on the dev stack (`tests/Nightware.Integration.Tests/Teams/TeamLiveTests.cs`,
in-process façade against Nextcloud): alice creates a team with bob over Graph
(202, the operation reads `succeeded`); both list it, and bob's circles list
has it; it is redescribed and made public; bob is made an owner and back, and
can't remove alice (400). General exists and is the primary channel (can't be
deleted). alice adds a channel and posts with a subject; bob replies from
Talk's own API and over Graph and reacts; replies list and read back; a post
without a subject reports none; `$expand=replies` brings the thread; a later
edit and subject change, the Talk reply and a new post show in the delta, whose
next link is then empty. Both get the same `filesFolder` and list it through
`/me/drive/items/{id}/children`. A channel is renamed, another made and
deleted. A UUID group is 501, bob (a member) can't delete the team (403), and
alice deleting it removes the circle and both rooms.

## Decisions beyond ADR 0009
- **Membership ids** are `m.` plus the circle member id, as D2 says; a channel's
  members are its team's (same ids).
- **Team ↔ Graph.** `tenantId`, `summary` and the settings objects are left
  out (no tenant, no counterpart), as phase 23 left `tenantId` out of chats.
  `createdDateTime` is the circle's `creation`. `webUrl` is the circle's page
  in Nextcloud's Teams app. `isArchived` is always false.
- **Creation** resolves every member before anything is made; the caller,
  named among the members as Graph's examples do, is skipped (they own the
  circle). A team that can't be finished (a member or the General room
  refused) is destroyed again. `firstChannelName` is ignored: the first
  channel is General. Graph's answer is `202` with `Location` (the operation)
  and `Content-Location` (the team) and no body.
- **Deletion** needs the circle's owner (level 9) who moderates every channel
  room; otherwise it is 403 before anything is deleted (a member's channel
  can't be deleted by the owner, Step 0). ADR 0009 D5 is amended for this.
- **Channels.** A name taken by another channel of the team (ignoring case) is
  `409 NameAlreadyExists`. Names follow Graph's rules (50 characters, none of
  `~#%&*{}+/\:<>?|'"`, no leading `_`/`.` nor trailing `.`). `email` is empty,
  `createdDateTime` null (Talk keeps none), `layoutType` `post`, and the list
  has the primary channel first, then the rest by name.
- **filesFolder** is `{team folder mount point}/{channel name}` in the caller's
  drive (a `/` or `\` in a name made in Talk becomes `_`), created with
  conflict `fail` and re-read if someone made it in between. Its
  `parentReference` names the caller's personal drive, which is where it is.
- **Subjects.** A post without a subject is titled with its first line of text
  (mentions left out, at most 100 characters, `Post` when there is none), and a
  post whose title is that default reports `subject: null`; so does a deleted
  post. Editing such a post's body re-titles it with the new first line. A
  reply has no subject (400 on PATCH with one). A subject has at most 200
  characters (Talk cuts a longer thread title to 200 and an ellipsis).
- **Posts outside threads** (Talk's own clients) are posts without replies;
  replying to one is 400, because Talk refuses (`reply-to`). A `replyTo`
  reply made in Talk outside threads is listed as a post.
- **Delta** follows D5: posts and replies are upserts; the message an edit,
  delete, thread rename or reaction is about is re-read and reported. The
  first sync lists the channel's posts (newest first, paged), and its delta
  link starts at the newest message when the sync began. A token for another
  channel is 410. `$filter` (Graph's `lastModifiedDateTime gt`) is 400.
- **Shared Talk pieces** grew additively: `TalkChatClient.CreateTeamRoomAsync`,
  `DeleteRoomAsync`, `SetDescriptionAsync`, `RenameThreadAsync`,
  `FutureAsync`; `TalkRoom.Description`; a filtered `TalkMessageFeed.PageAsync`
  overload; `TalkMessageCodec.IsPost`, `IsThreadReply`, `DefaultThreadTitle`;
  `TalkTestServer.Dav` in the tests. Fixed in the codec: only a message in a
  thread is a reply (a `replyTo` outside threads gave a false `replyToId`), and
  a reply has no subject (replies carried their thread's title).

## Known gaps
- Renaming a team keeps its folder's name, and renaming a channel makes its
  next `filesFolder` a new folder (the old one stays).
- A channel a member made in Talk keeps the team from being deleted by its
  owner (403) until that member deletes it or makes the owner a moderator.
- The subject rule is a heuristic: a post whose subject was given equal to its
  first line reads back with no subject.
- Every channel operation reads the caller's circles, the team's resources and
  the room (three OCS calls); listing channels reads the caller's whole room
  list.
- `$filter`, `$orderby` other than newest first and `$count` on channel
  messages are 400 (a page is 20 unless `$top` says otherwise, as Graph's);
  joined teams, members and channels are served whole and take `$select` only
  (anything else is 400 rather than ignored).
- Reading who reacted costs one Talk call per reacted message (as chats).
- `$expand=replies` gives each post its newest 50 replies and no
  `replies@odata.nextLink` (Graph gives up to 200, then a link).

## Acceptance
- alice creates a team with bob over Graph; both see it in `joinedTeams` and in
  Nextcloud's Teams app; General exists and is the primary channel. **Done**
  (live test).
- alice adds a channel, posts with a subject, bob replies in Talk's client, and
  the reply shows in `replies` over Graph; delta picks up a later edit.
  **Done** (live test; bob's reply goes through Talk's OCS API as its client
  does).
- `filesFolder` returns a folder both can list through `/me/drive/items/{id}/children`.
  **Done** (live test).
- Deleting the team removes the circle and its rooms. **Done** (live test).
