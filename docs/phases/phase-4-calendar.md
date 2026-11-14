# Phase 4 — Calendar, scheduling and rooms

**Goal:** NightMail's calendar, invitations, RSVP, free/busy and room booking
work against Nightware.

Depends on: Phase 1.1 skeleton. Parallel with: 2, 3, 5.

## Endpoints
- `GET calendarView?startDateTime&endDateTime` (expanded occurrences,
  `Prefer: outlook.timezone`)
- `GET/POST/PATCH/DELETE events/{id}`
- `POST events/{id}/accept|decline|tentativelyAccept` (comment, sendResponse)
- `POST events/{id}/cancel`, `POST events/{id}/forward`
- `GET messages/{id}/microsoft.graph.eventMessage[Request|Response]/event` and
  `messages/{id}/event` (invitation mail → calendar event)
- `POST calendar/getSchedule` (free/busy for people and rooms)
- `GET places/microsoft.graph.room`, `GET beta/me/findRooms`

## Backend mapping (Nextcloud CalDAV, Sabre)
- Events ↔ VEVENT via Ical.Net; ids are `DavId(href)`; `iCalUId` = UID.
- `calendarView` → `calendar-query` REPORT with `<C:expand>` (server-side
  recurrence expansion); occurrence ids encode master href + RECURRENCE-ID.
- `seriesMasterId`, `type` (singleInstance/occurrence/exception/seriesMaster).
- RSVP → update own ATTENDEE PARTSTAT; Sabre's scheduling plugin emits the
  iTIP REPLY (iMIP via Stalwart). Cancel → organizer METHOD:CANCEL.
- Invitation mail → event: read the message's `text/calendar` part through the
  **mail read port** (Application port, not the JMAP adapter directly), match
  UID to the user's calendar (`calendar-query` on UID), fall back to the
  parsed iCalendar if not yet delivered to the calendar.
- `getSchedule` → CalDAV scheduling outbox free-busy POST (RFC 6638).
- Rooms → `calendar_resource_management` room principals.
- Times: Graph `dateTimeTimeZone` ↔ iCalendar with Windows/IANA zone mapping
  (`TimeZoneInfo.TryConvertIanaIdToWindowsId`).

## File ownership
`Domain/Calendar/**`, `Domain/Rooms/**`, `Application/Calendar/**`,
`Application/Rooms/**`, `Adapters.Dav/Calendar/**`, `Adapters.Dav/Rooms/**`,
`Api/Features/Calendar/**`, `Api/Features/Rooms/**`, matching tests.

## Acceptance
- Unit tests for iCalendar↔domain mapping (recurrence, exceptions, all-day,
  timezones), RSVP state transitions; WireMock tests for every DAV request;
  endpoint tests; fixtures pass.
- Integration: create meeting with attendee → attendee receives iMIP mail →
  `messages/{id}/event` resolves → accept → organizer sees PARTSTAT=ACCEPTED.
