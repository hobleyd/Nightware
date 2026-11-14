# Phase 3 — Mail, write side

**Goal:** everything NightMail does that changes mail works against Nightware.

Depends on: Phase 1.1 skeleton. Parallel with: 2, 4, 5 (coordinate on
`Api/Features/Mail/Dto/` — additive changes only).

## Endpoints
- `PATCH messages/{id}` (isRead, flag, categories, body for drafts)
- `POST messages/{id}/move`, `DELETE messages/{id}`, `POST messages/{id}/permanentDelete`
- Folders: `POST mailFolders`, `POST mailFolders/{id}/childFolders`,
  `PATCH mailFolders/{id}`, `POST mailFolders/{id}/move`, `DELETE mailFolders/{id}`
- Drafts: `POST messages` (create draft), `PATCH messages/{draftId}`,
  `POST messages/{draftId}/attachments`, `DELETE .../attachments/{id}`,
  `POST messages/{draftId}/send`
- `POST sendMail` (with inline attachments, saveToSentItems)
- `POST messages/{id}/createReply|createReplyAll|createForward` (returns draft)
- `POST messages/{id}/reply|replyAll|forward` (comment + recipients, sends)

## Backend mapping (Stalwart JMAP)
- Keyword/mailbox changes → `Email/set`; permanent delete → `Email/set destroy`;
  delete → move to `trash` role mailbox (Graph semantics).
- Folder CRUD/move → `Mailbox/set` (`parentId`); delete with
  `onDestroyRemoveEmails`.
- Drafts → `Email/set create` in `drafts` with `$draft`; attachments via blob
  upload; updating a draft = create replacement + destroy old, **keeping the
  Graph id stable** via the adapter's id mapping (document the approach).
- Send → `EmailSubmission/set` with `onSuccessUpdateEmail` moving to `sent`.
- Reply/forward bodies built in Application (`ReplyComposer`): quoted header
  block, `In-Reply-To`/`References`, subject prefixes, recipient rules for
  reply-all (exclude self) — the way Outlook does it.

## File ownership
`Domain/Mail/Write/**`, `Application/Mail/Write/**`, `Adapters.Jmap/Mail/Write/**`,
`Api/Features/Mail/Write/**`, matching tests.

## Acceptance
- Unit tests incl. `ReplyComposer` edge cases (HTML/text, missing headers,
  reply-all self-exclusion); WireMock tests for each `Email/set`/
  `EmailSubmission/set` shape; endpoint tests; fixtures pass.
- Integration: compose → attach → send → appears in recipient inbox and own
  Sent; reply threads into same `conversationId`.
