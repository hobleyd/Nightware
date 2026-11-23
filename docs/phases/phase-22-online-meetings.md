# Phase 22 — Online meetings in Nextcloud Talk

> **Status (2026-11-22): done, local only.** Asked for: open-source online
> meetings; of Nextcloud Talk, Jitsi, BigBlueButton, LiveKit and Matrix, Talk
> was chosen (option 1): it is already behind the stack's Nextcloud and
> Keycloak, and needs no new identity or service to start with. NightMail asks
> for a meeting with `isOnlineMeeting: true` (`onlineMeetingProvider:
> 'teamsForBusiness'`) and reads only `onlineMeeting.joinUrl`; until now
> Nightware ignored the flag.

## Step 0 findings (local stack, Nextcloud 35.0.3, Talk 25.0.8)
- **A user's bearer token reaches Talk's OCS API.** `POST
  /ocs/v2.php/apps/spreed/api/v4/room` with the token-exchanged `nextcloud`
  token creates a room owned by that user. Success is OCS `statuscode` 201,
  which the DAV client used to treat as a failure (only 100/200 were success).
- **Event rooms.** `objectType=event`, `objectId=<start>#<end>` (Unix seconds)
  binds a room to a meeting's span; Talk's `ExpireObjectRooms` job deletes it
  `retention_event_rooms` days (28) after the end, never while a call is on.
  There is no way to re-time a bound room, only to unbind it
  (`DELETE room/{token}/object`, 400 when it is bound to nothing).
- **Nextcloud's iMIP mail shows the event's `URL`** as a "Link:" line, so the
  join link reaches external attendees without editing the event's body.
  Internal attendees' copies carry it too (`onlineMeeting.joinUrl`, verified
  through Graph as bob).
- `DELETE room/{token}` is 200, then 404.

## Done
| Gap | Version |
|---|---|
| `isOnlineMeeting: true` on `POST events`, `POST calendars/{id}/events` and `PATCH events/{id}` opens a Talk room whose link is `onlineMeeting.joinUrl`; deleting or cancelling the meeting closes it (verified live on the local stack) | 1.59.0 |
| Turning an existing meeting online sends the attendees the update ("Invitation updated", with the link); Talk's High-Performance Backend and a TURN server (the `talk` service), registered with Talk by `configure.sh` (both verified live on the local stack) | 1.60.0 |
| Call recording: Talk's recording server (the `talk-recording` service), registered with Talk by `configure.sh`; a recording started over OCS was uploaded to the starter's files with its notification (verified on the local stack, no media in the call) | 1.61.0 |
| Recording transcripts: integration_openai on any OpenAI-compatible server (a separate transcription URL allowed), Talk's `call_recording_transcription`; a speech webm uploaded as a recording came back as a transcript `.md` with its notification (verified on the local stack with speaches) | 1.62.0 |
| The join link also in `LOCATION` (after the place, stripped again on read), so Talk renames and re-times a meeting's room with its event; a move or rename no longer unbinds it (ADR 0009 D7, verified live) | 1.68.0 |
| Audio-only recordings (`TALK_RECORDING_AUDIO_ONLY`): the recorder's video settings are rewritten so Talk's video recording comes out as an Opus `.ogg` (verified on the local stack; on for live) | 1.63.0 |

## Decisions
- **One public room per meeting**, named after the subject (255 characters at
  most; "Online meeting" without one), opened as the caller. Public (type 3), so
  attendees outside the server join by the link as guests; Nextcloud users
  join as themselves. Attendees are not added as room participants: the
  invitation carries the link, and Talk's own email invitations would be a
  second invitation.
- **The link is the event's iCalendar `URL`**, `{Talk:PublicBaseUrl}call/{token}`;
  the codec already reads `URL` into `onlineMeetingUrl`. Not
  `X-MICROSOFT-SKYPETEAMSMEETINGURL`: Outlook would offer it as a Teams meeting.
  `onlineMeetingProvider` is accepted and ignored.
- **Lifetime.** A single meeting's room is bound to its span, so Talk tidies it
  up once the meeting is long over, even when the event is never deleted. A
  series' room is unbound (it has no end Nightware can name cheaply). When a
  change makes a meeting a series the room is unbound and stays until the
  meeting is deleted. *Amended (2026-11-23):* a moved or renamed meeting's
  room is no longer unbound: Talk follows the event (below).
- **The link in `LOCATION` too** (ADR 0009 D7). Talk's `CalDavEventListener`
  renames a room bound to an event and moves its span whenever the room
  owner's event whose `LOCATION` ends in `…/call/{token}` changes (inside the
  CalDAV write). So an event whose `URL` is a Talk link (`…/call/{token}`)
  stores `LOCATION` as `{place}, {link}` (or `{link}`), and reading strips
  that trailing link again, so Graph's `location`/`locations` (and the room
  matched by name) are unchanged, in calendars and in mailed invitations;
  any other event's `LOCATION` is read and written as before. Talk takes
  `SUMMARY` as the room name (or "Meeting"), and `DESCRIPTION` as its
  description.
- **Events made before** (link in `URL` only): the editor adds the link to
  `LOCATION` with the organiser's next change of subject, time, place or
  link, whose PUT Talk then follows (verified live: such an event moved 40
  days later re-timed its bound room). Not with other edits (categories,
  reminders…): `LOCATION` is a significant change for Nextcloud and would
  send attendees an update for nothing. An attendee's copy is never
  rewritten. NightMail resends the subject with every edit, so in practice
  its first edit upgrades the event, with one "updated" message to
  attendees. A room already unbound by the old rule stays unbound.
- **Talk's listener runs inside every such write**, and fails it (HTTP 500)
  for an event without `DTEND` (it reads `DTEND` unchecked) or with an empty
  `SUMMARY` (an empty room name), probed on Talk 25.0.8. So when the link
  goes into `LOCATION`, a `DURATION`-only event gets a `DTEND` and an empty
  `SUMMARY` is dropped (Talk names the room "Meeting"). A subject longer than
  254 bytes is cut by Talk at a byte boundary plus "…" (multibyte subjects
  were probed: accepted).
- Nextcloud's invitation mail shows the link on its "Location:" line as well
  as "Link:". The organizer deleting or
  cancelling the whole meeting closes the room; one occurrence, or an
  attendee's copy, never does.
- **Turning an event online later** (`PATCH` with `isOnlineMeeting: true`)
  works for the organizer's events that have no link yet. As in Graph, `false`
  is ignored: an online meeting stays online. The attendees are sent the
  update: Nextcloud doesn't count `URL` as a significant change (its
  `TipBroker` list is times, recurrence, status, summary, description,
  location), so the editor bumps SEQUENCE and marks each person attendee on
  the main component `SCHEDULE-FORCE-SEND=REQUEST` (RFC 6638). Sabre strips
  the parameter from that component once it has sent, so it never sticks. Not
  on the organizer's own line (it gets no message, so the parameter would
  stay), on rooms and resources, or on one occurrence's component (Sabre
  strips it from the main component only), so a link on one occurrence alone
  reaches attendees with the next real change.
- **Calls go through our own servers** (the `talk` service, Nextcloud AIO's
  `aio-talk`): the signaling server (High-Performance Backend) with Janus as
  SFU, so each participant sends one stream instead of one per peer, and
  eturnal as TURN and STUN. Talk's default public STUN server
  (`stun.nextcloud.com`) is removed: clients learn their address from ours.
  Everything rides on one port, 3478 tcp+udp: Janus and the clients all relay
  through the same eturnal. Janus reaches it by its own container name, not
  127.0.0.1, whose replies the kernel drops on the way to Janus's sockets
  (calls then connected only after a failed first attempt, seen live
  2026-11-23). The live stack reaches TURN by a name Cloudflare
  doesn't proxy (`TALK_TURN_HOST`), since Cloudflare carries only HTTP.
- **A Talk failure never fails the event write.** The event is saved without a
  link (the client sees no `joinUrl`), and the adapter logs a warning. A room
  opened for a write that then fails is closed again.
- **Off unless configured.** Without `Talk:PublicBaseUrl` nothing changes, and
  no event is read to look for a room. `Dav:BaseUrl` (an internal address)
  cannot stand in for the public URL.

## Known gaps
- **No browser call has been placed** on either stack: the signaling login,
  Nextcloud's signed notifications and an authenticated TURN allocation were
  verified, not audio/video between two people. Likewise no recording has
  captured a participant's audio or video; the recorder reaches Janus only
  by its compose-network address (deploy/README.md "Online meetings").
- **Long recordings and PHP memory.** integration_openai reads the whole
  recording into PHP to send it (memory_limit 1G here). A 1080p recording
  runs about 400 MB an hour (1.58 MB for 14 s), so meetings much past an hour
  may fail to transcribe; untested. Audio-only recordings (~45 MB an
  hour) leave plenty of room.
- **`PUBLIC_HOST=*.localhost` breaks Talk** inside the containers (curl
  resolves it to loopback): rooms fail to open. Use another name
  locally.
- **Delegated writes don't move the room.** Talk follows only the room
  owner's own calendar. An event written in someone else's calendar
  (`/users/{x}/events`) has its room opened by the caller, so Talk ignores
  that event, and a move no longer unbinds the room either: moved more than
  `retention_event_rooms` (28) days later, the room can expire before the
  meeting. Likewise a room owned by someone other than the calendar's owner.
- **Talk ignores an event whose `LOCATION` has parameters** (it looks for the
  literal `LOCATION:`); Nightware writes none, but another client might.
- **Rooms of meetings deleted by other clients** (Thunderbird, the Nextcloud
  calendar) are not closed. A single meeting's room still expires by itself.
- **A link added to one occurrence only** reaches attendees with the next
  significant change (see Decisions).
- `allowedOnlineMeetingProviders` on calendars stays `[]` (NightMail doesn't
  read it).

## Deploy
- `deploy/nextcloud/configure.sh` installs `spreed` and registers the `talk`
  service's signaling server and TURN/STUN with Talk (`TALK_*` in `.env`;
  `init-env.sh` generates the secrets), and the `talk-recording` service as
  its recording server (`TALK_RECORDING_SECRET`).
- The API needs `Talk__PublicBaseUrl` (Nextcloud's public URL with its
  `/cloud/` sub-path): in `deploy/docker-compose.yml`, and in the ansible
  compose file for the live stack.
- Live (ansible): `nightware-talk` in `compose/nightware.yml`, its secrets in
  the vault (`nightware_talk_*`, into `talk.env` and `deploy/.env`), nginx's
  `/standalone-signaling/`, and `turn.sharpblue.com.au` (Cloudflare, DNS only;
  coredns gives the LAN 192.168.18.7). **fw must forward 3478 tcp and udp to
  the docker host** (192.168.18.7), as it does 25 and 443; it isn't managed
  by ansible.
- Transcripts: `NEXTCLOUD_AI_URL` and/or `NEXTCLOUD_AI_TRANSCRIPTION_URL`
  with `NEXTCLOUD_AI_TRANSCRIPTION_MODEL` (optionally `_SUMMARY_MODEL` and the
  keys) in `.env`; `configure.sh` sets up integration_openai and Talk's
  transcription, or turns both off. The transcription server must take webm
  and Ogg (OpenAI, a Whisper server; not Ollama's audio models).
- Live recording and transcripts (in the ansible repo, not yet applied):
  `nightware-talk-recording` in `compose/nightware.yml`, reached as
  `TALK_RECORDING_URL=http://nightware-talk-recording:1234`, its secret
  `nightware_talk_recording_secret` in the vault (into `talk-recording.env`
  and `deploy/.env`); transcripts from ishtar's `whisper-api` (speaches,
  on demand, `http://ishtar.sharpblue.com.au:10310/v1`,
  `deepdml/faster-whisper-large-v3-turbo-ct2`; `roles/ishtar_apps`, and
  `ishtar_firewall` opens 10310 to the LAN), summaries off (Ollama ready for
  them), recordings audio only (`TALK_RECORDING_AUDIO_ONLY: "true"` and
  the checkout's `talk-recording/run.sh` mounted). No nginx route or
  firewall port on the docker host.
