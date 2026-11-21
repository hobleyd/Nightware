# Phase 13 — Calendar parity

> **Status (2026-11-20): planned.** Decisions: [ADR 0006](../adr/0006-graph-parity.md) D6.

**Goal:** multiple calendars, event listing, recurrence instances, delta,
reminders, attachments, sharing and meeting-time suggestions.

## Step 0 (Nextcloud 35)
- `MKCALENDAR`, `PROPPATCH` (displayname, calendar-color) and `DELETE`: can the
  default "personal" calendar be deleted?
- `sync-collection` on calendars: token lifetime, and the error when a token is
  too old (→ 410).
- Is `ACKNOWLEDGED` / RFC 9074 snooze kept on PUT?
- Calendar sharing: the `oc:share` POST body and the read/write roles; sharing
  with a user who has never signed in.
- `ATTACH;VALUE=URI` on VEVENT survives PUT and round-trips.

## Operations
| id | Request |
|---|---|
| `calendar.calendars.list/get/create/update/delete` | `{mailbox}/calendars[/{id}]` |
| `calendar.calendar.get` | `{mailbox}/calendar` |
| `calendar.calendars.events.list/create` | `{mailbox}/calendars/{id}/events` |
| `calendar.calendars.calendar-view` | `{mailbox}/calendars/{id}/calendarView` |
| `calendar.events.list` | `GET {mailbox}/events` (`$top`, `$orderby`, `$select`, `$filter`) |
| `calendar.events.instances` | `GET {mailbox}/events/{id}/instances` |
| `calendar.calendar-view.delta` / `…-changes` | `GET {mailbox}/calendarView/delta` |
| `calendar.find-meeting-times` | `POST {mailbox}/findMeetingTimes` |
| `calendar.reminder-view` | `GET {mailbox}/reminderView(startDateTime='…',endDateTime='…')` |
| `calendar.events.snooze-reminder` / `dismiss-reminder` | `POST {mailbox}/events/{id}/snoozeReminder` / `dismissReminder` |
| `calendar.events.attachments.list/get/create/delete` | `{mailbox}/events/{id}/attachments[/{id}]` |
| `calendar.permissions.list/create/update/delete` | `{mailbox}/calendars/{id}/calendarPermissions[/{id}]` |
| `calendar.calendar-groups.list` / `calendar.calendar-groups.calendars` | `{mailbox}/calendarGroups[/{id}/calendars]` |

## Acceptance
- Create a second calendar, add a weekly series and list its instances.
- Delta reports an edit and a delete in the window.
- Share the calendar with bob, who sees it in his `/calendars`.

## Findings
- Nextcloud limits calendar creation (about ten MKCALENDARs per user per hour); past
  it Sabre answers 429 "groupware server is throttling", which the façade passes on.
  The live sharing test therefore shares the default calendar instead of a new one.
- Sharing is `POST oc:share` (`oc:set`, plus `oc:read-write` for write) on the
  calendar collection; sharees are read back from PROPFIND `oc:invite`. Only the
  owner may share, and only with a principal of this server (400 otherwise).
  Graph roles other than read/write answer 501.
