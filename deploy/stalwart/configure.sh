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
#   4. trusted compose network, inbound throttle scope, JMAP concurrent
#      requests per account (default 32)
#   5. ReloadSettings
#
# Authenticates as the recovery administrator (STALWART_RECOVERY_ADMIN).
source "$(dirname "$0")/../scripts/lib.sh"

USING='"using":["urn:ietf:params:jmap:core","urn:stalwart:jmap"]'
call() { stalwart_api "{${USING},\"methodCalls\":$1}"; }

log "Stalwart: waiting for HTTP listener"
wait_for "stalwart" ccurl -f "${MAIL_ALIAS_URL}/healthz/live"

# ---------------------------------------------------------------- 1. bootstrap
if call '[["x:Bootstrap/get",{"ids":["singleton"]},"c"]]' | jq -e '.methodResponses[0][0]=="x:Bootstrap/get"' >/dev/null \
   && ! call '[["x:Domain/query",{},"c"]]' | jq -e '.methodResponses[0][0]=="x:Domain/query"' >/dev/null; then
  log "Stalwart: bootstrap mode -> submitting Bootstrap object"
  call "$(jq -cn --arg host "mail.${MAIL_DOMAIN}" --arg dom "${MAIL_DOMAIN}" '[["x:Bootstrap/set",{"update":{"singleton":{
      defaultHostname:$host, defaultDomain:$dom,
      requestTlsCertificate:false, generateDkimKeys:true,
      tracer:{"@type":"Stdout",level:"info",ansi:false}}}},"c"]]')" \
    | jq -e '.methodResponses[0][1].updated.singleton' >/dev/null
  dc restart stalwart >/dev/null
  sleep 3
  wait_for "stalwart restart" ccurl -f "${MAIL_ALIAS_URL}/healthz/live"
  for _ in $(seq 1 60); do
    call '[["x:Domain/query",{},"c"]]' 2>/dev/null | grep -q '"x:Domain/query"' && break
    sleep 2
  done
fi

DOMAIN_ID=$(call "$(jq -cn --arg d "$MAIL_DOMAIN" '[["x:Domain/query",{"filter":{"name":$d}},"c"]]')" | jq -er '.methodResponses[0][1].ids[0]')
log "Stalwart: domain ${MAIL_DOMAIN} id=${DOMAIN_ID}"

# ------------------------------------------------------- 2. test user accounts
existing=$(call '[["x:Account/query",{},"q"],["x:Account/get",{"#ids":{"resultOf":"q","name":"x:Account/query","path":"/ids"},"properties":["name","emailAddress"]},"g"]]' \
  | jq -r '.methodResponses[1][1].list[].emailAddress')
for u in alice bob; do
  if grep -qx "${u}@${MAIL_DOMAIN}" <<<"$existing"; then
    log "Stalwart: account ${u}@${MAIL_DOMAIN} exists"
  else
    log "Stalwart: creating account ${u}@${MAIL_DOMAIN}"
    call "$(jq -cn --arg n "$u" --arg d "$DOMAIN_ID" '[["x:Account/set",{"create":{"u":{"@type":"User",name:$n,domainId:$d,description:($n|ascii_downcase)}}},"c"]]')" \
      | jq -e '.methodResponses[0][1].created.u.id' >/dev/null \
      || { echo "account create failed" >&2; exit 1; }
  fi
done

# ------------------------------------------------------- 3. Keycloak directory
# Stalwart fetches discovery and JWKS from ${ISSUER} through Caddy. It cannot
# verify Caddy's local CA, so certificate checks are off unless
# STALWART_ALLOW_INVALID_CERTS=false (a publicly trusted certificate).
DIR_BODY=$(jq -cn --arg iss "$ISSUER" --argjson insecure "${STALWART_ALLOW_INVALID_CERTS:-true}" '{
  "@type":"Oidc",
  description:"Keycloak (nightware realm)",
  issuerUrl:$iss,
  claimUsername:"email",
  claimName:"name",
  acceptedAudiences:{"stalwart":true},
  unscopedTokens:"interoperable",
  allowInvalidCerts:$insecure
}')
DIR_ID=$(call '[["x:Directory/query",{},"c"]]' | jq -r '.methodResponses[0][1].ids[0] // empty')
if [[ -z "$DIR_ID" ]]; then
  log "Stalwart: creating OIDC directory -> ${ISSUER}"
  DIR_ID=$(call "[[\"x:Directory/set\",{\"create\":{\"d\":${DIR_BODY}}},\"c\"]]" | jq -er '.methodResponses[0][1].created.d.id')
else
  log "Stalwart: updating OIDC directory ${DIR_ID}"
  call "[[\"x:Directory/set\",{\"update\":{\"${DIR_ID}\":$(jq -c 'del(."@type")' <<<"$DIR_BODY")}},\"c\"]]" \
    | jq -e ".methodResponses[0][1].updated | has(\"${DIR_ID}\")" >/dev/null
fi
call "[[\"x:Authentication/set\",{\"update\":{\"singleton\":{\"directoryId\":\"${DIR_ID}\"}}},\"c\"]]" \
  | jq -e '.methodResponses[0][1].updated' >/dev/null

# ------------------------------------------- 4. trusted compose network (iMIP)
# Nextcloud cannot SMTP-AUTH (accounts live in Keycloak, no passwords), so it
# delivers invitations unauthenticated to port 25 like any inbound MX. Mail to
# local recipients is accepted that way, but the spam filter would file it as
# Junk (no SPF/DKIM for *.localhost). Skip the spam filter for the stack's own
# Docker subnet; everything else keeps the default rule.
SUBNET="${DOCKER_SUBNET:-172.31.250.0/24}"
call "$(jq -cn --arg net "$SUBNET" '[["x:MtaStageData/set",{"update":{"singleton":{
  enableSpamFilter:{"else":("is_empty(authenticated_as) && !is_ip_in_cidr(remote_ip, \u0027" + $net + "\u0027)")}}}},"c"]]')" \
  | jq -e '.methodResponses[0][1].updated' >/dev/null

# ------------------------------------------------- 4b. inbound throttle scope
# Stalwart's default "sender domain to recipient" throttle allows 25 messages an
# hour per (sender domain, recipient). In a single-domain organisation that caps
# *all colleagues together* at 25 messages an hour to any one person. Authenticated
# submissions are already accountable, so the throttle applies to unauthenticated
# inbound mail only (which still covers Nextcloud's iMIP and external senders).
THROTTLE_ID=$(call '[["x:MtaInboundThrottle/query",{},"q"],["x:MtaInboundThrottle/get",{"#ids":{"resultOf":"q","name":"x:MtaInboundThrottle/query","path":"/ids"},"properties":["key"]},"g"]]' \
  | jq -r '.methodResponses[1][1].list[] | select(.key.rcpt == true and .key.senderDomain == true) | .id' | head -n1)
if [[ -n "$THROTTLE_ID" ]]; then
  log "Stalwart: scoping inbound throttle ${THROTTLE_ID} to unauthenticated senders"
  call "$(jq -cn --arg id "$THROTTLE_ID" '[["x:MtaInboundThrottle/set",{"update":{($id):{
      description:"Sender domain to recipient throttle (unauthenticated inbound only)",
      match:{match:{},else:"is_empty(authenticated_as)"}}}},"c"]]')" \
    | jq -e ".methodResponses[0][1].updated | has(\"${THROTTLE_ID}\")" >/dev/null
fi

# ------------------------------------------ 4c. concurrent JMAP requests/account
# Stalwart allows 4 JMAP requests in flight per account by default. Every one of a
# user's devices reaches it as that user through the façade, so a few devices
# polling together (or one device prefetching bodies) got JMAP `limit` problems,
# answered as 429. The façade already bounds a user to 32 requests in flight
# (RateLimiting:ConcurrentRequestLimit), so Stalwart matches it and the
# façade's limit is the one that applies. ReloadSettings does not apply JMAP
# limits to a running server (verified: the old limit held until a restart), so a
# change here restarts Stalwart after the reload below.
RESTART_STALWART=0
JMAP_CONCURRENCY="${STALWART_JMAP_MAX_CONCURRENT_REQUESTS:-32}"
current=$(call '[["x:Jmap/get",{"ids":["singleton"],"properties":["maxConcurrentRequests"]},"g"]]' \
  | jq -r '.methodResponses[0][1].list[0].maxConcurrentRequests')
if [[ "$current" != "$JMAP_CONCURRENCY" ]]; then
  log "Stalwart: JMAP maxConcurrentRequests ${current} -> ${JMAP_CONCURRENCY} per account"
  call "$(jq -cn --argjson n "$JMAP_CONCURRENCY" '[["x:Jmap/set",{"update":{"singleton":{maxConcurrentRequests:$n}}},"c"]]')" \
    | jq -e '.methodResponses[0][1].updated | has("singleton")' >/dev/null
  RESTART_STALWART=1
fi

# ------------------------------------------------------------------ 5. reload
call '[["x:Action/set",{"create":{"r":{"@type":"ReloadSettings"}}},"c"]]' \
  | jq -e '.methodResponses[0][1].created' >/dev/null
if (( RESTART_STALWART )); then
  log "Stalwart: restarting to apply the JMAP request limits"
  dc restart stalwart >/dev/null
  wait_for "stalwart restart" ccurl -f "${MAIL_ALIAS_URL}/healthz/live"
fi
log "Stalwart: configured (directory ${DIR_ID} is the default directory)"
