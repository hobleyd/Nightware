# ADR 0006 — Graph parity beyond NightMail

Status: accepted (2026-11-20). Amends `architecture.md` rules 1 and 2 and
supersedes ADR 0001 §21.

## Context

The contract has been "exactly what NightMail calls" (76 operations). The goal
now is for a generic Graph mail/calendar/contacts/To Do client that lets you set
its Graph base URL and OAuth authority to work against Nightware. It should not
have to hit a 501 for the common personal-information operations. This adds
the core PIM gaps and two cross-cutting features, `$batch` and change
notifications.

Microsoft-only products stay out of scope: OneDrive/SharePoint, Teams, OneNote,
Planner, presence and online meetings. The directory (groups, manager,
organization) was out of scope too; phase 21 (2026-11-22) brought in reading it
(see D11).

## Decisions

### D1 — The contract grows; the rule stays
`docs/contract/endpoints.json` is still the whole scope ("implement only what is
listed"). It now holds two kinds of operation:

- NightMail operations, as before.
- **Graph-parity operations**, marked `"source": "graph-parity"`. These have
  `"clientCallSites": []` and a `"docs"` link to the learn.microsoft.com
  reference page.

`tools/inventory/check_inventory.py` accepts empty call sites only on
graph-parity operations. It and `render_endpoints_md.py` learn the new phase
numbers. ADR 0001 §21 (cut `$batch` and `subscriptions`) is superseded.

### D2 — Fixtures are captured from a real tenant
Graph-parity fixtures are recorded from Microsoft 365 by `tools/capture`, using
a dedicated test mailbox. They are scrubbed to example.com data and carry the
provenance `"Captured from Microsoft Graph v1.0 (tenant capture <date>),
scrubbed"`.

Docs samples are never relabelled as captures. When a capture is impossible
(for example a lifecycle notification), the fixture is constructed, and its
provenance says so and names the docs page.

A phase commits each operation's contract entry and its fixture together,
because the checker fails on either one alone.

### D3 — OData for parity operations
A parity operation accepts the query options in its captured request plus the
ones its Graph reference page lists for that resource. They are written per
operation in `odata-subset.md` §Parity. Anything else is still `400
invalidRequest` (ADR 0001 §32 applies).

### D4 — Phasing and frozen files
The work runs one phase at a time, each committed green before the next:

| Phase | Area |
|---|---|
| 11 | `$batch` |
| 12 | Mail |
| 13 | Calendar |
| 14 | Contacts |
| 15 | To Do |
| 16 | Change notifications |
| 21 | The directory, read-only (D11) |

The §24 freeze was written for the parallel fan-out. It is lifted for additive
wiring: a phase may add registrations to `Program.cs`/`Composition/`, new
`ErrorKind` mappings and new ports. It still may not change the behaviour of an
existing NightMail operation. Every NightMail fixture must stay green.

Each phase starts with **Step 0**: probe the local stack and write the findings
into the phase doc before contracting operations (as phase 10 did).

### D5 — Mail
*Amended after Step 0 (2026-11-21). The findings are in the phase 12 doc.*

- **flag, categories and importance on any message** (PATCH). These never
  rewrite the message, so the id is kept (ADR 0001 §1):
  - `flagged` is the `$flagged` keyword.
  - `complete`, the flag dates, categories and importance go in the message's
    shared `metadata` under the `nightware.ms` namespace
    (`urn:ietf:params:jmap:metadata`).

  Stalwart lower-cases keywords and caps them at 128 characters, so names
  cannot live there. A PATCH that also touches draft-only fields keeps today's
  draft path.
- **`copy`** makes a distinct message: `Email/import` of the blob into the
  target mailbox, plus that message's metadata in the same request (import
  drops it). JMAP "add a mailbox id" would give one id two parent folders.
- **`masterCategories`** live in the Inbox mailbox's `metadata/nightware.ms`
  (8 KB, about 80 categories; beyond that, 400).
- **`messageRules`** and **automatic replies** share one Nightware-owned Sieve
  script, `nightware-rules`.
  - Stalwart runs one active script per account, and `VacationResponse`
    enabling its own script would switch the rules off.
  - The script holds the auto-reply block first, in Stalwart's own shape so
    behaviour does not change, then the rules.
  - Its state is base64 JSON in comments. Every write is validated first.
  - Moving off `VacationResponse` is in the phase 12 doc.
  - *Amended 2026-11-21:* a reply with separate internal and external
    messages, or an `externalAudience` other than `all`, is written to the
    script even without rules. It tests the envelope sender's domain
    (`envelope :domain "from"`, where `vacation` sends the reply; not the
    forgeable From header) against the domains of the entity that owns the mailbox's domain,
    stored in the script's state. `contactsOnly` answers internal senders only.
  - A rule using an unsupported condition or action gets `400 notSupported`
    naming the field.
- **Large attachments.** `createUploadSession` returns a self-authorising URL
  (§39, its own key purpose, bound to the draft and session).
  - Each chunk is a Stalwart blob. The session (the file, and its blobs in
    order) is kept in the draft's metadata (`nightware.upload`), so mail
    stays in the mail store.
  - Consecutive chunks are merged with `Blob/upload` (RFC 9404) while the
    result stays within `maxSizeBlobSet` (7.5 MB on Stalwart). This keeps the
    list well inside 8 KB.
  - The last chunk joins the blobs and attaches the file through the draft
    store, keeping the draft's `d.` id.
- **`mailFolders/delta`** is built on `Mailbox/changes`, with the same token
  layering as ADR 0001 §6–8.
- **Cut (501):** Focused Inbox (`inferenceClassification`), open and schema
  extensions, and `singleValueExtendedProperties`/`multiValueExtendedProperties`.

### D6 — Calendar
- **Calendars.** `calendars` are the caller's CalDAV collections that support
  VEVENT, owned or shared. `/calendar` is the default ("personal").
  - Create is `MKCALENDAR`, update (name, color) is `PROPPATCH` and delete is
    `DELETE`.
  - The default calendar can't be deleted (`400`, as Graph).
- **`calendarGroups`.** There is one synthetic group, "My Calendars"
  (`calendargroup-default`), holding every calendar. It is list/get only;
  creating one is `400 notSupported`.
- **Events and instances.**
  - `events` lists are `calendar-query` across calendars, merged, ordered and
    paged with an offset token.
  - `instances` is an `expand` query over the series.
- **`calendarView/delta`** (and per-calendar `events/delta`) uses
  `sync-collection` (RFC 6578) per collection, filtered to the window. The
  token holds every collection's sync token.
  *Amended 2026-11-22:* Graph v1.0 has no `events/delta` (per calendar or
  top-level); it is beta-only, so it was not added. Event delta is
  `calendarView/delta` (phase 18 doc).
- **Reminders.**
  - `reminderView` is computed from VALARMs.
  - `dismissReminder` sets `ACKNOWLEDGED`.
  - `snoozeReminder` adds an RFC 9074 snooze alarm (`RELATED-TO;RELTYPE=SNOOZE`).
- **Event attachments** are stored in Nextcloud Files and referenced by
  `ATTACH` URL, as To Do attachments are.
- **`calendarPermissions`** map to Nextcloud CalDAV sharing.
  - Supported roles are `read` and `write`. Others are `501 notSupported`.
  - The share target must be a directory user.
- **`findMeetingTimes`** is Application logic over the existing free/busy port
  and working hours (§29).

### D7 — Contacts
- **Folders.** `contactFolders` are CardDAV address books; the default is
  `contacts`. CardDAV is flat, so `childFolders` is always empty and creating a
  child is `400 notSupported`.
- **Contacts.** Full contact CRUD uses the vCard 4 ↔ Graph contact mapping
  table, which lives in the adapter README. The photo is vCard `PHOTO`.
- **Delta** (folders and contacts) uses `sync-collection`.

### D8 — To Do
- **Lists.** List CRUD is `MKCALENDAR`/`PROPPATCH`/`DELETE` on VTODO
  collections.
- **`checklistItems`** are child VTODOs (`RELATED-TO;RELTYPE=PARENT`), which is
  how Nextcloud Tasks shows subtasks. They are hidden from the task listing.
- **`linkedResources`** are stored as `X-NIGHTWARE-LINKED-RESOURCE` properties,
  unless Step 0 finds a standard property that Nextcloud keeps.
- **Delta** (lists and tasks) uses `sync-collection`. Task delete and
  attachment get/delete are added.

### D9 — `$batch`
- **Dispatch.** `POST /v1.0/$batch` and `/beta/$batch` dispatch each
  sub-request in-process through the app's own request pipeline: a fresh
  `HttpContext` carrying the caller's `Authorization`. Authentication, `Prefer`,
  OData parsing, routing and the Graph error envelope therefore apply per
  sub-request.
- **Limits.**
  - At most 20 sub-requests; more is `400`.
  - `url` must be relative and must stay on the same version root.
  - A nested `$batch` and self-authorising URLs are refused per sub-request.
- **Order.** Sub-requests without `dependsOn` run concurrently (bounded).
  `dependsOn` runs in order; a failed dependency gives `424` for that
  sub-request only.
- **Responses.** A sub-request's own failure is its own status, never the
  batch's. Continuation links inside responses are absolute on `graphBaseUrl`
  (endpoints.md requirement 1).

### D10 — Change notifications: a façade subscription store (option A)
Subscriptions are data of record, and `architecture.md` rule 2 forbids it in
the façade.

Stalwart 1.1 advertises JMAP push (`PushSubscription`, EventSource, WebSocket).
Nextcloud has no push to arbitrary URLs. Turning a backend change into a Graph
notification with a resource id also needs a credential for the user at a time
when no request is in flight.

Options:
- **A. Façade subscription store.** A small Postgres database holds
  subscriptions, sync state and a sealed per-subscription refresh credential.
  One notifier watches JMAP by push and DAV by polling. This amends rule 2.
- **B. Mail only, backend-held.** Stalwart `PushSubscription` keeps the record;
  events, contacts and To Do subscriptions stay 501.
- **C.** B for mail, plus A for the DAV resources only.

**Chosen: A** (2026-11-20). It covers every resource type with one design.
Rule 2 gains one exception: the subscription store is data of record. It is
the only one, and it holds nothing a backend already owns. Phase 16 Step 0
settles the background credential and the database (its own Postgres in
compose and ansible).

Required:
- An SSRF guard on `notificationUrl`: https only, resolved address not
  loopback/private/link-local/compose network, re-checked at each send.
- The `validationToken` handshake within 10 s, and `clientState` echoed.
- Never a bearer token in a notification.
- Graph's maximum expirations; lifecycle notifications (`reauthorizationRequired`,
  `missed`); retries with backoff. *Amended 2026-11-21:* a subscription not
  reauthorized within an hour of `reauthorizationRequired` is removed with
  `subscriptionRemoved`, as Graph removes one whose access is gone. Expiry still
  sends nothing. Each subscription keeps a set of hashed item ids it has seen, so
  `created` and `updated` are exact rather than guessed.
- Rich notifications (`includeResourceData`) are `400 notSupported`.

### D11 — The directory, read-only (phase 21, 2026-11-22)
A user's `manager`, `directReports` and `memberOf`/`transitiveMemberOf`,
`/groups` (one group, its `members`/`transitiveMembers`) and `/organization`.
They read what the console records, so nothing new is stored: a group is a
console group (ADR 0003), an organization is an entity, and a manager is the
user attribute `nightware_manager`, set on the console's user page (another
member of the same entity, no loops). Each user sees only their own
organizations' groups. Writes (`manager/$ref`, group create and membership) stay
out: in Graph they need administrator rights, and here the console holds them.
Groups don't nest and hold only people, so the transitive forms are the same
lists. Details: [`docs/phases/phase-21-directory.md`](../phases/phase-21-directory.md).

## Consequences
- The contract roughly doubles. NightMail fixtures and behaviour are unchanged.
- The capture tool needs a Microsoft 365 test mailbox and, for notifications, a
  public HTTPS tunnel.
- Phase 16 (option A) needs deploy and ansible changes. Phase 12 needs none.
