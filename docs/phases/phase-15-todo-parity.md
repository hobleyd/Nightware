# Phase 15 — To Do parity

> **Status (2026-11-20): planned.** Decisions: [ADR 0006](../adr/0006-graph-parity.md) D8.

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
