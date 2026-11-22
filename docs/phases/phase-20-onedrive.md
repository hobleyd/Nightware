# Phase 20 — OneDrive over Nextcloud Files

> **Status (2026-11-22): done, local only.** Asked for: close the OneDrive gap.
> A Graph client's "Files" works against Nightware from the caller's Nextcloud
> home: 30 operations, decisions in ADR 0008. No tenant captures were possible:
> every fixture is constructed from Microsoft's reference page and says so; the
> capture scenarios (`tools/capture`, phase 20) replace them.

## Step 0 findings (local stack, Nextcloud 35, 2026-11-22)
- **No `sync-collection` on Files.** `REPORT sync-collection` on
  `files/{uid}/` is `ReportNotSupported`. Delta needs another source.
- **The activity stream is a change feed.**
  `GET ocs/v2.php/apps/activity/api/v2/activity/files?since={id}&sort=asc`
  works with the caller's bearer. Every create, change, rename, move and
  delete over WebDAV appears at once, with the file id in `object_id`. One
  activity may name several items; `objects` maps each id to its path.
  Activity ids are shared across apps, so the files filter skips numbers.
  `X-Activity-First-Known` (with `sort=desc&limit=1`) is the newest id, and
  `X-Activity-Last-Given` plus `Link: rel="next"` page the stream. Types seen:
  `file_created`, `file_changed` (edits, renames: "You renamed a to b", moves:
  "You moved a to sub"), `file_deleted`.
- **`SEARCH`** (RFC 5323 basicsearch, scoped `/files/{uid}`, depth infinity):
  - `oc:fileid` supports `eq` (and `or` of several `eq`) but not `gt`.
  - `displayname` supports `like` (`%` wildcards), so `like '%'` lists every
    item below the home (the home itself is not returned).
  - Without `<d:limit>`, at most 100 results come back. `<d:nresults>` with
    `<ns:firstresult>` (SearchDAV namespace) pages.
  - `oc:fileid` isn't sortable; `displayname` and `getlastmodified` are.
- **Properties** (`PROPFIND`): `oc:fileid`, `getetag`, `oc:size` (folders
  too), `getlastmodified`, `nc:creation_time` (0 when unknown),
  `oc:permissions`, `oc:owner-id`, `nc:has-preview`,
  `nc:contained-folder-count`/`nc:contained-file-count` on folders, and
  `quota-used-bytes`/`quota-available-bytes` (`-3` = unlimited).
- **Writes** with the caller's bearer: `MKCOL` 201, `PUT` 201 (204 on
  overwrite) with `OC-FileId`, `MOVE` (rename and move) 201 keeping the file
  id, `COPY` 201 with the copy's `OC-FileId`, `DELETE` 204 into the trash bin
  (`trashbin/{uid}/trash/` lists it with original location and time).
- **Previews:** `index.php/core/preview?fileId={id}&x=&y=&a=1` answers
  `image/png` with the caller's bearer.
- **OCS sharing** with the bearer: `POST apps/files_sharing/api/v1/shares`
  `shareType=3` (link; `permissions=1` comes back as 17 with share
  permission) returns `id`, `token`, `url`; `shareType=0` with `shareWith`
  shares with a user. `GET shares?path=` lists an item's own shares (with
  `reshares=true`, the caller's and others'), `GET shares/inherited?path=`
  the shares of its ancestors.
- **Chunked upload v1** (phase 19 Step 0) applies unchanged to drive uploads.

## Operations
All `"source": "graph-parity"`, forms `me`/`users` for `{mailbox}/drive…`;
each `{drive}` route is also mapped under `/drives/{driveId}`, and each
`items/{itemId}` route also under `root` and path addressing.

| Area | Operations |
|---|---|
| Drive | `GET drive`, `GET drives`, `GET /drives/{id}`, `GET drive/special/{name}` |
| Items | `GET items/{id}` (`root`, `root:/path:`), `GET children`, `GET content` (302), `PUT content` (simple upload), `POST children` (folder), `PATCH`, `DELETE`, `POST copy` (202 + monitor) |
| Upload sessions | `POST createUploadSession`, upload URL `PUT`/`GET`/`DELETE` |
| Sync and search | `GET root/delta`, `GET root/search(q='…')` |
| Sharing | `POST createLink`, `POST invite`, `GET permissions`, `DELETE permissions/{id}` |
| Thumbnails | `GET thumbnails`, thumbnail `content` |

## Done
| Gap | Version |
|---|---|
| The drive, items (read, path addressing, children, content, simple upload, folders, rename/move, delete, copy), upload sessions, delta, search, special folders, thumbnails, sharing links, invitations and permissions | 1.58.0 |

Verified live on the dev stack (`tests/Nightware.Integration.Tests/Files/DriveLiveTests.cs`):
browse, upload by path, conflict behaviours, download through the redirect,
rename and move keeping the id, copy and its monitor, search, delete; a 5 MB
upload session in two chunks read back byte for byte; delta from `token=latest`
through a create and a delete; a link and an invitation to bob, listed and
removed; a skeleton PNG's thumbnail, and `special/documents`.

## Decisions
See ADR 0008. Beyond it:
- One handler per operation serves `/me/drive`, `/users/{caller}/drive` and
  `/drives/{id}`, and `root`, `items/{id}` and path addressing. The route values
  are read by a bound `DriveAddress`, since `[FromRoute]` fails on routes
  without the segment.
- A self-authorising URL kind may list several methods (`PUT,GET,DELETE` for an
  upload URL, as Graph's takes all three); the list is sealed in.
- A path segment still holding `%2F` or `%5C` (the server doesn't decode those)
  is 400: it is a separator a name can't hold.
- Drive errors use OneDrive's codes (`itemNotFound`, `accessDenied`,
  `nameAlreadyExists`) where the mapper would emit Exchange's.
- The upload session's intermediate chunks answer 202 with `"{next}-"`, as
  OneDrive does (mail and event sessions answer 200 with the bare number, as
  Outlook's do).
- `fileSize` is optional on `createUploadSession`; the chunks' `Content-Range`
  total decides when it is absent.
