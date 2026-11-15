# Known issues and follow-ups

Collected from the phase reports (2026-11-15). Each item says where it bites and
what a fix needs. Ordered by impact within each section.

## Behaviour gaps the client can notice

### Mail
- **Delta reports moves too broadly.** JMAP `Email/changes` doesn't say where an
  updated message used to be, so any update to a message outside the delta
  folder is sent as `@removed: changed`. The client drops that row from its
  cache wherever it is filed; it reappears on that folder's next listing.
  Fix: a per-folder `Email/queryChanges` where Stalwart supports it, falling
  back to today's behaviour.
- **Initial delta can miss a message.** The initial sync pages by position
  (ADR §7), so a message deleted above the cursor mid-sync can push another
  past it unseen until it changes again. Fix: anchor-based paging.
- **Item-attachment names.** An attached email is named "Attached message";
  Graph uses the inner message's subject.
- **A `d.` draft moved out of Drafts can't be resolved.** The locator only lists
  Drafts. The client never moves drafts.
- **A POST with no `Content-Type`** to a JSON route gets the 400 unknown-route
  answer. The client always sends the header.
- Stalwart reports `hasAttachment: false` on some invitation mail that carries
  an `.ics` and an image.

### Calendar
- **External invitations Nextcloud never delivered to the calendar** resolve to
  404 on `messages/{id}/event` (the client falls back to its other lookups).
  Fix: an `ICalendarWriter.ImportAsync(MailboxRef, string ics)` port, or
  `NewEvent.ICalUId`.
- **Not carried by Nextcloud's iMIP:** RSVP/cancel/forward comments,
  `sendResponse: false` (Sabre always replies), and a decline's
  `proposedNewTime` (Sabre has no iTIP COUNTER). Inbound COUNTERs from external
  organizers do work.
- Clearing a recurrence via PATCH isn't expressible through `EventPatch`.
- Online-meeting fields are accepted and ignored.

### Tasks and settings
- **Automatic replies are lossy.** `VacationResponse` has one message and
  replies to everyone: the external message is stored, the audience reads back
  as `all`, and `scheduled` without dates reads back as `alwaysEnabled`.
- **Upload URLs can be replayed until they expire** (≤ 15 min); each replay adds
  another attachment of exactly the declared size to the same task.
- `ATTACH` values carry the configured `Dav:BaseUrl` host (`http://nextcloud/`
  in compose), so other CalDAV clients see an internal URL. Reads use only the
  path.
- Attachments for a shared mailbox's task go to the owner's Files; the upload
  only works if Nextcloud lets the delegate write there.

### Directory
- `/users/{shared}` for a shared mailbox that isn't a Keycloak user is 404. The
  client only calls it for the signed-in account.
- Graph-only profile fields (`jobTitle`, `department`, aliases, …) come from
  `nightware_*` Keycloak attributes that the dev realm doesn't declare yet, so
  they are null live and `proxyAddresses` is only `SMTP:<mail>`.

## Operations and security
- **Stalwart doesn't check From against the sender's Identity.** The façade
  enforces it (403 `ErrorSendAsDenied`), but other Stalwart clients (IMAP/SMTP)
  can still spoof. Harden Stalwart's submission policy.
- **`DeltaLinks:SigningKey` must be set in production.** Without it a
  per-process key is used, so delta links and upload URLs don't survive a
  restart or reach a second instance.
- **Token-endpoint availability.** NightMail treats any HTTP response to a token
  refresh (including a proxy 502/503) as revoked credentials and forces
  re-sign-in. Keep Keycloak highly available; never put a maintenance page in
  front of its token endpoint.
- Fetching an attachment with its content buffers it whole, with no size cap.
- Docker's VM disk filled up during development (Postgres couldn't start);
  build cache is the usual culprit (`docker builder prune`).

## Deferred by design (ADR §43)
`/shares/{id}/driveItem[/content]` and
`beta/security/threatSubmission/emailThreats` return 501: the client gates both
off for NightMail Server accounts. `DeferredOperationsTests` asserts the 501s.
