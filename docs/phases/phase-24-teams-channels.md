# Phase 24 — Teams and channels over Nextcloud Teams and Talk

> **Status (2026-11-23): planned, after phase 23** (it reuses the chat message
> codec). Decisions in ADR 0009 D5; Step 0 in phase 23.

## Operations
`docs/contract/endpoints.json`, phase 24 (34 operations): joined teams, team
get/create/update/delete (`DELETE /groups/{id}`), the create operation's status,
members list/get/add/update/remove, channels list/get/create/update/delete,
the primary channel, a channel's `filesFolder` and members, channel messages
list/get/send/update/softDelete/reactions/delta, replies list/get/send/update/
softDelete/reactions.

## Build notes
- Feature folder `Teams/` in every project. The team/circle client in
  `Adapters.Dav/Teams/`. Channel messages use phase 23's codec
  (`Adapters.Dav/Chats/Talk/`).
- `filesFolder` goes through the drive port (`Application/Files`), not WebDAV
  directly, so the item comes back shaped as phase 20's.
- `DELETE /groups/{id}` is mapped by this feature and only serves team ids
  (31 alphanumerics); a UUID falls through to `ErrorKind.Unsupported` (501), as
  group deletion is not in scope.
- Step 0 left for this phase: a reply with `threadId` on a top-level message that
  is not a thread (does Talk make it one?); renaming a thread; the circle's
  config flags for `visibility`; circle deletion's effect on its rooms (they
  must be deleted by the façade first if Talk keeps them); the team folder's
  name and id from `teams/{id}/resources`.

## Acceptance
- alice creates a team with bob over Graph; both see it in `joinedTeams` and in
  Nextcloud's Teams app; General exists and is the primary channel.
- alice adds a channel, posts with a subject, bob replies in Talk's client, and
  the reply shows in `replies` over Graph; delta picks up a later edit.
- `filesFolder` returns a folder both can list through `/me/drive/items/{id}/children`.
- Deleting the team removes the circle and its rooms.
