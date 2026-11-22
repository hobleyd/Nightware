# Phase 19 — Photos, MIME, copies, `$count` segments and more aliases

> **Status (2026-11-22): in progress, local only.** Asked for: the gaps listed
> after phase 18 — the signed-in user's photo (read, metadata, set), a
> top-level `POST mailFolders`, `mailFolders/{id}/copy`, MIME create and send,
> the `/$count` path segment, event attachment `$value` and upload sessions;
> the remaining alias routes; and the smaller items (room lists,
> `translateExchangeIds`, `Prefer: IdType="ImmutableId"`).

## Step 0 findings (local stack, 2026-11-22)
- **`message: unsubscribe` is beta-only** (its reference page is
  graph-rest-beta). Not added, as phase 18 left out `events/delta`.
- **Graph documents the `/$count` segment generally** (`users/$count`, "the
  integer total of the collection"; query-parameters page), but the mail,
  calendar, contacts and To Do list pages don't mention it. It is offered on
  the routes that already take `$count=true`, as the OData convention defines
  it (a `text/plain` integer).
- **Folder copy is 200** with the new folder (mailfolder-copy); a folder move
  is 201.
- **Stalwart** creates a whole subtree in one `Mailbox/set`: a child's
  `parentId` may be `#creationId` of a folder created in the same call.
- **Nextcloud 35 avatars:** `POST index.php/avatar/` (multipart `files[]`)
  accepts a bearer token when the request carries `OCS-APIRequest: true`, and
  `DELETE index.php/avatar/` removes it. Only a **square JPEG or PNG** is stored
  (`{"status":"success"}`); any other image gets `{"data":"notsquare", …}` with
  nothing saved (the web UI crops in the browser), and NC 35 has no
  `avatar/cropped` route. No other Nextcloud route sets an avatar.

## Done
| Gap | Version |
|---|---|
| `POST mailFolders` (top level), `POST mailFolders/{id}/copy` (verified live) | 1.49.0 |
| `/$count` on the eight lists that take `$count=true` | 1.50.0 |
| `/me/photo/$value`; `/me/photo` and `/users/{x}/photo` metadata; PUT, PATCH and DELETE of one's own photo | 1.51.0 |
| MIME `POST messages` and `POST sendMail` | 1.53.0 |

## Decisions
- MIME is its own endpoint per route, chosen by `Content-Type: text/plain`, so
  the JSON forms can't change. The base64 may be wrapped. The Message-ID is
  always minted (see `Adapters.Jmap/README.md`); 4 MB is the limit (413), as
  Graph's reference gives for MIME payloads. Graph also takes MIME on reply,
  forward and their create* forms; those were not asked for and stay JSON.
- Photos: metadata is read from the image itself (JPEG frame header or PNG
  `IHDR`), with a hash of it as `@odata.mediaEtag`. A user without a photo gets
  Graph's documented 1×1 `image/gif` placeholder there; only `…/photo/$value` is
  404 (`ImageNotFound`, now also the code of that 404). A write is the caller's
  own photo only: `/users/{x}` writes are 403 unless `x` is the caller. Uploads
  must be a square JPEG or PNG of at most 4 MB (400 otherwise, 413 over the
  limit; `docs/known-issues.md`); the image's bytes decide its type.
- `/$count` maps the list's own handler with `$count=true` bound, and the
  collection result writes only the total (`text/plain`), so the segment and
  `@odata.count` can't disagree.
- `POST mailFolders` with `isHidden: true` is 400 `notSupported`: the mail
  store has no hidden folders, and Graph can't hide a folder after creation, so
  a silently visible folder would be wrong for good.
- A folder copy is a new folder of the same name under the destination, with
  its subfolders and a distinct copy of every message (`Email/import` of the
  original blob, metadata carried, as `messages/{id}/copy`). A well-known
  folder copies as a plain folder. JMAP has no transaction across requests,
  so a failure part-way leaves what was copied and returns the error.
