# Phase 14 — Contacts parity

> **Status (2026-11-20): planned.** Decisions: [ADR 0006](../adr/0006-graph-parity.md) D7.

**Goal:** full contact read/write across address books, with photos and delta.

## Step 0 (Nextcloud 35)
- vCard 4 vs 3: what Nextcloud stores and returns. Which Graph contact fields
  have no vCard home (they go in `X-` properties, or are dropped and
  documented)?
- `MKCOL` of an address book, rename, delete, and deleting the default.
- `sync-collection` on address books.

## Operations
| id | Request |
|---|---|
| `contacts.create/get/update/delete` | `{mailbox}/contacts[/{id}]` |
| `contacts.list-full` | `GET {mailbox}/contacts` with no `$select` |
| `contacts.photo-put` / `contacts.photo-get` | `{mailbox}/contacts/{id}/photo/$value` |
| `contacts.folders.list/get/create/update/delete` | `{mailbox}/contactFolders[/{id}]` |
| `contacts.folders.contacts.list/create` | `{mailbox}/contactFolders/{id}/contacts` |
| `contacts.folders.child-folders.list` | `{mailbox}/contactFolders/{id}/childFolders` (always empty) |
| `contacts.folders.delta` | `GET {mailbox}/contactFolders/delta` |
| `contacts.delta` / `contacts.delta-changes` | `GET {mailbox}/contactFolders/{id}/contacts/delta` |

## Acceptance
- A contact created over Graph appears in Nextcloud Contacts with every mapped
  field. An edit there shows in delta.
- NightMail's `contacts.list` fixture is unchanged.
