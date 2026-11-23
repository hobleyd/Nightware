# Phase 26 — Online meetings and calls over Talk

> **Status (2026-11-23): done, local only.** Asked for: "calling". Graph's calling API is
> application-only and needs a media bot; agreed scope (2026-11-23): online
> meetings with their recordings and transcripts, and delegated calls that ring
> people, media in Talk's clients. Decisions in ADR 0009 D7; Step 0 in phase 23
> and below. No tenant captures were possible: every fixture is constructed from
> Microsoft's reference page and says so.

## Operations
`docs/contract/endpoints.json`, phase 26 (14 operations): online meetings
create/get/find by `JoinWebUrl`/update/delete, recordings and transcripts
list/get/content, calls create/get/delete.

## Step 0 findings (local stack, Nextcloud 35, Talk 25.0.8, 2026-11-23)
- **Recordings.** The recording server uploads to
  `{attachment folder}/Recording/{token}/Recording yyyy-MM-dd HH-mm-ss.{ogg|webm}`
  in the files of whoever started the recording (Talk's `RecordingService`).
  The time in the name is the recording's start on the recorder's clock (UTC
  here: `Recording 2026-11-23 09-59-18.ogg`, started 09:59:15 UTC, uploaded
  09:59:35). The folder is named after the room token; Talk itself relies on
  that to tell which room a file belongs to (`storeTranscript` refuses a
  recording whose parent folder isn't the token). `nc:upload_time` is the
  upload. The attachment folder (`/Talk` by default) is the user's Talk
  setting and is in their capabilities (`spreed.config.attachments.folder`);
  a separate `recording_folder` user preference exists but isn't exposed.
- **Transcripts.** Talk's transcription writes `{recording name}.md` beside the
  recording: the text, a blank line, and a localised "Transcript is AI
  generated and may contain mistakes". A summary is `{name} - summary.md`.
  No cue times, no speakers. (Transcription is currently off on the dev stack,
  `call_recording_transcription=no`; the transcripts from phase 22's testing,
  in alice's `Talk/Recording/g4qoq2me/` and `oygp6e5d/`, show the format.)
- **The call session.** `POST room/{token}/participants/active` with the bearer
  answers with Nextcloud's session cookies. Two are needed and sufficient: the
  instance's session cookie (named per instance, e.g. `oc7tfh0d0328`; its value
  equals `nc_session_id`'s) and `oc_sessionPassphrase`. The same answer sets
  `nc_username`/`nc_token`, Nextcloud's 15-day "remember me" login, which the
  façade never keeps. Sending the session cookie *without* its passphrase
  destroys the session (every later request is 404).
- **No signaling, short life.** The façade's session never pings. `GET
  call/{token}` stops listing it after about 30 seconds, but the room keeps
  `hasCall` and the callee keeps ringing. The session is reaped, ending a call
  nobody joined ("call_left", "call_ended", and the callee's notification turns
  into "You missed a group call"), as soon as anything lists the room's
  participants (`GET room/{token}/participants`) more than
  `SESSION_TIMEOUT_KILL` (100 s) after the join: verified at 130 s, where
  `GET room` and `GET call` changed nothing and the participant listing ended
  the call at once. Without that, the probe call stayed up 8 minutes.
- **Ringing.** `POST call/{token}` with `silent=true` starts the call without
  notifying anyone; `POST call/{token}/ring/{attendeeId}` then gives the
  callee Talk's call notification. The ring lasts as long as the call
  (`GET call/{token}/notification-state` turns 201, "missed call", when it ends).
- **Leaving.** `DELETE call/{token}?all=true` ends the call for everyone when
  the caller moderates (in a one-to-one room both are owners). With a reaped
  session it is 404.
- **Meeting rooms.** `POST room` (JSON) takes `roomType` 3, `objectType` `event`
  with `objectId` `start#end`, and `participants.users` in one request; the
  user is added at once (bob saw the room in his list). Talk refuses to rename a
  room bound to an event (`PUT room/{token}` is 400 `{"error":"event"}`), as it
  refuses to re-time one. A room has no creation time; its first chat message
  (`conversation_created`) gives it.

## Done
| Gap | Version |
|---|---|
| Online meetings as public Talk rooms: create (bound to its span, directory attendees added as participants), get, find by `JoinWebUrl` (calendar events' rooms too), rename/re-span, delete; recordings and transcripts in the room's recording folder, listed, read and downloaded (transcripts as WebVTT) | set at merge |
| Delegated calls: `POST /communications/calls` rings the targets from the caller's one-to-one room (or a new group room), `GET` reports `establishing`/`established`/`terminated`, `DELETE` ends it; the call id seals the Talk session (`Calls:SealingKey`) | set at merge |

Verified live on the dev stack (`tests/Nightware.Integration.Tests/OnlineMeetings/`,
`/Calls/`, run with `NIGHTWARE_INTEGRATION=1 NIGHTWARE_CONNECT_ADDRESS=127.0.0.1
NIGHTWARE_DEPLOY_DIR=…/deploy`): alice creates a meeting with bob, bob has the
room in his Talk conversations, get/find (with the link plain and
percent-encoded) and a calendar event's meeting found by its `joinUrl`, a
rename and re-span (room unbound), delete then 404; files placed where the
recording server puts them list and download, the transcript as WebVTT without
the AI warning; alice calls bob, bob gets Talk's call notification, `GET` is
`establishing`, bob's token can't read alice's call id, `DELETE` ends the call
(`hasCall` false) and `GET` is `terminated`. Also by hand: a real recording
made by the `talk-recording` service in a public room (audio-only `.ogg`)
listed and downloaded over Graph with the recorder's start and upload times.

## Decisions (beyond ADR 0009 D7)
- **Meetings are the caller's.** `/users/{x}` works only when `x` is the caller
  (`403` otherwise), as the drive does: Talk is read as the caller. A meeting is
  a public room the caller takes part in (participant types 1–3); any other room
  is `404`.
- **Span.** A meeting created without both times is unbound and reports null
  `startDateTime`/`endDateTime` (Graph would invent now and an hour). Half a
  span is `400`. A rename or a changed span unbinds a bound room (Talk can
  neither rename nor re-time one), after which the meeting reports no span: the
  PATCH response says so rather than echoing times nothing keeps.
- **`joinMeetingIdSettings.joinMeetingId`** is the room token (what Talk joins
  by); `isPasscodeRequired` is the room's password flag.
- **Participants.** The owner is the organizer (`presenter`); moderators are
  `coorganizer`, users `attendee`. Users map to directory ids through
  `IUserAccountNames`; guests and email invitees have a name and a null id;
  groups and teams are left out. Unknown attendees on create are skipped (they
  join by the link).
- **Recordings** are read from the caller's own files (whoever started the
  recording has them; normally the organizer). `createdDateTime` is the start in
  the file name, `endDateTime` the upload; a recording and its transcript share
  the recording's file id as `contentCorrelationId`. Get and content look the id
  up in the room's folder, so no other file is reachable. Content answers `200`
  with the bytes (Graph's own shape; the `/content` route already gets the
  2-minute transfer budget), not the drive's 302; it streams with the drive's
  `ResponseStream`.
- **Transcript content** is WebVTT only (`$format=text/vtt` accepted, anything
  else `400`): one cue per paragraph spanning the recording, Talk's last
  paragraph (its localised warning) dropped.
- **Calls.** One target: the one-to-one room (Talk returns the existing one).
  Several: a new group room named after `subject` or the targets' names,
  deleted again if the call can't be placed. The call starts `silent` and each
  target is rung, so nobody else in the room is notified. `requestedModalities`
  defaults to `audio`; `callbackUri` is optional, and must be https and at most
  1024 characters (it rides in the id).
- **The call id** is AES-256-GCM (key derived from `Calls:SealingKey` with a
  purpose label) over the room token, the session's two cookies, a 16-byte hash
  of the Talk session id, the call's start time, the targets' uids, modalities
  and callback URI, with the caller's directory id as associated data: another
  user replaying it gets `404` and never sends alice's cookie.
- **State.** `terminated` once the room has no call, or a call with another
  start time, or is gone; `established` while `GET call/{token}` lists a peer
  other than the façade's session; `establishing` otherwise. Reading a call
  never lists participants (that would end it, see Step 0).
- **Hanging up** sends `DELETE call/{token}?all=true` and leaves the room. If
  Talk has already reaped the session but the targets are still being rung, a
  fresh session ends the call; a call already over is `204`.
- **Off-switches.** Without `Talk:PublicBaseUrl` every meeting route is `501`;
  without `Calls:SealingKey` (or Talk) every call route is `501`. A sealing key
  that is set must be at least 32 bytes of hex, or the host fails to start.
- **Shared Talk code** moved to `Adapters.Dav/Talk/` (`TalkOptions`, room
  token/link/name/span rules, lenient OCS JSON, the uid map); phase 22's
  `NextcloudTalkMeetings` uses it with unchanged behaviour and tests.
- **Registration by factory.** `TalkPeople` (needs the directory adapter's
  `IUserAccountNames`) and `TalkCalls` (needs the host's `IUserContext`) are
  registered with factories, so a host or test that registers the Dav adapter
  alone still passes `ValidateOnBuild`.

## Known gaps
- **A call nobody answers ends early when Talk is opened.** Any Talk client
  showing the room lists its participants, which reaps the façade's idle session
  after 100 s: the call ends and the callee sees a missed call. Until then it
  rings for as long as nobody looks (8 minutes in the probe). Media and keeping
  the session alive are the caller's own Talk client's job (`@nightware.joinWebUrl`).
- **No Graph notifications** to `callbackUri`, no `answer`/`reject`/`transfer`,
  no media (ADR 0009 D1).
- **Re-timing or renaming a meeting unbinds its room**, so Talk no longer
  expires it; it stays until deleted (as phase 22's moved meetings do).
- **Recordings started by someone else** are in their files, not the caller's,
  and don't list for the organizer. A user's separate Talk recording folder
  setting isn't visible over the API (the default `{attachments}/Recording` is
  assumed).
- **Transcripts have no cue times or speakers** (Talk keeps none). Graph's
  `metadataContent` and the `application/vnd.microsoft.graph.transcript+text`
  format are not offered. A transcript from Talk's transcription itself was not
  made in this phase (transcription is off on the dev stack); the format comes
  from phase 22's.
- **Recording content** streams within the 2-minute transfer budget; a very
  large video over a slow link may not finish (audio-only recordings, as live
  runs, are ~45 MB an hour).
- A meeting's `chatInfo.messageId` is always `"0"`; `audioConferencing`,
  `lobbyBypassSettings` and the other Teams-only members are not emitted.

## Deploy
- `Calls__SealingKey` from `NIGHTWARE_CALLS_KEY` in `deploy/docker-compose.yml`;
  `deploy/.env.example` documents it and `deploy/scripts/init-env.sh` generates
  it (32 bytes of hex) once. The live (ansible) compose file needs the same
  variable and a vault secret; not done here.
- Online meetings need only `Talk__PublicBaseUrl`, which phase 22 already sets.
