# Phase 17 — Remaining Graph gaps

> **Status (2026-11-21): done, local only.** Asked for: clearing a
> recurrence with PATCH, calendars for shared mailboxes, and the client-visible
> behaviour gaps listed in `docs/known-issues.md` where possible.

## Done
| Gap | Version | Where it is recorded |
|---|---|---|
| `PATCH` with `"recurrence": null` makes a series a single event | 1.32.0 | this doc |
| `$search` pages with an `@odata.nextLink` | 1.33.0 | `docs/contract/odata-subset.md` §6 |
| An attached message is named after its subject (`Email/parse`) | 1.34.0 | `Adapters.Jmap/README.md` |
| Folder delta reports only messages that left the folder (`Email/queryChanges`) | 1.35.0 | ADR 0001 §6 (amended) |
| Automatic replies keep separate internal and external messages | 1.36.0, 1.38.1 | ADR 0006 D5 (amended) |
| Graph profile fields set in the console, declared in Keycloak | 1.37.0 | `docs/known-issues.md` |
| Notifications: exact created/updated; `subscriptionRemoved` | 1.38.0 | phase 16 doc, ADR 0006 D10 |
| Shared mailboxes have a calendar | 1.39.0 | ADR 0004 D6 |
| RSVP `sendResponse: false`; comments and proposed times to outside organizers by iMIP | 1.40.0 | `Adapters.Dav/README.md` |

## Step 0 findings (local stack, 2026-11-21)
- **Stalwart** answers `Email/queryChanges` with a folder filter and
  `receivedAt` sort (`canCalculateChanges: true`), and `Email/parse` on a part's
  blob id (not through a back-reference with a wildcard path). It reports
  `total` for text searches.
- **Sieve:** `envelope` is available (the mailing lists already use it), and
  `vacation` in both branches of an `if` validates and replies.
- **Nextcloud, shared-mailbox calendars:**
  - A local user created over OCS can be used with Basic auth over WebDAV; its
    first PROPFIND creates `personal` (with a sample event).
  - Sharing that calendar with a group (`oc:share`, read-write) gives every
    member it as "Personal ({name})", with `owner-principal` naming the user,
    which the façade's scope code already handles.
  - The group principal must be URL-encoded when the id holds `@`.
  - An event a member creates there with an attendee has the mailbox's address
    as ORGANIZER; the attendee gets it in their calendar and an invitation mail
    "{name} via Nextcloud" from the calendar sender.
- **Nextcloud, Apache:** prefork kept 5–10 idle workers (see the load README).
- **Sabre scheduling:** an attendee's copy written with
  `ORGANIZER;SCHEDULE-AGENT=CLIENT` and a new PARTSTAT sends no REPLY (the
  organizer's copy keeps "needs action"); without it the REPLY lands at once.
  A stored calendar object must not carry `METHOD` (415).
- **Stalwart submission:** recipients on reserved test domains (`.example`,
  `.test`) are dropped from the envelope ("No recipients found").
- **Not possible:** a comment or proposed time for an organizer inside
  Nextcloud. Sabre delivers the REPLY into their calendar and can't carry
  either; a mailed iTIP message would arrive as mail, not update the calendar.
