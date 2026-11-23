# Phase 26 — Online meetings and calls over Talk

> **Status (2026-11-23): planned.** Asked for: "calling". Graph's calling API is
> application-only and needs a media bot; agreed scope (2026-11-23): online
> meetings with their recordings and transcripts, and delegated calls that ring
> people, media in Talk's clients. Decisions in ADR 0009 D7; Step 0 in phase 23.

## Operations
`docs/contract/endpoints.json`, phase 26 (14 operations): online meetings
create/get/find by `JoinWebUrl`/update/delete, recordings and transcripts
list/get/content, calls create/get/delete.

## Build notes
- Feature folder `OnlineMeetings/` (meetings, recordings, transcripts) and
  `Calls/` in every project. Room creation reuses phase 22's Talk options and
  token rules (`Adapters.Dav/Calendar/Talk/`); move shared pieces into
  `Adapters.Dav/Talk/` rather than copying them.
- The Talk session for calls needs cookies through the OCS client
  (`OcsExchangeAsync`, ADR 0009 D8); the façade's `HttpClient` is cookie-free by
  design, so the cookie travels explicitly in the request and the sealed call id.
- `Calls:SealingKey` like `Subscriptions:SealingKey` (env, validated at start;
  without it `/communications/calls` is 501).
- Step 0 left: where Talk's recording server and transcription put their files
  (folder, names) and how to tell which room a file belongs to; whether the
  façade's call session (no signaling connection, no media) stays in the call
  and how long; whether a ring survives once the façade's session leaves.

## Acceptance
- alice creates a meeting over Graph with bob as attendee; bob sees it in his
  chats; `$filter=JoinWebUrl eq` finds it and a calendar event's meeting.
- A recording and its transcript made on the local stack list and download
  over Graph.
- alice places a call to bob over Graph; bob gets Talk's call notification;
  `GET` reports `establishing`, then `terminated` after `DELETE`.
