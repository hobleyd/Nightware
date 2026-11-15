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
- **Mail search is one page.** `$search` returns at most `$top` results and
  no nextLink (`docs/contract/odata-subset.md` §6). NightMail never pages it.

### Calendar
- **External invitations Nextcloud never delivered to the calendar** resolve to
  404 on `messages/{id}/event` (the client falls back to its other lookups).
  Fix: an `ICalendarWriter.ImportAsync(MailboxRef, string ics)` port, or
  `NewEvent.ICalUId`.
- **Not carried by Nextcloud's iMIP:** RSVP/cancel/forward comments,
  `sendResponse: false` (Sabre always replies), and a decline's
  `proposedNewTime` (Sabre has no iTIP COUNTER). Inbound COUNTERs from external
  organizers do work.
- **Every `calendarView` page re-expands the whole window.** The CalDAV
  expand REPORT has no paging, so each `$skiptoken` page expands, materialises
  and sorts every occurrence in the window again before slicing. The window is
  capped at ten years (`GetCalendarView.MaxWindow`), which bounds one request
  (20 daily series over ten years is ~73,000 instances), and NightMail never
  pages this route (one request, `$top` 100), so only another client walking
  many pages pays it repeatedly. Fix if one does: cache the sorted expansion
  per credential + calendar + window (+ the calendar's CTag) for a minute.
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

### Documents (`/shares`)
- **Share-token guessing through the façade isn't slowed by Nextcloud.** The
  api container's pinned address is exempt from Nextcloud's brute-force
  throttling (otherwise a few dead links from anyone throttled public shares
  for everyone; `deploy/README.md`), so the only bound is a Keycloak sign-in
  and the per-user rate limit. If that proves too loose, forward the reader's
  address instead (Nextcloud already trusts the stack subnet as a proxy) so
  Nextcloud throttles per reader.
- An unknown share token is a Nextcloud 503, which the DAV resilience pipeline
  probably retries before it is mapped to 404: extra latency on dead links,
  same answer.
- Collabora's outbound TLS verification is off in compose
  (`COLLABORA_SSL_VERIFICATION=false`) because it fetches richdocuments'
  settings over the Caddy-CA `cloud.` name. Turn it on with public certificates.
- In-browser editing was verified at the protocol level (WOPI session over the
  editor's websocket, save landed in Nextcloud), not in a rendered browser.

### Directory
- `/users/{shared}` for a shared mailbox that isn't a Keycloak user is 404. The
  client only calls it for the signed-in account.
- Graph-only profile fields (`jobTitle`, `department`, aliases, …) come from
  `nightware_*` Keycloak attributes that the dev realm doesn't declare yet, so
  they are null live and `proxyAddresses` is only `SMTP:<mail>`.

## Operations and security
- **Stalwart's default inbound throttle (25 messages/hour per sender domain and
  recipient)** would cap a whole single-domain organisation at 25 messages an
  hour to any one colleague. `deploy/stalwart/configure.sh` scopes it to
  unauthenticated inbound mail. It still limits Nextcloud's iMIP (sent
  unauthenticated on port 25): a busy organiser's invitations to one person
  could hit it. Review per deployment.
- **Stalwart doesn't check From against the sender's Identity.** The façade
  enforces it (403 `ErrorSendAsDenied`), but other Stalwart clients (IMAP/SMTP)
  can still spoof. Harden Stalwart's submission policy.
- **`DeltaLinks:SigningKey` is the façade's root secret.** It signs delta links
  and keys the seal on upload/download URLs, which carry callers' access tokens.
  Production refuses to start without one of at least 32 bytes, and compose has
  no default (`deploy/scripts/init-env.sh` generates it). Rotating it is
  disruptive: see `docs/operations/deployment.md`.
- **A delta link signed with another key answers 400, not 410.** The codec
  treats a bad signature as tampering (`invalidRequest`). NightMail's poller
  clears its cursor and resyncs on 400 as on 410, so a key rotation still
  recovers. A Graph client that only resyncs on 410 would not. Fix (frozen
  `Api/Graph/Json/DeltaLinkCodec.cs`): answer 410 for a signature mismatch.
- **The façade doesn't require `email_verified`.** Identity is the `email`
  claim (`/me`, and Stalwart's `claimUsername`). Users can no longer change
  their email or username themselves (the realm user profile makes both
  admin-only; `deploy/keycloak/configure.sh`), so the remaining exposure is an
  admin entering the wrong address. A check in `HttpUserContext` alone wouldn't
  help: the exchanged token still carries the email to Stalwart. Fix: an
  authorization policy on every Graph route that requires `email_verified=true`,
  enforced before token exchange. The realm has `verifyEmail` off, so
  admin-created users must then be marked verified. alice and bob already are.
- **Email is required but no longer user-editable.** A user created without an
  email can't add one in the account console. An admin must set it, which is
  the intended path because the address is the mailbox.
- **Token-endpoint availability.** NightMail treats any HTTP response to a token
  refresh (including a proxy 502/503) as revoked credentials and forces
  re-sign-in. Keep Keycloak highly available; never put a maintenance page in
  front of its token endpoint.
- Fetching an attachment with its content buffers it whole, with no size cap.
- **Stalwart allows 4 concurrent JMAP requests per account.** Several devices
  on one account polling together get 429s. Raise Stalwart's limit (runbook) or
  add a per-account concurrency limit in the façade.
- **No circuit breakers** on the backend HttpClients; they must count only 5xx,
  timeouts and network errors. The JMAP retry policy also retries 429 (DAV and
  Keycloak exclude it), so a Stalwart 429 can stall until the request budget
  answers 503.
- Per-user rate limits are per façade instance.
- **Load figures need an idle machine.** The phase-6 poll-cycle run (20 devices,
  machine saturated by other suites) missed the 300 ms p95 on inbox delta
  (486 ms), task lists (3.3 s) and calendarView (8.3 s).
- Backup procedures: only the `pg_dump`s were exercised; exports, the Stalwart
  tar and every restore are untested (`docs/operations/backup.md`).
- Docker's VM disk filled up during development (Postgres couldn't start);
  build cache is the usual culprit (`docker builder prune`).

## Deferred by design (ADR §43)
`beta/security/threatSubmission/emailThreats` returns 501: the client gates it
off for NightMail Server accounts (a phishing report is a junk move or a
Phishing Inbox forward). `DeferredOperationsTests` asserts the 501. The
`/shares` operations were implemented in phase 7.
