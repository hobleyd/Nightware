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
  change moves a meeting's end later, or makes it a series, the room is
  unbound and stays until the meeting is deleted. The organizer deleting or
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
  verified, not audio/video between two people.
- **Rooms of meetings deleted by other clients** (Thunderbird, the Nextcloud
  calendar) are not closed. A single meeting's room still expires by itself.
- **A link added to one occurrence only** reaches attendees with the next
  significant change (see Decisions).
- `allowedOnlineMeetingProviders` on calendars stays `[]` (NightMail doesn't
  read it).

## Deploy
- `deploy/nextcloud/configure.sh` installs `spreed` and registers the `talk`
  service's signaling server and TURN/STUN with Talk (`TALK_*` in `.env`;
  `init-env.sh` generates the secrets).
- The API needs `Talk__PublicBaseUrl` (Nextcloud's public URL with its
  `/cloud/` sub-path): in `deploy/docker-compose.yml`, and in the ansible
  compose file for the live stack.
- Live (ansible): `nightware-talk` in `compose/nightware.yml`, its secrets in
  the vault (`nightware_talk_*`, into `talk.env` and `deploy/.env`), nginx's
  `/standalone-signaling/`, and `turn.sharpblue.com.au` (Cloudflare, DNS only;
  coredns gives the LAN 192.168.18.7). **fw must forward 3478 tcp and udp to
  the docker host** (192.168.18.7), as it does 25 and 443; it isn't managed
  by ansible.
