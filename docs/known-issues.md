# Known issues and follow-ups

Collected from the phase reports (2026-11-15). Each item says where it bites and
what a fix needs. Ordered by impact within each section.

## Behaviour gaps the client can notice

### Mail
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
- **RSVP comments and proposed times reach outside organizers only.** For an
  organizer outside Nextcloud the façade mails the answer itself (an iTIP
  REPLY, or COUNTER with the proposed time) and keeps Nextcloud quiet. For an
  organizer inside Nextcloud, Sabre delivers the answer straight to their
  calendar and can't carry either, so they stay on the attendee's copy.
  `sendResponse: false` works for both. Cancel and forward comments are still
  not carried. Inbound COUNTERs from external organizers do work.
- **Every `calendarView` page re-expands the whole window.** The CalDAV
  expand REPORT has no paging, so each `$skiptoken` page expands, materialises
  and sorts every occurrence in the window again before slicing. The window is
  capped at ten years (`GetCalendarView.MaxWindow`), which bounds one request
  (20 daily series over ten years is ~73,000 instances), and NightMail never
  pages this route (one request, `$top` 100), so only another client walking
  many pages pays it repeatedly. Fix if one does: cache the sorted expansion
  per credential + calendar + window (+ the calendar's CTag) for a minute.
- Online-meeting fields are accepted and ignored.

### Directory
- **A photo upload must be a square JPEG or PNG.** Nextcloud keeps nothing
  else (its web UI crops in the browser) and the façade has no image library,
  so any other image is a 400 saying why. Graph accepts any image. Fix: crop
  in the façade, which needs an image library (a new package).

### Tasks and settings
- **Automatic replies: `contactsOnly` means internal senders only.** Contacts
  live in Nextcloud, out of the mail filter's reach, so nobody outside the
  entity's domains is answered. It reads back as `contactsOnly`. "Internal"
  means a sender from one of the domains of the entity that owns the mailbox's
  domain, looked up when the reply is saved.
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
- `/users/{shared}` describes a shared mailbox only to its members, by address
  and the name its identity was created with; renaming it in the console does
  not change that name (ADR 0004).
- A new member of a shared mailbox sees it in NightMail only once the façade's
  cached JMAP session expires (`Jmap:SessionCacheDuration`, 5 minutes) or their
  token is renewed. Removal is immediate (the mail server refuses).
- A shared mailbox has a calendar (ADR 0004 D6) but no contacts or files:
  those routes answer as for any address Nextcloud does not know. Shared
  mailboxes created before v1.39 get their calendar from the console's Repair.
- Graph-only profile fields (`jobTitle`, `department`, `companyName`,
  `officeLocation`, `businessPhones`, `mobilePhone`) are set on the console's
  user page ("Profile"). `keycloak/configure.sh` declares their attributes;
  until it has run on a stack they are null.

### Change stream (ADR 0007)
- A signal is up to `ChangeStream:PollInterval` (10 s) late: it polls backend
  state rather than following Stalwart's JMAP push.
- Contacts and To Do have no area yet; NightMail syncs them on its own timers.
- The per-user cap is per façade instance.

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
- **Email is required but no longer user-editable.** A user created without an
  email can't add one in the account console. An admin must set it, which is
  the intended path because the address is the mailbox.
- **Token-endpoint availability.** NightMail treats any HTTP response to a token
  refresh (including a proxy 502/503) as revoked credentials and forces
  re-sign-in. Keep Keycloak highly available; never put a maintenance page in
  front of its token endpoint.
- Per-user rate limits are per façade instance.
- **A cold start is slow when many devices poll at the same instant.** On a
  façade that has just started, every device's first calendarView (and task
  list) does the token exchange, principal discovery and calendar-home listing
  before its REPORT. Twenty devices on two accounts arriving in the same second
  took 1.6–2 s each on the dev stack (2026-11-21); every later cycle stayed
  under 160 ms (median 90 ms). Real devices don't start in step, so this shows
  up only in the load test's first cycle, which fills its p90/p95. Discovery is
  cached per exchanged token, so each device pays it again when its token is
  refreshed (every 15 minutes).
- Backup procedures: only the `pg_dump`s were exercised; exports, the Stalwart
  tar and every restore are untested (`docs/operations/backup.md`).
- Docker's VM disk filled up during development (Postgres couldn't start);
  build cache is the usual culprit (`docker builder prune`).
- **Dev stack TLS trust is off by default between backends.** With
  `CADDY_TLS=internal` Collabora (`COLLABORA_SSL_VERIFICATION=false`) and
  Stalwart's OIDC directory (`STALWART_ALLOW_INVALID_CERTS=true`) do not
  verify the public URL's certificate. With a publicly trusted certificate
  (ACME or certificate files) both can verify; set
  `COLLABORA_SSL_VERIFICATION=true` and `STALWART_ALLOW_INVALID_CERTS=false`
  (verified 2026-11-16 with a Let's Encrypt wildcard).
- **Stalwart and Keycloak share the `/auth/` prefix.** Stalwart can't run
  under a path, so its web admin (`/admin/`, `/account/`, `/login`) sits at
  the host root, and the sign-in it uses calls Stalwart's own OAuth endpoints
  (`/auth/token`, `/auth/jwks.json`, ...). Caddy sends those exact paths to
  Stalwart and the rest of `/auth/` to Keycloak, whose paths all sit under
  `/auth/realms`, `/auth/admin`, `/auth/resources` and `/auth/js`. A
  Stalwart upgrade that adds an OAuth endpoint needs it added to the list in
  `caddy/Caddyfile`; `smoke.sh` checks one from each side.
- **`BIND_ADDRESS` on macOS.** Docker Desktop can publish ports on
  `127.0.0.1` or `0.0.0.0` only, not on one specific other address such as a
  WireGuard interface; a LAN-facing dev stack listens on every interface.
  Collabora's plain port stays on `COLLABORA_BIND_ADDRESS` (loopback), since
  it offers `convert-to` to any private address.
- **`occ richdocuments:activate-config` clears the WOPI callback URL**, after
  which Collabora would call back on the public URL, outside its alias group.
  Rerun `deploy/nextcloud/configure.sh` after using it.

## Deferred by design (ADR §43)
`beta/security/threatSubmission/emailThreats` returns 501: the client gates it
off for NightMail Server accounts (a phishing report is a junk move or a
Phishing Inbox forward). `DeferredOperationsTests` asserts the 501. The
`/shares` operations were implemented in phase 7.

## Admin console (phase 8)

- **Shares an origin with Nextcloud, Stalwart's web UI, Keycloak and Collabora.**
  The console lives at `https://PUBLIC_HOST/console`; a cross-site-scripting flaw
  in any of those apps would run with the console's cookie in reach (a cookie
  path is not a security boundary). Fix: serve the console from its own host
  (e.g. `admin.PUBLIC_HOST`, needs a DNS name and certificate) with a `__Host-`
  cookie. Until then, keep `ADMIN_ALLOWED_CIDRS` narrow where you can.
- **Alias uniqueness against other users' aliases** is checked against mailboxes
  and Keycloak e-mail addresses, not against other users' `nightware_aliases`
  attributes.
- **Keycloak user updates are read-modify-write** (no ETag); an alias or marker
  write racing an offboarding could write `enabled: true` back. The console's
  own writes are sequential per request; two admins acting on one user at the
  same moment could still collide.
- **The user list** asks Stalwart and Nextcloud about each member (8 at a time,
  up to 200 members; above that it shows Keycloak only). An address missing from
  Stalwart costs a bounded scan of all accounts (no alias filter in Stalwart).
- **Nextcloud has no narrower role than administrator** for provisioning, so the
  console's Nextcloud account is a full administrator with a static password.
- **Stalwart rejects reserved test TLDs** (`.example`) as domains; use `.test` or
  a real domain on test stacks.
- **Not yet covered by automated live tests:** the walk-through in the phase
  report was run by hand against an isolated stack; `smoke.sh` and
  `Nightware.Integration.Tests` do not exercise the console yet.

