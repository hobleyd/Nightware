# Phase 27 — OneDrive, calendar, directory and settings gaps

> **Status (2026-11-24): in progress, local only.** Asked for: "fix the gaps in
> OneDrive, Calendar, Directory & Settings" listed in `docs/known-issues.md`.
> Each gap is triaged below; the table at the end records what closed and in
> which version.

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
| Calendar: RSVP comment/proposal to organizers inside Nextcloud; cancel and forward comments | existing route | Sabre still updates calendars inside Nextcloud; the façade mails the iTIP message carrying the text, and suppresses Sabre's iMIP for outside recipients so they get one message. |
| Calendar: every `calendarView` page re-expands the window | existing route | A disposable, bounded in-memory cache of the sorted expansion per credential, calendar, window and CTag, one minute. |
| Directory: photo must be a square JPEG/PNG | existing route | Centre-crop and re-encode in the façade (new image package). |
| Directory: shared mailbox has no contacts or files | new provisioning | As its calendar (ADR 0004 D6): the shared mailbox's Nextcloud user shares its address book and a folder with the group; console Repair for existing ones. |
| Directory: shared mailbox rename not reflected; new member waits for the JMAP session cache | existing route | Rename updates the identity name; an unknown account refreshes the cached session once. |
| Settings: automatic replies `contactsOnly` means internal only | existing route | The mailbox's contacts' addresses join the internal domains in the Sieve script, frozen at save time and refreshed when the façade writes a contact. |

## Done
| Gap | Version |
|---|---|
| OneDrive simple upload up to Graph's 250 MB (`Limits:MaxLargeUploadBytes` and a 15-minute budget on the drive `PUT …/content` routes only); a body streamed into Nextcloud gets `Dav:TransferTimeout` instead of the 30 s attempt timeout | 1.70.0 |
