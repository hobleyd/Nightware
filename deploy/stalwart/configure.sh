#!/usr/bin/env bash
# Configure Stalwart v1.x through its management API (JMAP `x:` methods).
#
# Stalwart >= 1.0 keeps no TOML config: /etc/stalwart/config.json holds only the
# data-store location, and every other setting is an object in the data store
# ("registry"), managed over JMAP. This script is idempotent:
#
#   1. bootstrap mode (fresh volume) -> submit x:Bootstrap, restart container
#   2. pre-create the test accounts (an OIDC directory cannot look up
#      recipients, so mail to a never-signed-in user would bounce)
#   3. create/update the Keycloak OIDC directory, make it the default directory
#   4. ReloadSettings
#
# Authenticates as the recovery administrator (STALWART_RECOVERY_ADMIN).
source "$(dirname "$0")/../scripts/lib.sh"

USING='"using":["urn:ietf:params:jmap:core","urn:stalwart:jmap"]'
call() { stalwart_api "{${USING},\"methodCalls\":$1}"; }

log "Stalwart: waiting for HTTP listener"
wait_for "stalwart" ccurl -f "${MAIL_URL}/healthz/live"

# ---------------------------------------------------------------- 1. bootstrap
if call '[["x:Bootstrap/get",{"ids":["singleton"]},"c"]]' | jq -e '.methodResponses[0][0]=="x:Bootstrap/get"' >/dev/null \
   && ! call '[["x:Domain/query",{},"c"]]' | jq -e '.methodResponses[0][0]=="x:Domain/query"' >/dev/null; then
  log "Stalwart: bootstrap mode -> submitting Bootstrap object"
  call "$(jq -cn --arg host "mail.${BASE_DOMAIN}" --arg dom "${BASE_DOMAIN}" '[["x:Bootstrap/set",{"update":{"singleton":{
      defaultHostname:$host, defaultDomain:$dom,
      requestTlsCertificate:false, generateDkimKeys:true,
      tracer:{"@type":"Stdout",level:"info",ansi:false}}}},"c"]]')" \
    | jq -e '.methodResponses[0][1].updated.singleton' >/dev/null
  dc restart stalwart >/dev/null
  sleep 3
  wait_for "stalwart restart" ccurl -f "${MAIL_URL}/healthz/live"
  for _ in $(seq 1 60); do
    call '[["x:Domain/query",{},"c"]]' 2>/dev/null | grep -q '"x:Domain/query"' && break
    sleep 2
  done
fi

DOMAIN_ID=$(call "$(jq -cn --arg d "$BASE_DOMAIN" '[["x:Domain/query",{"filter":{"name":$d}},"c"]]')" | jq -er '.methodResponses[0][1].ids[0]')
log "Stalwart: domain ${BASE_DOMAIN} id=${DOMAIN_ID}"

# ------------------------------------------------------- 2. test user accounts
existing=$(call '[["x:Account/query",{},"q"],["x:Account/get",{"#ids":{"resultOf":"q","name":"x:Account/query","path":"/ids"},"properties":["name","emailAddress"]},"g"]]' \
  | jq -r '.methodResponses[1][1].list[].emailAddress')
for u in alice bob; do
  if grep -qx "${u}@${BASE_DOMAIN}" <<<"$existing"; then
    log "Stalwart: account ${u}@${BASE_DOMAIN} exists"
  else
    log "Stalwart: creating account ${u}@${BASE_DOMAIN}"
    call "$(jq -cn --arg n "$u" --arg d "$DOMAIN_ID" '[["x:Account/set",{"create":{"u":{"@type":"User",name:$n,domainId:$d,description:($n|ascii_downcase)}}},"c"]]')" \
      | jq -e '.methodResponses[0][1].created.u.id' >/dev/null \
      || { echo "account create failed" >&2; exit 1; }
  fi
done

# ------------------------------------------------------- 3. Keycloak directory
DIR_BODY=$(jq -cn --arg iss "$ISSUER" '{
  "@type":"Oidc",
  description:"Keycloak (nightware realm)",
  issuerUrl:$iss,
  claimUsername:"email",
  claimName:"name",
  acceptedAudiences:{"stalwart":true},
  unscopedTokens:"interoperable",
  allowInvalidCerts:true
}')
DIR_ID=$(call '[["x:Directory/query",{},"c"]]' | jq -r '.methodResponses[0][1].ids[0] // empty')
if [[ -z "$DIR_ID" ]]; then
  log "Stalwart: creating OIDC directory -> ${ISSUER}"
  DIR_ID=$(call "[[\"x:Directory/set\",{\"create\":{\"d\":${DIR_BODY}}},\"c\"]]" | tee /dev/stderr | jq -er '.methodResponses[0][1].created.d.id')
else
  log "Stalwart: updating OIDC directory ${DIR_ID}"
  call "[[\"x:Directory/set\",{\"update\":{\"${DIR_ID}\":$(jq -c 'del(."@type")' <<<"$DIR_BODY")}},\"c\"]]" \
    | jq -e ".methodResponses[0][1].updated | has(\"${DIR_ID}\")" >/dev/null
fi
call "[[\"x:Authentication/set\",{\"update\":{\"singleton\":{\"directoryId\":\"${DIR_ID}\"}}},\"c\"]]" \
  | jq -e '.methodResponses[0][1].updated' >/dev/null

# ------------------------------------------------------------------ 4. reload
call '[["x:Action/set",{"create":{"r":{"@type":"ReloadSettings"}}},"c"]]' \
  | jq -e '.methodResponses[0][1].created' >/dev/null
log "Stalwart: configured (directory ${DIR_ID} is the default directory)"
