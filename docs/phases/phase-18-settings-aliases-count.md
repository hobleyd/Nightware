# Phase 18 — Settings, aliases, single items and `$count`

> **Status (2026-11-22): in progress, local only.** Asked for: the gaps
> recommended after phase 17 — anchor-based initial mail delta, the
> default-calendar and nested-item aliases, To Do single-item GETs,
> `mailboxSettings` sub-routes with `outlook/supportedTimeZones` and
> `supportedLanguages`, and `$count`.

## Step 0 findings (local stack, 2026-11-22)
- **Graph v1.0 has no `events/delta`.** Event delta is
  `calendarView/delta` only (event-delta reference); `events/delta` and
  `calendars/{id}/events/delta` are beta. Not added; ADR 0006 D6's mention of
  per-calendar `events/delta` is beta-only.
- **Stalwart supports `Email/query` `anchor`/`anchorOffset`** (RFC 8620 §5.5):
  `anchor: X, anchorOffset: 1` returns the page after X with its real
  `position`; a missing anchor is the method error `anchorNotFound` (the
  back-referenced `Email/get` then fails too, so the fallback resends).
- **Delta pages are sized by `$top`**, not `Prefer: odata.maxpagesize`.
- **Host culture and time-zone names differ** (macOS vs the Linux image), and
  newer CLDR writes a narrow no-break space before `tt`. The time zones and
  languages are a fixed table, generated once by
  `tools/outlook-catalog/generate.cs` and committed.
- **No fixtures could be captured** (no tenant token in this session). Every
  phase 18 fixture is constructed from Microsoft's reference page, says so in
  its provenance, and has a capture scenario (`tools/capture`, phase 18) to
  replace it.

## Done
| Gap | Version |
|---|---|
| Initial folder delta anchored after the last message seen | 1.44.2 |
| `mailboxSettings/{automaticRepliesSetting,timeZone,language,workingHours,dateFormat,timeFormat}`; `outlook/supportedTimeZones` (both standards), `outlook/supportedLanguages` | 1.45.0 |
| Aliases: `calendar/events` (list, create), `calendar/calendarView`, `calendar/events/{id}` and `calendars/{id}/events/{id}`, `mailFolders/{id}/messages/{id}`, `contactFolders/{id}/contacts/{id}` (get, update, delete) | 1.46.0 |

## Decisions
- An alias maps the base operation's own handler on Graph's other route, so
  the two can't drift. Its fixture is the base fixture on the alias path, and
  its conformance arrangement is the base's. `calendar/events` and
  `calendar/calendarView` resolve the default calendar and go through
  `calendars/{id}/…` (Nightware's top-level `/events` and `/calendarView`
  already read the default calendar only).
- Nested item routes do not check the parent segment: event, message and
  contact ids are unique in the mailbox. Literal routes (`…/delta`) keep
  precedence over `{id}` (tested).
- `mailboxSettings/userPurpose` and `delegateMeetingMessageDeliveryOptions`
  are 404: Nightware has no source for either. The single settings are
  read-only, as in Graph (PATCH goes to `mailboxSettings`).
- Unset values: language `en-US`; date and time formats from the language;
  working hours Exchange's defaults in the mailbox's zone, the same rule
  `findMeetingTimes` plans with.
