#!/usr/bin/env bash
# Record real JMAP (Stalwart) and CalDAV (Nextcloud) exchanges into
# deploy/recordings/ for use as WireMock fixtures. Test data only; no
# Authorization headers or tokens are written.
#
#   ./scripts/record.sh       (stack up + bootstrapped)
#
# Seeds alice's mailbox with two messages from bob (JMAP EmailSubmission) and
# alice's calendar with one event, then records:
#   jmap/01-session ... jmap/06-email-changes      request + response JSON
#   caldav/01-principal ... caldav/07-*            request + response XML
source "$(dirname "$0")/lib.sh"
OUT="${DEPLOY_DIR}/recordings"
mkdir -p "$OUT/jmap" "$OUT/caldav"

ALICE=$(exchange_token "$(user_token alice)" stalwart)
BOB=$(exchange_token "$(user_token bob)" stalwart)
ALICE_DAV=$(exchange_token "$(user_token alice)" nextcloud)

session() { ccurl -L -H "Authorization: Bearer $1" "${MAIL_URL}/.well-known/jmap"; }
jmap() { ccurl -H "Authorization: Bearer $1" -H 'Content-Type: application/json' "${MAIL_URL}/jmap/" -d "$2"; }
USING='["urn:ietf:params:jmap:core","urn:ietf:params:jmap:mail","urn:ietf:params:jmap:submission"]'

# save <dir/name> <request-json|-> <response>
save_jmap() {
  local name="$1" req="$2" resp="$3"
  [[ "$req" != "-" ]] && jq . <<<"$req" > "$OUT/jmap/${name}.request.json"
  jq . <<<"$resp" > "$OUT/jmap/${name}.response.json"
  log "recorded jmap/${name}"
}

ALICE_ACC=$(session "$ALICE" | jq -r '.primaryAccounts["urn:ietf:params:jmap:mail"]')
BOB_ACC=$(session "$BOB" | jq -r '.primaryAccounts["urn:ietf:params:jmap:mail"]')

# State before seeding, for Email/changes.
STATE0=$(jmap "$ALICE" "{\"using\":${USING},\"methodCalls\":[[\"Email/get\",{\"accountId\":\"${ALICE_ACC}\",\"ids\":[]},\"s\"]]}" \
  | jq -r '.methodResponses[0][1].state')

# ---- seed: bob -> alice, two messages (one HTML + attachment-less, one plain)
BOB_MB=$(jmap "$BOB" "{\"using\":${USING},\"methodCalls\":[[\"Mailbox/get\",{\"accountId\":\"${BOB_ACC}\",\"properties\":[\"role\"]},\"m\"],[\"Identity/get\",{\"accountId\":\"${BOB_ACC}\"},\"i\"]]}")
DRAFTS=$(jq -r '.methodResponses[0][1].list[] | select(.role=="drafts") | .id' <<<"$BOB_MB")
SENT=$(jq -r '.methodResponses[0][1].list[] | select(.role=="sent") | .id' <<<"$BOB_MB")
IDENT=$(jq -r '.methodResponses[1][1].list[0].id // empty' <<<"$BOB_MB")
if [[ -z "$IDENT" ]]; then
  IDENT=$(jmap "$BOB" "{\"using\":${USING},\"methodCalls\":[[\"Identity/set\",{\"accountId\":\"${BOB_ACC}\",\"create\":{\"i\":{\"name\":\"Bob Brown\",\"email\":\"bob@${BASE_DOMAIN}\"}}},\"c\"]]}" \
    | jq -r '.methodResponses[0][1].created.i.id')
fi
send() {
  local subject="$1" body="$2" html="$3"
  local email
  email=$(jq -cn --arg d "$DRAFTS" --arg dom "$BASE_DOMAIN" --arg s "$subject" --arg b "$body" --arg h "$html" '{
    mailboxIds:{($d):true}, keywords:{"$draft":true,"$seen":true},
    from:[{name:"Bob Brown",email:("bob@"+$dom)}], to:[{name:"Alice Anderson",email:("alice@"+$dom)}],
    subject:$s,
    bodyStructure:(if $h=="" then {type:"text/plain",partId:"t"} else
      {type:"multipart/alternative",subParts:[{type:"text/plain",partId:"t"},{type:"text/html",partId:"h"}]} end),
    bodyValues:({t:{value:$b}} + (if $h=="" then {} else {h:{value:$h}} end))}')
  jmap "$BOB" "{\"using\":${USING},\"methodCalls\":[
    [\"Email/set\",{\"accountId\":\"${BOB_ACC}\",\"create\":{\"e\":${email}}},\"a\"],
    [\"EmailSubmission/set\",{\"accountId\":\"${BOB_ACC}\",\"create\":{\"s\":{\"identityId\":\"${IDENT}\",\"emailId\":\"#e\"}},
      \"onSuccessUpdateEmail\":{\"#s\":{\"mailboxIds/${DRAFTS}\":null,\"mailboxIds/${SENT}\":true,\"keywords/\$draft\":null}}},\"b\"]]}" \
    | jq -e '.methodResponses[1][1].created.s' >/dev/null
}
send "Quarterly planning" "Hi Alice,

Can we sync on the Q3 plan on Thursday?

Bob" "<p>Hi Alice,</p><p>Can we sync on the <b>Q3 plan</b> on Thursday?</p><p>Bob</p>"
send "Lunch?" "Free for lunch tomorrow? -- Bob" ""
sleep 3

# ---- JMAP recordings (alice)
save_jmap 01-session - "$(session "$ALICE")"

REQ=$(jq -cn --argjson u "$USING" --arg a "$ALICE_ACC" '{using:$u,methodCalls:[["Mailbox/get",{accountId:$a,ids:null},"0"]]}')
save_jmap 02-mailbox-get "$REQ" "$(jmap "$ALICE" "$REQ")"

INBOX=$(jmap "$ALICE" "$REQ" | jq -r '.methodResponses[0][1].list[] | select(.role=="inbox") | .id')
REQ=$(jq -cn --argjson u "$USING" --arg a "$ALICE_ACC" --arg m "$INBOX" '{using:$u,methodCalls:[["Email/query",{accountId:$a,filter:{inMailbox:$m},sort:[{property:"receivedAt",isAscending:false}],position:0,limit:25,calculateTotal:true},"0"]]}')
QR=$(jmap "$ALICE" "$REQ")
save_jmap 03-email-query "$REQ" "$QR"

IDS=$(jq -c '.methodResponses[0][1].ids' <<<"$QR")
REQ=$(jq -cn --argjson u "$USING" --arg a "$ALICE_ACC" --argjson ids "$IDS" '{using:$u,methodCalls:[["Email/get",{accountId:$a,ids:$ids,
  properties:["id","blobId","threadId","mailboxIds","keywords","size","receivedAt","messageId","inReplyTo","references","sender","from","to","cc","bcc","replyTo","subject","sentAt","hasAttachment","preview","bodyStructure","bodyValues","textBody","htmlBody","attachments","headers"],
  fetchTextBodyValues:true,fetchHTMLBodyValues:true,maxBodyValueBytes:262144},"0"]]}')
save_jmap 04-email-get "$REQ" "$(jmap "$ALICE" "$REQ")"

REQ=$(jq -cn --argjson u "$USING" --arg a "$ALICE_ACC" --argjson ids "$IDS" '{using:$u,methodCalls:[
  ["Email/query",{accountId:$a,sort:[{property:"receivedAt",isAscending:false}],collapseThreads:true,limit:10},"q"],
  ["Email/get",{accountId:$a,"#ids":{resultOf:"q",name:"Email/query",path:"/ids"},properties:["threadId","subject","from","receivedAt","preview","keywords"]},"g"],
  ["Thread/get",{accountId:$a,"#ids":{resultOf:"g",name:"Email/get",path:"/list/*/threadId"}},"t"]]}')
save_jmap 05-query-get-thread-batched "$REQ" "$(jmap "$ALICE" "$REQ")"

REQ=$(jq -cn --argjson u "$USING" --arg a "$ALICE_ACC" --arg s "$STATE0" '{using:$u,methodCalls:[["Email/changes",{accountId:$a,sinceState:$s,maxChanges:100},"0"]]}')
save_jmap 06-email-changes "$REQ" "$(jmap "$ALICE" "$REQ")"

# Mark one read -> Email/set + Mailbox/changes, a typical delta follow-up.
FIRST=$(jq -r '.[0]' <<<"$IDS")
MSTATE=$(jmap "$ALICE" "{\"using\":${USING},\"methodCalls\":[[\"Mailbox/get\",{\"accountId\":\"${ALICE_ACC}\",\"ids\":[]},\"s\"]]}" | jq -r '.methodResponses[0][1].state')
REQ=$(jq -cn --argjson u "$USING" --arg a "$ALICE_ACC" --arg id "$FIRST" '{using:$u,methodCalls:[["Email/set",{accountId:$a,update:{($id):{"keywords/$seen":true}}},"0"]]}')
save_jmap 07-email-set-seen "$REQ" "$(jmap "$ALICE" "$REQ")"
REQ=$(jq -cn --argjson u "$USING" --arg a "$ALICE_ACC" --arg s "$MSTATE" '{using:$u,methodCalls:[["Mailbox/changes",{accountId:$a,sinceState:$s},"0"]]}')
save_jmap 08-mailbox-changes "$REQ" "$(jmap "$ALICE" "$REQ")"

REQ=$(jq -cn --argjson u "$USING" --arg a "$ALICE_ACC" '{using:$u,methodCalls:[["Email/get",{accountId:$a,ids:["does-not-exist"]},"0"],["Mailbox/get",{accountId:"no-such-account"},"1"]]}')
save_jmap 09-errors-notfound-and-accountnotfound "$REQ" "$(jmap "$ALICE" "$REQ")"

# ---- CalDAV recordings (alice)
source "$DEPLOY_DIR/scripts/imip-event.sh"
EVENT_UID=$(put_invite "$ALICE_DAV" alice "bob@${BASE_DOMAIN}")
# dav <name> <method> <path> <depth> <body>
dav() {
  local name="$1" method="$2" path="$3" depth="$4" body="$5" hdr
  printf '%s %s\nDepth: %s\nContent-Type: application/xml; charset=utf-8\n\n%s\n' "$method" "$path" "$depth" "$body" \
    > "$OUT/caldav/${name}.request.txt"
  hdr=$(mktemp)
  ccurl -X "$method" -H "Authorization: Bearer ${ALICE_DAV}" -H "Depth: ${depth}" \
    -H 'Content-Type: application/xml; charset=utf-8' -D "$hdr" --data "$body" "${CLOUD_URL}${path}" \
    | xmllint --format - > "$OUT/caldav/${name}.response.xml"
  grep -iE '^(HTTP/|content-type|etag|dav:)' "$hdr" | tr -d '\r' > "$OUT/caldav/${name}.response-headers.txt"
  rm -f "$hdr"
  log "recorded caldav/${name}"
}
dav 01-principal PROPFIND /remote.php/dav/principals/users/alice/ 0 \
'<d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:card="urn:ietf:params:xml:ns:carddav"><d:prop><d:current-user-principal/><d:displayname/><c:calendar-home-set/><c:calendar-user-address-set/><c:schedule-inbox-URL/><c:schedule-outbox-URL/><card:addressbook-home-set/></d:prop></d:propfind>'
dav 02-calendar-home PROPFIND /remote.php/dav/calendars/alice/ 1 \
'<d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/" xmlns:ic="http://apple.com/ns/ical/" xmlns:oc="http://owncloud.org/ns"><d:prop><d:resourcetype/><d:displayname/><cs:getctag/><d:sync-token/><c:supported-calendar-component-set/><ic:calendar-color/><d:current-user-privilege-set/><oc:owner-principal/></d:prop></d:propfind>'
FROM=$(date -u +%Y%m%dT000000Z); TO=$(date -u -v+7d +%Y%m%dT000000Z 2>/dev/null || date -u -d '+7 days' +%Y%m%dT000000Z)
dav 03-calendar-query-timerange REPORT /remote.php/dav/calendars/alice/personal/ 1 \
"<c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><d:getetag/><c:calendar-data/></d:prop><c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\"><c:time-range start=\"${FROM}\" end=\"${TO}\"/></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>"
dav 04-sync-collection-initial REPORT /remote.php/dav/calendars/alice/personal/ 1 \
'<d:sync-collection xmlns:d="DAV:"><d:sync-token/><d:sync-level>1</d:sync-level><d:prop><d:getetag/></d:prop></d:sync-collection>'
dav 05-calendar-multiget REPORT /remote.php/dav/calendars/alice/personal/ 1 \
"<c:calendar-multiget xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><d:getetag/><c:calendar-data/></d:prop><d:href>/remote.php/dav/calendars/alice/personal/${EVENT_UID}.ics</d:href></c:calendar-multiget>"
dav 06-room-principals PROPFIND /remote.php/dav/principals/calendar-rooms/ 1 \
'<d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:nc="http://nextcloud.com/ns"><d:prop><d:displayname/><c:calendar-user-type/><c:calendar-user-address-set/><nc:room-type/><nc:room-seating-capacity/><nc:room-building-address/><nc:room-building-room-number/><nc:room-features/></d:prop></d:propfind>'
dav 07-principal-search-rooms REPORT /remote.php/dav/principals/calendar-rooms/ 0 \
'<d:principal-property-search xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" test="anyof"><d:property-search><d:prop><d:displayname/></d:prop><d:match>Board</d:match></d:property-search><d:prop><d:displayname/><c:calendar-user-type/><c:calendar-user-address-set/></d:prop></d:principal-property-search>'
dav 08-freebusy-outbox-props PROPFIND /remote.php/dav/calendars/alice/outbox/ 0 \
'<d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:current-user-privilege-set/></d:prop></d:propfind>'

# Free/busy for a room and a user via the scheduling outbox (RFC 6638 POST).
FB_START=$(date -u -v+1d +%Y%m%dT000000Z 2>/dev/null || date -u -d tomorrow +%Y%m%dT000000Z)
FB_END=$(date -u -v+2d +%Y%m%dT000000Z 2>/dev/null || date -u -d '+2 days' +%Y%m%dT000000Z)
FB=$(printf '%s\r\n' 'BEGIN:VCALENDAR' 'VERSION:2.0' 'PRODID:-//Nightware//record//EN' 'METHOD:REQUEST' \
  'BEGIN:VFREEBUSY' "UID:nightware-fb-$(date +%s)" "DTSTAMP:$(date -u +%Y%m%dT%H%M%SZ)" \
  "DTSTART:${FB_START}" "DTEND:${FB_END}" "ORGANIZER:mailto:alice@${BASE_DOMAIN}" \
  "ATTENDEE:mailto:boardroom@${BASE_DOMAIN}" "ATTENDEE:mailto:bob@${BASE_DOMAIN}" 'END:VFREEBUSY' 'END:VCALENDAR')
printf 'POST /remote.php/dav/calendars/alice/outbox/\nContent-Type: text/calendar; charset=utf-8\n\n%s\n' "$FB" > "$OUT/caldav/09-freebusy-post.request.txt"
ccurl -X POST -H "Authorization: Bearer ${ALICE_DAV}" -H 'Content-Type: text/calendar; charset=utf-8' \
  --data-binary "$FB" "${CLOUD_URL}/remote.php/dav/calendars/alice/outbox/" | xmllint --format - > "$OUT/caldav/09-freebusy-post.response.xml"
log "recorded caldav/09-freebusy-post"

cat > "$OUT/README.md" <<EOF
# Recorded backend exchanges

Captured by \`deploy/scripts/record.sh\` against the dev stack (Stalwart
v1.1.0, Nextcloud 35.0.3) on $(date -u +%Y-%m-%d). Test users alice/bob,
domain \`${BASE_DOMAIN}\`. Authorization headers are not recorded; every
request was sent with a Keycloak access token obtained by token exchange
(aud \`stalwart\` for JMAP, \`nextcloud\` for CalDAV).

- \`jmap/NN-*.request.json\` — body POSTed to \`${MAIL_URL}/jmap/\`
  (01-session is \`GET /.well-known/jmap\` → 307 → \`/jmap/session\`).
- \`jmap/NN-*.response.json\` — response body.
- \`caldav/NN-*.request.txt\` — method, path, Depth and XML body sent to
  \`${CLOUD_URL}\`; \`*.response.xml\` is the (pretty-printed) multistatus;
  \`*.response-headers.txt\` holds status line, content-type, ETag, DAV.

Ids, states and sync-tokens are real values from that run; they are only
meaningful together (e.g. 06-email-changes' \`sinceState\` is the state
before the two seeded messages arrived).
EOF
log "recordings written to ${OUT}"
