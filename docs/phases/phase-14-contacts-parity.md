# Phase 14 — Contacts parity

> **Status (2026-11-21): in progress** — 14a (contact CRUD, photo) done. Decisions: [ADR 0006](../adr/0006-graph-parity.md) D7.

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

## Findings (Step 0, Nextcloud 35)
- Nextcloud stores and serves vCard 3. Every property probed survived a PUT and GET
  unchanged: five-part N, NICKNAME, two-part ORG, ROLE, IMPP, URL;TYPE=WORK,
  CATEGORIES, ADR;TYPE=OTHER, RELATED;VALUE=text, X- properties and an X- parameter
  on EMAIL. `Accept: text/vcard; version=4.0` converts on the way out.
- A vCard 4 card PUT with `PHOTO:data:image/jpeg;base64,…` came back as vCard 3
  **without its photo**. Photos are therefore always written as vCard 3
  `PHOTO;ENCODING=b`.
- An address-book MKCOL (RFC 5689 extended MKCOL), PROPPATCH of `displayname`, and
  DELETE all work. The home also holds `z-server-generated--system` ("Accounts")
  and `z-app-generated--contactsinteraction--recent`, which are not folders.
- An initial `sync-collection` with a limit answers 507 for the collection, as on
  calendars: an initial delta takes the book's own `sync-token` from PROPFIND.
- After a session cookie is set, Nextcloud's CSRF check refuses further cookie-bearing
  DAV requests unless they send `OCS-APIRequest: true` (only the raw-DAV probes and
  live tests hit this; the adapter sends no cookies).
- `GET {mailbox}/contacts` keeps spanning every address book, because NightMail's
  typeahead depends on it; Graph lists only the default folder. New contacts go to
  the default book (`contacts`).
- The Graph birthday is shown at 11:59 UTC, as Exchange stores it, so no time zone
  moves it to another day.
