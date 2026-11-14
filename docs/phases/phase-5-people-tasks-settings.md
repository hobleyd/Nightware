# Phase 5 — Contacts, people, directory, tasks, settings, files, security

**Goal:** the remaining Graph surface NightMail uses.

Depends on: Phase 1.1 skeleton. Parallel with: 2, 3, 4. Split in two
independent work streams.

## 5A — Directory, contacts, people
- `GET /me`, `GET /users/{id|upn}` (+ `$select`), `GET /users?$filter`/`$search`
  — Keycloak (userinfo / admin API) → `Adapters.Directory`.
- `GET /users/{addr}/photo/$value` — Nextcloud avatar API (404 → Graph 404).
- `GET contacts` (+ paging), contact CRUD if the client uses it — CardDAV
  (vCard 3/4 ↔ Graph contact) → `Adapters.Dav/Contacts`.
- `GET people?$search` — ranked merge (Application `PeopleRanker`) of contacts,
  directory users, and recent correspondents (from the mail read port).
- Ownership: `*/Directory/**`, `*/Contacts/**`, `*/People/**`.

## 5B — Tasks, mailbox settings, files, threat submission
- To Do: `GET todo/lists`, `GET/POST/PATCH/DELETE todo/lists/{id}/tasks[/{id}]`,
  task attachments (`GET`, `$value`, `createUploadSession`) — CalDAV VTODO
  collections (Nextcloud Tasks) → `Adapters.Dav/Tasks`; attachments stored in
  Nextcloud Files and referenced by ATTACH URL; upload sessions implemented as
  a chunked-upload proxy (Nextcloud chunking v2).
- `GET/PATCH mailboxSettings` (automaticRepliesSetting, timeZone, language,
  workingHours) — JMAP `VacationResponse` for auto-replies
  (`Adapters.Jmap/MailboxSettings`); other settings in a JMAP-stored
  preferences blob or Nextcloud user prefs (decide, document).
- `GET /shares/{shareId}/driveItem[/content]` — decode Graph's `u!` sharing
  URL, map to Nextcloud public share token → OCS + WebDAV
  (`Adapters.Dav/Files`).
- `POST beta/security/threatSubmission/emailThreats` — move to junk and train
  Stalwart's spam classifier (`Adapters.Jmap/Security`), return a Graph-shaped
  submission.
- Ownership: `*/Tasks/**`, `*/MailboxSettings/**`, `*/Files/**`, `*/Security/**`.

## Acceptance
- Unit + WireMock + endpoint tests for each route; fixtures pass.
- Integration: set auto-reply → external sender receives it; task round-trip;
  shared-file link opens.
