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
| `POST mailFolders` (top level), `POST mailFolders/{id}/copy` | 1.49.0 |

## Decisions
- `POST mailFolders` with `isHidden: true` is 400 `notSupported`: the mail
  store has no hidden folders, and Graph can't hide a folder after creation, so
  a silently visible folder would be wrong for good.
- A folder copy is a new folder of the same name under the destination, with
  its subfolders and a distinct copy of every message (`Email/import` of the
  original blob, metadata carried, as `messages/{id}/copy`). A well-known
  folder copies as a plain folder. JMAP has no transaction across requests,
  so a failure part-way leaves what was copied and returns the error.
