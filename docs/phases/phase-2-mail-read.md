# Phase 2 — Mail, read side

**Goal:** NightMail's inbox syncs and reads against Nightware.

Depends on: Phase 1.1 skeleton. Parallel with: 3, 4, 5.

## Endpoints (`/me/...` and `/users/{id}/...`, see `docs/contract/endpoints.json`)
- `GET mailFolders`, `mailFolders/{id|wellKnownName}`, `mailFolders/{id}/childFolders`
- `GET mailFolders/{id}/messages` (+ `$select/$filter/$orderby/$top/$skip`)
- `GET messages` (+ `$search` KQL, `$filter` on `conversationId` etc.)
- `GET mailFolders/{id}/messages/delta` (initial, `nextLink`, `deltaLink`, `@removed`)
- `GET messages/{id}` (+ `$select`, `$expand=attachments(...)`,
  `internetMessageHeaders`, `singleValueExtendedProperties`)
- `GET messages/{id}/$value` (raw MIME)
- `GET messages/{id}/attachments`, `attachments/{id}`, `attachments/{id}/$value`
  (file and item attachments)
- `Prefer: outlook.body-content-type="text"|"html"` and
  `outlook.timezone` on returned datetimes

## Backend mapping (Stalwart JMAP)
| Graph | JMAP |
|---|---|
| mailFolder, wellKnownName | `Mailbox`, `role` (inbox, sent→sentitems, drafts, trash→deleteditems, junk→junkemail, archive) |
| unreadItemCount/totalItemCount | `unreadEmails`/`totalEmails` |
| message list/filter/order | `Email/query` + `Email/get` |
| delta | `Email/queryChanges` (per-mailbox filter) + `Email/changes`, state in `DeltaToken` |
| conversationId | `threadId` |
| isRead/flag | `$seen` / `$flagged` keywords |
| body/uniqueBody/bodyPreview | `bodyValues`, `preview` |
| internetMessageHeaders | `headers` |
| `$value` / attachment content | blob download |
| `$search` KQL | translator to JMAP `FilterCondition` (`text`, `from`, `to`, `subject`, `hasAttachment`, `before/after`) |

## File ownership
`Domain/Mail/**` (read models), `Application/Mail/Read/**`,
`Adapters.Jmap/Mail/Read/**`, `Api/Features/Mail/Read/**`, matching test folders.
Shared mail DTOs live in `Api/Features/Mail/Dto/` — Phase 2 owns them, Phase 3
may add fields.

## Acceptance
- Unit tests for every use case and mapper; WireMock tests for every JMAP call
  shape; endpoint tests for every route; conformance fixtures for the routes pass.
- Integration test: seed a mailbox via SMTP, run an initial delta + an
  incremental delta, read a message with attachments.
