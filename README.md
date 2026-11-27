# Nightware

Self-hosted, Microsoft Graph–compatible backend for the
[NightMail](../nightmail) client: a stateless Graph façade over Stalwart (mail),
Nextcloud (calendar, contacts, tasks, files, Talk) and Keycloak (identity).

```
NightMail ──HTTPS + OIDC bearer──▶ Nightware.Api (Graph façade)
                                     │
            ┌────────────────────────┼─────────────────────────┐
            ▼ JMAP                   ▼ CalDAV/CardDAV/WebDAV/OCS ▼ OIDC / admin API
        Stalwart                  Nextcloud (+ Talk, Office)  Keycloak
```

The façade keeps no data of record: ids and delta tokens encode backend state,
so the servers underneath stay the source of truth. Only the Graph surface in
[`docs/contract/endpoints.json`](docs/contract/endpoints.json) is implemented;
anything else answers with a Graph-shaped `501 notImplemented`. Response shapes
are pinned by golden fixtures captured from Microsoft 365.

## Graph API

| Area | Backed by | What works |
|---|---|---|
| **Mail** | Stalwart (JMAP) | Folders (create, move, copy, delta), message lists with `$filter`/`$orderby`/`$search` and paging, conversations, MIME read and MIME `POST`, drafts, reply/forward, `sendMail`, attachments incl. large-file upload sessions, flags, categories (`masterCategories`), importance, copy/move, message and folder delta |
| **Inbox rules & auto-replies** | Stalwart (Sieve, VacationResponse) | Inbox rules as one Sieve script; automatic replies with separate internal/external messages, scheduling, and contacts-only replies |
| **Calendar** | Nextcloud (CalDAV) | Calendars and calendar groups, events, recurrence and instances, `calendarView` and its delta, attachments (incl. upload sessions), reminders (snooze/dismiss), RSVP with comments and proposed times, `findMeetingTimes` over free/busy and working hours, calendar sharing, rooms and room lists, outside invitations imported from mail |
| **Contacts** | Nextcloud (CardDAV) | Contacts and contact folders, photos, delta |
| **To Do** | Nextcloud (CalDAV VTODO) | Lists, tasks, checklist items, linked resources, attachments, list and task delta |
| **Mailbox settings** | Keycloak attributes + Stalwart | Time zone, language, working hours, date/time formats, supported time zones and languages |
| **Directory & people** | Keycloak | Users, profile fields, photos (set your own; non-square photos are cropped), groups, manager and organization, people typeahead incl. groups and shared mailboxes |
| **Shared mailboxes** | Stalwart + Nextcloud | Opened by members, with a shared calendar, address book and drive |
| **OneDrive** | Nextcloud Files | The caller's drive: browse, upload (simple up to 250 MB and upload sessions), download URLs, hashes, sharing and view-only links, `sharedWithMe`, recent, versions, check-out/check-in, preview, search; Nextcloud share links via `/shares`; Office conversion through Collabora |
| **Chats** | Nextcloud Talk | Chats, messages, members, chat search (KQL) |
| **Teams & channels** | Nextcloud Teams + Talk | Teams, channels and channel messages |
| **Online meetings & calls** | Nextcloud Talk | Meetings whose Talk room follows the calendar event, delegated calls that ring through Talk, recordings and AI transcripts (any OpenAI-compatible server) |
| **Presence** | Nextcloud user status | Presence, including live out-of-office |
| **Search** | All of the above | Microsoft Search (`/search/query`) for drive items and chat/channel messages |
| **Change notifications** | Subscription store | Webhook subscriptions for mail, events, contacts, tasks, chats and channel messages, with lifecycle events; plus a long-poll change stream that tells NightMail when to sync |
| **Plumbing** | — | JSON `$batch`, `$count` and the `/$count` segment, `$select`/`$top`/`$skip`, `Prefer` headers, Graph's alias and parent-scoped routes, `translateExchangeIds` |

Known gaps are listed in [`docs/known-issues.md`](docs/known-issues.md).

## Admin console

`/console/` is the system of record for the deployment; every action reaches
Keycloak, Stalwart and Nextcloud together.

- **Sign-in:** OIDC through Keycloak with a passkey, or an authenticator code as
  fallback; one OTP is good for 8 hours across the console, Grafana and
  Stalwart's web admin.
- **Entities, domains and users:** onboard and offboard users (offboarding blocks
  every sign-in), set profile fields and automatic replies, verify email, see
  each user's access history with what each system said; a domain's catch-all.
- **Groups and shared mailboxes:** groups kept in Keycloak and mirrored to a
  Stalwart list and a Nextcloud group; shared mailboxes with calendar, address
  book and drive.
- **DNS:** per-domain MX, SPF, DMARC and MTA-STS records, hardened defaults
  suggested from the server's public address, and one-click publishing to
  Cloudflare, DigitalOcean, Gandi, Hetzner, Amazon Route 53, Azure DNS or Google
  Cloud DNS with the entity's own credential.
- **Reports:** DMARC and TLS reports analysed by Stalwart, each with the fix
  beside it.
- **Dashboard:** what needs attention, system health for Keycloak, Stalwart,
  Nextcloud and Grafana, and links into each one's own admin console.
- **Apps:** change-notification subscriptions, and an OpenAPI 3.1 reference with
  Swagger UI for third parties.

## Running it

A Docker Compose stack (Caddy, Keycloak, Stalwart, Nextcloud with Collabora,
Talk's High-Performance Backend, TURN and recording server, Grafana) serves
everything from one host. See [`deploy/README.md`](deploy/README.md) and
[`docs/operations/`](docs/operations) for configuration, deployment,
monitoring, backups and upgrades.

```bash
dotnet build
dotnet test
```

## Documentation

- Architecture and conventions: [`docs/architecture.md`](docs/architecture.md)
- Decisions: [`docs/adr/`](docs/adr)
- Delivery plan: [`docs/phases/`](docs/phases)
- Operations: [`docs/operations/`](docs/operations)

## Licence

GNU General Public License v3.0 — see [LICENSE](LICENSE).
