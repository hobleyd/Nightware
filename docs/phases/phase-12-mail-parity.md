# Phase 12 — Mail parity

> **Status (2026-11-20): planned.** Decisions: [ADR 0006](../adr/0006-graph-parity.md) D5.

**Goal:** flags, categories, importance, copy, inbox rules, master categories,
large attachments and folder delta work as on Exchange Online.

## Step 0 (local stack, before contracting)
- Can JMAP keywords be set on a received message, keeping its id, in this
  syntax: `$flagged`, `$nwcat-…`, `$nwimp-high`? Which keyword characters does
  Stalwart accept?
- Is there anywhere to keep flag dates per email?
- `urn:ietf:params:jmap:metadata`: can it keep a JSON value per account (for
  master categories)? If not, add the Keycloak attribute (§29) and the ansible
  change.
- `SieveScript/set` and `SieveScript/validate`: one active script per account?
  How does it interact with `VacationResponse`, and with a user's own script?
- `Email/import` of an existing blob into another mailbox gives a new id.

## Operations
| id | Request |
|---|---|
| `mail.messages.update-flag` | `PATCH {mailbox}/messages/{id}` `{flag}` |
| `mail.messages.update-categories` | `PATCH …/messages/{id}` `{categories}` |
| `mail.messages.update-importance` | `PATCH …/messages/{id}` `{importance}` |
| `mail.messages.copy` | `POST …/messages/{id}/copy` |
| `mail.master-categories.list/get/create/update/delete` | `{mailbox}/outlook/masterCategories[/{id}]` |
| `mail.rules.list/get/create/update/delete` | `{mailbox}/mailFolders/inbox/messageRules[/{id}]` |
| `mail.attachments.create-upload-session` | `POST …/messages/{id}/attachments/createUploadSession` |
| `mail.attachments.upload-session-put` | `PUT {uploadUrl}` (external, chunked; 200 per chunk, 201 at the end) |
| `mail.folders.delta` / `mail.folders.delta-changes` | `GET {mailbox}/mailFolders/delta` (initial, then after a change) |

The message PATCH dispatch gains a third arm: keyword-only fields on any
message. It must not disturb `update-read-state` or `drafts.update`.

Supported rule conditions: `senderContains`, `fromAddresses`,
`subjectContains`, `bodyContains`, `headerContains`, `sentToAddresses`,
`hasAttachments`, `importance`. Supported actions: `moveToFolder`,
`copyToFolder`, `delete`, `markAsRead`, `markImportance`, `assignCategories`,
`forwardTo`, `redirectTo`, `stopProcessingRules`. Exceptions mirror the
conditions.

## Acceptance
- Flag, categorise and mark important a received message: the id is unchanged
  and NightMail's own list still projects it.
- A rule "subject contains X → move to folder" files a live delivery.
- A 10 MB attachment uploaded in chunks arrives on the sent message.
- Folder delta reports a rename as an update.
