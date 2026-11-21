# Phase 15 — To Do parity

> **Status (2026-11-21): in progress** — 15a (lists, task get/delete, attachment get/delete) and 15b (checklist items, linked resources) done. Decisions: [ADR 0006](../adr/0006-graph-parity.md) D8.

**Goal:** list management, task delete, checklist items, linked resources and
delta.

## Step 0 (Nextcloud 35)
- Subtasks: how Nextcloud Tasks writes `RELATED-TO`. Does deleting the parent
  delete the children?
- Whether an unknown `X-` property survives an edit made in the Nextcloud Tasks
  UI.
- `sync-collection` on VTODO collections.

## Operations
| id | Request |
|---|---|
| `tasks.lists.create/get/update/delete` | `{mailbox}/todo/lists[/{id}]` |
| `tasks.lists.delta` | `GET {mailbox}/todo/lists/delta` |
| `tasks.tasks.get/delete` | `{mailbox}/todo/lists/{id}/tasks/{id}` |
| `tasks.tasks.delta` / `tasks.tasks.delta-changes` | `GET {mailbox}/todo/lists/{id}/tasks/delta` |
| `tasks.checklist-items.list/create/update/delete` | `…/tasks/{id}/checklistItems[/{id}]` |
| `tasks.linked-resources.list/create/update/delete` | `…/tasks/{id}/linkedResources[/{id}]` |
| `tasks.attachments.get/delete` | `…/tasks/{id}/attachments/{id}` |

## Acceptance
- A task with two checklist items shows as a task with subtasks in Nextcloud
  Tasks. Checking one off there shows `isChecked` over Graph.

## Findings (Step 0, Nextcloud 35)
- A subtask written as Nextcloud Tasks writes it (`RELATED-TO:<parent uid>`, no
  RELTYPE) is stored as is. DELETE of the parent over CalDAV leaves the child (the
  cascade is the Tasks app's, not the server's), so the façade deletes a task's
  checklist children itself.
- An `X-` property with parameter values holding `"`, `;`, `:`, `,` and non-ASCII
  survives PUT and GET. Whether the Nextcloud Tasks *UI* keeps unknown `X-`
  properties when it edits a task could not be tested here and is unverified.
- An initial `sync-collection` with a limit on a VTODO collection answers 507, as
  on calendars and address books.
- Task lists are calendars: MKCALENDAR counts against Nextcloud's calendar-creation
  limit (about ten an hour), so live tests make one list per run.
- A list is deleted only if it holds tasks alone; a collection that also holds
  events is refused (400), as are the default list (400) and shared lists (403).
- Graph trims trailing zeros from To Do timestamps (`…:06.231183Z`, `…:09Z`); the
  façade now does too, except the upload session's expiry, which NightMail's parser
  expects with seven digits. `categories` is omitted when empty, as NightMail's own
  task fixtures carry none.
- A checklist item is a subtask: a VTODO in the same list whose `RELATED-TO` names
  the task (no RELTYPE, as Nextcloud Tasks writes it); its id is its resource's
  `DavItemId`. Subtasks whose parent is in the same list are left out of the task
  listing; an orphan stays a task. Deleting a task deletes its subtasks (deepest
  first); its attachment files stay in Nextcloud Files.
- A linked resource is an `X-NIGHTWARE-LINKED-RESOURCE` property whose value is
  base64url JSON, so no iCalendar quoting is involved.
- The `checklistItems`, `linkedResources` and `todo/lists` create captures carry the
  same bare-service-root `Location` as `findMeetingTimes`; the fixtures drop it.
