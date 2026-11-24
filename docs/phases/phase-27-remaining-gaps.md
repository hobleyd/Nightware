# Phase 27 — OneDrive, calendar, directory and settings gaps

> **Status (2026-11-24): done, local only.** Asked for: "fix the gaps in
> OneDrive, Calendar, Directory & Settings" listed in `docs/known-issues.md`.
> Each gap is triaged below; the table at the end records what closed and in
> which version. What the backend can't do stays in `docs/known-issues.md`:
> another person's drive, delta without the activity app, SharePoint,
> cancel/forward comments and RSVP comments to organizers inside Nextcloud.
> No tenant captures were possible: the new fixtures are constructed and
> `tools/capture --phase 27` replaces them. The full live suite (108 tests)
> ran once: 104 passed; the four failures passed when rerun alone and come
> from tests sharing alice's data in parallel (one of them, `recent` order in
> the drive sharing test, was this phase's and is fixed).

## Triage
| Gap (known-issues) | Bucket | Plan |
|---|---|---|
| OneDrive: simple upload capped by `Limits:MaxRequestBodyBytes` (~67 MB) | existing route | Raise the limit to Graph's 250 MB on the drive content `PUT` routes only, streaming. |
| OneDrive: no `@microsoft.graph.downloadUrl` | existing route | Emit the self-authorising download URL (as `…/content` redirects to) on file items. |
| OneDrive: no `file.hashes` | existing route | Emit only what Nextcloud stores (`oc:checksums`); never download to hash. |
| OneDrive: `createLink` `embed`, `blocksDownload` | existing route | `embed` is an anonymous view link with `webHtml`; `blocksDownload` is Nextcloud's hide-download (links) or the `download` share attribute (invitations). |
| OneDrive: versions, `checkin`/`checkout`, `sharedWithMe`, `recent`, `preview` | new contract operations | `files_versions` DAV, `files_lock` (`LOCK` with `X-User-Lock`), OCS `shared_with_me`, `SEARCH` by modification time, a self-authorising preview URL. |
| OneDrive: another user's drive | backend can't | Nextcloud can't open another user's home as the caller; `sharedWithMe` is the way to their items. Stays. |
| OneDrive: SharePoint `/sites`, `follow`, analytics, item activities (beta) | out of scope | Unchanged (ADR 0008 D7, amended). |
| OneDrive: delta without the activity app | backend can't | Nextcloud Files has no other change feed. Stays. |
| Calendar: online-meeting fields ignored | already fixed | Phase 22 (1.59.0); the known-issues line was stale. |
| Calendar: undelivered external invitation is 404 on `messages/{id}/event` | existing route | Import the invitation into the default calendar, idempotent by UID, with `SCHEDULE-AGENT=CLIENT` so Sabre sends nothing. |
| Calendar: RSVP comment/proposal to organizers inside Nextcloud; cancel and forward comments | backend can't | Tried and reverted: Nextcloud mails every recipient inside it its own iMIP notice (no comment), so a façade mail is a duplicate; silencing Sabre stops the calendar update, and taking an outside attendee over makes Sabre cancel them (Broker::parseEventForOrganizer) or cuts them off from later updates. Recorded in known-issues. |
| Calendar: every `calendarView` page re-expands the window | existing route | A disposable, bounded in-memory cache of the sorted expansion per credential, calendar, window and CTag, one minute. |
| Directory: photo must be a square JPEG/PNG | existing route | Centre-crop and re-encode in the façade (new image package). |
| Directory: shared mailbox has no contacts or files | new provisioning | As its calendar (ADR 0004 D6): the shared mailbox's Nextcloud user shares its address book and a folder with the group; console Repair for existing ones. |
| Directory: shared mailbox rename not reflected; new member waits for the JMAP session cache | existing route | Rename updates the identity name; an unknown account refreshes the cached session once. |
| Settings: automatic replies `contactsOnly` means internal only | existing route | The mailbox's contacts' addresses join the internal domains in the Sieve script, frozen at save time and refreshed when the façade writes a contact. |

## Done
| Gap | Version |
|---|---|
| OneDrive simple upload up to Graph's 250 MB (`Limits:MaxLargeUploadBytes` and a 15-minute budget on the drive `PUT …/content` routes only); a body streamed into Nextcloud gets `Dav:TransferTimeout` instead of the 30 s attempt timeout | 1.70.0 |
| A file's GET carries `@microsoft.graph.downloadUrl` (listings don't); `file.hashes` from Nextcloud's stored checksums; `createLink` type `blocksDownload` (hide-download); `embed` stays 400 (share pages can't be framed) — verified live | 1.71.0 |
| `sharedWithMe` (NightMail's cloud picker calls it) and `recent`, with `remoteItem` entries the picker opens through `/drives/{id}/items/{id}`; the contract covers the picker's `root/children`, `/drives/{id}/items/{id}` and `root/search` spellings (1.71.1) — verified live | 1.72.0 |
| Microsoft Search `POST /search/query` for `driveItem` (the cloud picker's first try); the contract now covers every NightMail call path again — verified live | 1.73.0 |
| A file's versions: list, get, content and `restoreVersion` over `files_versions` — verified live | 1.74.0 |
| `checkout`/`checkin` over Nextcloud user locks, with the `publication` facet; a check-in comment labels the current version — verified live | 1.75.0 |
| `preview`: the large rendered preview behind a self-authorising URL (never the file inline) — verified live | 1.76.0 |
| An outside organizer's invitation is imported into the default calendar when `messages/{id}/event` finds no copy (idempotent by UID; Sabre silent until the RSVP) — verified live | 1.77.0 |
| A `calendarView` walk expands once: follow-up pages read a one-minute, size-bounded snapshot of the first page's expansion | 1.77.2 |
| Photos: any image is turned upright (EXIF), centre-cropped and re-encoded to a square JPEG/PNG for Nextcloud (SkiaSharp 4.156.2, MIT, NoDependencies native build; verified in the Linux SDK and aspnet images) — verified live | 1.78.0 |
| Shared mailboxes: a console rename renames the identities (`/users/{shared}` shows it, verified live); a member added a moment ago reaches the mailbox at once (the session is fetched again for an unknown address, at most every 30 s per caller) | 1.78.1 |
| Shared mailboxes have an address book: the console shares its Nextcloud user's `contacts` book with the members' group (Repair adds it to existing ones), and `/users/{x}/contacts` reads the books `x` shared with the caller (ADR 0004 D7) — verified live | 1.79.0 |
| Shared mailboxes have files: the console shares a folder with the members' group, and `/users/{x}/drive` / `/drives/{id}` reach it as the mailbox's drive (found by the share, not the name); a `SEARCH` scope is now the decoded path (a uid or folder needing escaping wasn't found) — verified live | 1.80.0 |
| Automatic replies `contactsOnly` answer the mailbox's contacts (validated addresses, at most 1,000, written into the Sieve script when the reply is saved and when the façade writes a contact; the console keeps the last list) — verified live | 1.81.0 |
