# shellcheck shell=bash
# put_invite <nc-bearer> <organizer-uid> <attendee-email>...: PUT an event into
# the organizer's personal calendar with the given attendees + the Boardroom.
put_invite() {
  local tok="$1" org="$2"; shift 2
  local uid="nightware-imip-$(date +%s)-$RANDOM"
  local start end att=""
  start=$(date -u -v+1d +%Y%m%dT100000Z 2>/dev/null || date -u -d tomorrow +%Y%m%dT100000Z)
  end=$(date -u -v+1d +%Y%m%dT110000Z 2>/dev/null || date -u -d tomorrow +%Y%m%dT110000Z)
  for a in "$@"; do
    att+="ATTENDEE;PARTSTAT=NEEDS-ACTION;ROLE=REQ-PARTICIPANT;RSVP=TRUE:mailto:${a}"$'\r\n'
  done
  ccurl -f -o /dev/null -X PUT -H "Authorization: Bearer ${tok}" \
    -H 'Content-Type: text/calendar; charset=utf-8' \
    "${CLOUD_URL}/remote.php/dav/calendars/${org}/personal/${uid}.ics" --data-binary @- <<ICS || return 1
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Nightware//smoke//EN
BEGIN:VEVENT
UID:${uid}
DTSTAMP:$(date -u +%Y%m%dT%H%M%SZ)
DTSTART:${start}
DTEND:${end}
SUMMARY:Nightware iMIP smoke ${uid}
ORGANIZER:mailto:${org}@${MAIL_DOMAIN}
ATTENDEE;PARTSTAT=ACCEPTED;ROLE=CHAIR:mailto:${org}@${MAIL_DOMAIN}
${att}ATTENDEE;CUTYPE=ROOM;PARTSTAT=NEEDS-ACTION;ROLE=NON-PARTICIPANT;RSVP=TRUE:mailto:boardroom@${MAIL_DOMAIN}
END:VEVENT
END:VCALENDAR
ICS
  echo "$uid"
}
