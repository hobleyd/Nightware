# Phase 12 — Mail parity

> **Status (2026-11-21): done.** Decisions: [ADR 0006](../adr/0006-graph-parity.md) D5.

**Goal:** flags, categories, importance, copy, inbox rules, master categories,
large attachments and folder delta work as on Exchange Online.

## Step 0 findings (local stack, Stalwart 1.1.0, 2026-11-21)

**Keywords are no home for categories.** Stalwart lower-cases keywords
(`$NWflag` reads back `$nwflag`), refuses spaces, `%` and non-ASCII, and caps a
keyword at 128 characters. `$flagged` set on a received message keeps its id.

**Per-message JSON: `urn:ietf:params:jmap:metadata`.**
- `Email` has `metadata` (shared) and `privateMetadata` properties. Each holds
  namespaces whose names must contain a dot (`nightware.ms`) and whose values
  are objects. Each namespace value is capped at **8192 bytes**.
- Setting the whole property replaces it. A path patch
  (`metadata/nightware.ms/importance`) merges.
- A metadata change keeps the id. It shows in `Email/changes`
  (`updatedProperties: ["metadata"]`).
- `Mailbox` takes metadata too: the Inbox can hold the master category list
  (about 80 categories in 8 KB).
- `Email/import` ignores metadata, so `copy` must set it afterwards, in the
  same request. Import gives a new id.

**Sieve: one active script per account.**
- `VacationResponse` is Stalwart's own generated script, named `vacation`.
  Enabling it deactivates any other script.
- Disabling it leaves the script in place, inactive. So including it would keep
  sending replies after "off".
- `VacationResponse.isEnabled` reads back from whether that script is active.
- Its block is
  `currentdate :value "ge"/"le" "iso8601"` around
  `vacation :mime :subject "…" "<multipart/alternative>"`.
- A Nightware-generated script holding the same vacation block followed by
  rules was delivered through end to end:
  - the reply arrived, with escaped quotes and dot-stuffed `text:` bodies;
  - `fileinto :mailboxid` filed the mail;
  - `addflag "\\Seen"` became `$seen`.
- `SieveScript/validate` reports errors without storing anything.
- Extensions include `body`, `fileinto`, `mailboxid`, `imap4flags`,
  `vacation`, `date`, `relational`, `editheader`, `copy` and `include`.
  `maxNumberRedirects` is 1.

**Not probed:** whether a shared-mailbox member who is not the owner may write
the shared `metadata`.

## Decisions from Step 0 (ADR 0006 D5, amended)
- **Flag.** `flagged` is the `$flagged` keyword, which NightMail already reads.
  `complete`, the flag dates and the completion date go in
  `metadata/nightware.ms/flag`.
- **Categories and importance** go in `metadata/nightware.ms`, shared because
  Exchange keeps them on the item. A message's categories need not be master
  categories.
- **Master categories** go in the Inbox's `metadata/nightware.ms`. No Keycloak
  attribute and no ansible change. A list over 8 KB is refused (400).
- **Reads ask for `metadata`** only when the session advertises the
  capability. Otherwise reads fall back to keywords and the header.
- **Rules and auto-replies share one Nightware-owned script,
  `nightware-rules`.** It holds the auto-reply block first (Stalwart's exact
  shape), then the rules in sequence order. Its state (rules and auto-reply
  setting) is kept as base64 JSON on `#` comment lines. Every write runs
  `SieveScript/validate` first.
  - **Without our script:** auto-replies are read from `VacationResponse`, as
    today.
  - **First write of either:** the current `VacationResponse` is folded into
    the script.
  - **Our script inactive** (vacation toggled elsewhere): `VacationResponse`
    and inactive rules are what is reported. The next write reactivates the
    script.
- **Graph actions:**
  - `delete` → `fileinto` Deleted Items;
  - `permanentDelete` → `discard`;
  - `moveToFolder`/`copyToFolder` → `fileinto [:copy] :mailboxid`;
  - `markAsRead` → `addflag "\\Seen"`;
  - `forwardTo` → `redirect :copy` (one only, as `maxNumberRedirects` is 1);
  - `redirectTo` → `redirect`.

  `reject` is never used.

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

Supported rule conditions (and exceptions):
- `subjectContains`, `bodyContains`, `bodyOrSubjectContains`;
- `senderContains`, `recipientContains`;
- `fromAddresses`, `sentToAddresses`;
- `hasAttachments`, `importance`.

Supported actions:
- `moveToFolder`, `copyToFolder`;
- `delete` (to Deleted Items), `permanentDelete` (`discard`);
- `markAsRead`;
- `forwardTo` / `redirectTo` (one address in all: `maxNumberRedirects` is 1);
- `stopProcessingRules`.

Anything else is `501` naming the field:
- `headerContains`: Sieve cannot search every header.
- `assignCategories`: categories live in JMAP metadata, which Sieve cannot
  write.
- `markImportance`, `isAutomaticReply`, the size and date predicates, and the
  rest.

## Acceptance
- Flag, categorise and mark important a received message: the id is unchanged
  and NightMail's own list still projects it.
- A rule "subject contains X → move to folder" files a live delivery.
- A 10 MB attachment uploaded in chunks arrives on the draft byte for byte
  (`DraftUploadLiveTests`).
- Folder delta reports a rename as an update.

## Built
All five slices: flag/categories/importance and copy, master categories,
folder delta, Inbox rules shared with auto-replies, and upload sessions. Each
is checked against the Microsoft 365 captures (conformance) and on the local
stack:
- `MailParityLiveTests`
- `MasterCategoryLiveTests`
- `FolderDeltaLiveTests`
- `MessageRuleLiveTests`
- `DraftUploadLiveTests`

Departures from the plan:
- Categories and flag dates use JMAP metadata, not keywords.
- Upload chunks are staged in Stalwart, not Nextcloud.
- `headerContains` and `assignCategories` are not available in rules (501).
