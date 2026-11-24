# ADR 0008 — OneDrive over Nextcloud Files

Status: accepted (2026-11-22). Amends ADR 0006 (Context: OneDrive is no longer
out of scope).

## Context

ADR 0006 left OneDrive/SharePoint out as Microsoft-only. A Graph client that
can be pointed at Nightware still hits a 501 for "Files": `/me/drive`,
browsing, downloading, uploading. Nextcloud Files already holds each user's
files (event and To Do attachments live there), so the façade can serve the
personal OneDrive surface from it. SharePoint sites, document libraries and
Teams files stay out of scope.

## Decisions

### D1 — One personal drive per user
- `/me/drive` (and `/users/{x}/drive` when `x` is the caller) is the caller's
  Nextcloud home, `files/{uid}/`. `driveType` is `personal`.
- The drive id is `nc!` + unpadded base64url of the Nextcloud uid. `/drives/{id}`
  is served only for the caller's own drive; another drive is `404
  itemNotFound` (the façade can't read another user's home as the caller).
- `/me/drives` lists that one drive.
- Quota comes from `quota-used-bytes`/`quota-available-bytes`. Nextcloud's
  negative "unlimited" values leave `total` and `remaining` out.

### D2 — Item ids are Nextcloud file ids
- A `driveItem` id is the item's `oc:fileid` (decimal digits). It survives
  renames and moves, as Graph's ids do.
- An id is resolved with a WebDAV `SEARCH` on `oc:fileid` scoped to the
  caller's home. Items shared with the caller are mounted in that home, so
  they resolve too.
- `root` names the home. Path addressing (`root:/a/b:`,
  `items/{id}:/a/b:`) is rewritten before routing into an internal
  `…/items/{anchor}/nightware.path/{base64url path}` segment, so every route
  takes the same handlers. A path segment that is empty, `.` or `..`, or
  holds `\` or a control character, is `400 invalidRequest`.

### D3 — Content
- `GET …/content` answers **302** to a self-authorising download URL (ADR
  0001 §39), as Graph redirects to a pre-authenticated URL.
- *(Amended, phase 27.)* A single file's `GET` carries that URL as
  `@microsoft.graph.downloadUrl` (five minutes, bound to the item). Listings
  don't: every URL seals a copy of the caller's token, so a 200-item page
  would carry 200 of them (about 2 KB each). Clients that read it from a
  listing fall back to `/content`.
- *(Amended, phase 27.)* `file.hashes` holds `sha1Hash`/`sha256Hash` (upper-case
  hex) when Nextcloud stored the checksum (`oc:checksums`, set when an uploader
  sent `OC-Checksum`). The façade never reads content to hash it, and has no
  `quickXorHash`.
- Simple upload (`PUT …:/{name}:/content`, `PUT items/{id}/content`) streams
  up to 250 MB into WebDAV. `@microsoft.graph.conflictBehavior` defaults to
  `replace` for uploads and `fail` for new folders, as Graph.
- `createUploadSession` stages chunks in Nextcloud's chunked upload v1, as
  event attachments do (phase 19), and keeps no state in the façade. The
  upload URL is self-authorising. GET on it reports progress, DELETE cancels.

### D4 — Writes
- `POST children` with `folder` is `MKCOL`. `PATCH` renames and moves (one
  `MOVE`). `DELETE` is WebDAV `DELETE`, which Nextcloud sends to its trash
  bin, as OneDrive sends deletes to its recycle bin.
- `copy` is a WebDAV `COPY`, done before answering. The answer is still
  Graph's `202 Accepted` with a `Location` monitor URL, so clients written for
  the asynchronous copy work. The monitor URL is self-authorising and reports
  `completed` with the new item's id.
- `rename` conflict behaviour is resolved before the write: Nextcloud's
  `COPY`/`MOVE`/`MKCOL` have no "rename on conflict", so the façade picks the
  next free `name (n).ext` itself.

### D5 — Delta from the activity stream
Nextcloud Files has no RFC 6578 `sync-collection` (Step 0). The `activity`
app's stream (`ocs/v2.php/apps/activity/api/v2/activity/files`) records every
create, change, rename, move and delete with the item's file id, including
changes others make to items shared with the caller.

- An initial `root/delta` lists every item (`SEARCH` on the home, paged), and
  its delta link holds the newest activity id at the moment the listing
  began.
- A follow-up reads the activities after that id, looks up each named item
  by file id, and returns it, or `{"id", "deleted": {}}` when it no longer
  resolves.
- `?token=latest` returns only a delta link.
- Without the activity app, `delta` is `501 notSupported`; every other
  operation works.

### D6 — Sharing and thumbnails
- `createLink` (`view`/`edit`, `anonymous` scope) and `invite` (directory
  users) create OCS shares. `permissions` lists the item's shares, and
  `DELETE permissions/{id}` removes one. A permission id is the OCS share id.
  Organization-scoped links, `embed` and `review` are `400 notSupported`.
  *(Amended, phase 27.)* `blocksDownload` is a read link with Nextcloud's
  hide-download set (by updating the new share, as Nextcloud ignores it on
  create; a link it can't make view-only is removed again); its `link` reads
  back with `preventsDownload: true`. `embed` stays out: Nextcloud's share
  pages send `frame-ancestors 'self'`, so another site's frame would be empty.
- `thumbnails` gives one set (`small` 96, `medium` 176, `large` 800 px) whose
  URLs are self-authorising and stream Nextcloud's preview. An item without a
  preview has no thumbnail set.

### D7 — Out of scope
SharePoint (`/sites`, document libraries), versions, `follow`, `checkin`/`checkout`,
`preview` (embed URLs), item activities and analytics. These stay
`501 notImplemented`.

### D8 — Shared with me and recent *(phase 27)*
- `sharedWithMe` reads Nextcloud's shares with the caller (OCS
  `shared_with_me`, user and group shares; an item reached by several is
  listed once, as its earliest share). Nextcloud mounts a shared item in the
  caller's home with the owner's file id, so each entry's `remoteItem` names
  the caller's own drive, and every item route (the cloud picker's
  `/drives/{driveId}/items/{id}/…`) reaches it. `remoteItem.shared.owner` is the
  owner. It isn't paged.
- `recent` is a `SEARCH` for files (not collections) ordered by
  `getlastmodified` descending, the 100 newest, as Nextcloud's own Recent
  view; entries carry `remoteItem` as Graph's do.
- D7 no longer excludes either.
- *(Phase 27.)* Microsoft Search (`POST /search/query`) answers `driveItem`
  requests only, as `root/search` searches (by name, the caller's drive), so
  NightMail's cloud picker, which tries it first for Microsoft 365 accounts,
  gets results instead of an error. Other entity types are `400 notSupported`.

## Consequences
- The contract gains the phase 20 operations, all `"source": "graph-parity"`.
- Delta needs Nextcloud's activity app, which a default install enables.
- No tenant capture was possible. Every fixture is constructed from
  Microsoft's reference page, and capture scenarios are added to replace them.
