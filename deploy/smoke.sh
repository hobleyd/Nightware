#!/usr/bin/env bash
# Smoke test for the running dev stack (after ./bootstrap.sh).
#   ./smoke.sh            all checks; exit status = number of failures
#   SMOKE_IMIP=0 ./smoke.sh   skip the (slower) iMIP delivery check
source "$(dirname "$0")/scripts/lib.sh"
source "$DEPLOY_DIR/scripts/imip-event.sh"
set +e
fails=0
check() { local name="$1"; shift; if "$@"; then ok "$name"; else bad "$name"; fails=$((fails+1)); fi; }

[[ -s "$CA_FILE" ]] || export_ca >/dev/null

log "Keycloak"
check "OIDC discovery issuer = ${ISSUER}" \
  bash -c "curl -sS --cacert '$CA_FILE' '${ISSUER}/.well-known/openid-configuration' | jq -e --arg i '$ISSUER' '.issuer==\$i' >/dev/null"

ALICE=$(user_token alice 2>/dev/null)
check "password grant for alice (nightware-dev)" test -n "$ALICE"
check "alice token: aud contains nightware-api, email=alice@${BASE_DOMAIN}" \
  bash -c "jq -e '([.aud]|flatten|index(\"nightware-api\"))!=null and .email==\"alice@${BASE_DOMAIN}\"' <<<'$(jwt_claims "$ALICE")' >/dev/null"
PKCE=$("$DEPLOY_DIR/get-token.sh" --pkce --json alice 2>/dev/null)
check "nightmail auth-code + PKCE, redirect http://localhost:34571 (headless)" \
  bash -c "jq -e '.access_token and .refresh_token' >/dev/null <<<'$PKCE'"
REFRESHED=$(ccurl "$TOKEN_URL" -d grant_type=refresh_token -d client_id=nightmail \
  --data-urlencode "refresh_token=$(jq -r .refresh_token <<<"$PKCE")" \
  --data-urlencode "scope=openid email profile offline_access" 2>/dev/null | jq -r '.access_token // empty')
check "nightmail refresh (scope=openid email profile offline_access)" test -n "$REFRESHED"
check "CORS for ${NIGHTMAIL_WEB_ORIGIN:-http://localhost:34572} on discovery + token endpoint" bash -c "
  curl -sS --cacert '$CA_FILE' -D - -o /dev/null -H 'Origin: ${NIGHTMAIL_WEB_ORIGIN:-http://localhost:34572}' '${ISSUER}/.well-known/openid-configuration' | grep -qi '^access-control-allow-origin: ${NIGHTMAIL_WEB_ORIGIN:-http://localhost:34572}' &&
  curl -sS --cacert '$CA_FILE' -D - -o /dev/null -X OPTIONS -H 'Origin: ${NIGHTMAIL_WEB_ORIGIN:-http://localhost:34572}' -H 'Access-Control-Request-Method: POST' '${TOKEN_URL}' | grep -qi '^access-control-allow-origin: ${NIGHTMAIL_WEB_ORIGIN:-http://localhost:34572}'"

ALICE_JMAP=$(exchange_token "$ALICE" stalwart 2>/dev/null)
check "token exchange alice -> aud=stalwart" \
  bash -c "jq -e '.aud==\"stalwart\" and .azp==\"nightware-api\"' <<<'$(jwt_claims "${ALICE_JMAP:-x.e30.x}")' >/dev/null"
ALICE_DAV=$(exchange_token "$ALICE" nextcloud 2>/dev/null)
check "token exchange alice -> aud=nextcloud" \
  bash -c "jq -e '.aud==\"nextcloud\"' <<<'$(jwt_claims "${ALICE_DAV:-x.e30.x}")' >/dev/null"
SVC=$(service_token 2>/dev/null)
check "service account can read users (view-users)" \
  bash -c "curl -sS --cacert '$CA_FILE' -H 'Authorization: Bearer $SVC' '${AUTH_URL}/admin/realms/nightware/users?username=alice&exact=true' | jq -e '.[0].attributes.zoneinfo[0]' >/dev/null"

log "Stalwart (JMAP)"
SESSION=$(ccurl -L -H "Authorization: Bearer ${ALICE_JMAP}" "${MAIL_URL}/.well-known/jmap" 2>/dev/null)
check "GET /.well-known/jmap with exchanged bearer -> session for alice" \
  bash -c "jq -e --arg u 'alice@${BASE_DOMAIN}' '.username==\$u and (.capabilities|has(\"urn:ietf:params:jmap:mail\"))' >/dev/null <<<'$SESSION'"
ACC=$(jq -r '.primaryAccounts["urn:ietf:params:jmap:mail"] // empty' <<<"$SESSION")
check "Mailbox/get returns an inbox" bash -c "curl -sS --cacert '$CA_FILE' -H 'Authorization: Bearer ${ALICE_JMAP}' -H 'Content-Type: application/json' '${MAIL_URL}/jmap/' \
  -d '{\"using\":[\"urn:ietf:params:jmap:core\",\"urn:ietf:params:jmap:mail\"],\"methodCalls\":[[\"Mailbox/get\",{\"accountId\":\"${ACC}\",\"properties\":[\"role\"]},\"a\"]]}' \
  | jq -e '.methodResponses[0][1].list | map(.role) | index(\"inbox\")' >/dev/null"
code=$(ccurl -L -o /dev/null -w '%{http_code}' -H "Authorization: Bearer ${ALICE}" "${MAIL_URL}/.well-known/jmap" 2>/dev/null)
check "un-exchanged NightMail token (aud nightware-api) is rejected by Stalwart (${code})" test "$code" = 401
check "master-user fallback: Basic alice%admin opens alice's session" \
  bash -c "curl -sS -L --cacert '$CA_FILE' -u 'alice@${BASE_DOMAIN}%${STALWART_ADMIN_USER}:${STALWART_ADMIN_PASSWORD}' '${MAIL_URL}/.well-known/jmap' | jq -e '.username==\"alice@${BASE_DOMAIN}\"' >/dev/null"

log "Nextcloud (CalDAV)"
PF='<d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:current-user-principal/><c:calendar-home-set/></d:prop></d:propfind>'
out=$(ccurl -X PROPFIND -H "Authorization: Bearer ${ALICE_DAV}" -H 'Depth: 0' -H 'Content-Type: application/xml' --data "$PF" \
  "${CLOUD_URL}/remote.php/dav/principals/users/alice/" 2>/dev/null)
check "PROPFIND principal with exchanged bearer -> calendar-home-set" grep -q '/remote.php/dav/calendars/alice/' <<<"$out"
out=$(ccurl -X PROPFIND -H "Authorization: Bearer ${ALICE_DAV}" -H 'Depth: 1' "${CLOUD_URL}/remote.php/dav/principals/calendar-rooms/" 2>/dev/null)
check "room principals: Boardroom + Focus Room 1" \
  bash -c "grep -q calendar_resource_management-nightware-boardroom <<<'$out' && grep -q calendar_resource_management-nightware-focus-1 <<<'$out'"
code=$(ccurl -o /dev/null -w '%{http_code}' -X PROPFIND -H "Authorization: Bearer ${ALICE}" -H 'Depth: 0' "${CLOUD_URL}/remote.php/dav/principals/users/alice/" 2>/dev/null)
check "un-exchanged token (aud nightware-api) is rejected by Nextcloud (${code})" test "$code" = 401

if [[ "${SMOKE_IMIP:-1}" == 1 ]]; then
  log "iMIP (Nextcloud -> Stalwart SMTP -> bob's inbox)"
  uid=$(put_invite "$ALICE_DAV" alice "bob@${BASE_DOMAIN}" 2>/dev/null)
  check "alice creates an event with bob + Boardroom (CalDAV PUT)" test -n "$uid"
  BOB_JMAP=$(exchange_token "$(user_token bob)" stalwart 2>/dev/null)
  BOB_ACC=$(ccurl -L -H "Authorization: Bearer ${BOB_JMAP}" "${MAIL_URL}/.well-known/jmap" | jq -r '.primaryAccounts["urn:ietf:params:jmap:mail"]')
  jmap_bob() {
    ccurl -H "Authorization: Bearer ${BOB_JMAP}" -H 'Content-Type: application/json' "${MAIL_URL}/jmap/" \
      -d "{\"using\":[\"urn:ietf:params:jmap:core\",\"urn:ietf:params:jmap:mail\"],\"methodCalls\":[$1]}"
  }
  INBOX=$(jmap_bob "[\"Mailbox/query\",{\"accountId\":\"${BOB_ACC}\",\"filter\":{\"role\":\"inbox\"}},\"m\"]" \
    | jq -r '.methodResponses[0][1].ids[0]')
  found=0
  for _ in $(seq 1 20); do
    n=$(jmap_bob "[\"Email/query\",{\"accountId\":\"${BOB_ACC}\",\"filter\":{\"inMailbox\":\"${INBOX}\",\"subject\":\"${uid}\"}},\"q\"]" \
      | jq -r '.methodResponses[0][1].ids | length' 2>/dev/null)
    [[ "${n:-0}" -ge 1 ]] && { found=1; break; }
    sleep 2
  done
  check "invitation for ${uid} delivered to bob's Inbox (JMAP)" test "$found" = 1
fi

log "Nightware API (profile 'api')"
if dc ps --status running --services 2>/dev/null | grep -qx nightware-api; then
  check "GET ${GRAPH_URL}/healthz" ccurl -f -o /dev/null "${GRAPH_URL}/healthz"
  code=$(ccurl -o /dev/null -w '%{http_code}' -H "Authorization: Bearer ${ALICE}" "${GRAPH_URL}/v1.0/me")
  check "GET /v1.0/me with alice's token (${code})" test "$code" = 200
else
  skip "nightware-api not running (docker compose -p nightware --profile api up -d)"
fi

echo
if [[ $fails -eq 0 ]]; then log "smoke: all checks passed"; else log "smoke: ${fails} check(s) failed"; fi
exit "$fails"
