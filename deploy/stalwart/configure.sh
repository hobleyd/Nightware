#!/usr/bin/env bash
# Configure Stalwart v1.x through its management API (JMAP `x:` methods).
#
# Stalwart >= 1.0 keeps no TOML config: /etc/stalwart/config.json holds only the
# data-store location, and every other setting is an object in the data store
# ("registry"), managed over JMAP. This script is idempotent:
#
#   1. bootstrap mode (fresh volume) -> submit x:Bootstrap, restart container
#   2. pre-create the mail accounts (an OIDC directory cannot look up
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

# ------------------------------------------------------- 2. user accounts
# STALWART_ACCOUNTS: the local parts to create (default the test users alice
# and bob). A deployment with real users lists theirs instead; each must match
# the email claim of the Keycloak user who signs in as it.
existing=$(call '[["x:Account/query",{},"q"],["x:Account/get",{"#ids":{"resultOf":"q","name":"x:Account/query","path":"/ids"},"properties":["name","emailAddress"]},"g"]]' \
  | jq -r '.methodResponses[1][1].list[].emailAddress')
for u in ${STALWART_ACCOUNTS:-alice bob}; do
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

# ------------------------------------------------ 4d. admin console principal
# The admin console (ADR 0002 D8) manages accounts and domains as its own
# internal account, never as the recovery admin. Verified on Stalwart 1.1.0:
#   - an internal account cannot hold a password while Keycloak is the default
#     directory ("Cannot set credentials for accounts in an external
#     directory"), but the recovery admin can mint it an API key
#     (x:ApiKey/set with the account's accountId); Stalwart returns the secret
#     once, and it authenticates as `Authorization: Bearer API_...`;
#   - roles are extended through roleIds, and the built-in role "User" holds
#     `authenticate` plus the user permissions Stalwart requires a principal to
#     hold before it may change user accounts.
# Our role adds the management permissions (destroy only for "delete mail",
# which the console's policy gates); no impersonation, no domain deletion.
# The key is written to .env as STALWART_MANAGEMENT_API_KEY and re-minted when
# it no longer authenticates. STALWART_MANAGEMENT_LOGIN (the recovery-admin
# fallback) skips all of this.
api_key_works() { # the key goes through stdin, not the command line (ps)
  [[ -n "$1" ]] && printf 'Authorization: Bearer %s\n' "$1" | ccurl -f -H @- -H 'Content-Type: application/json' "${MAIL_URL}/jmap/" \
    -d "{${USING},\"methodCalls\":[[\"x:Account/query\",{},\"q\"]]}" 2>/dev/null | jq -e '.methodResponses[0][0]=="x:Account/query"' >/dev/null
}
env_put() { # KEY VALUE: replace KEY= in deploy/.env, or append it (value via the environment, not argv)
  local tmp; tmp=$(mktemp)
  ENV_PUT_KEY="$1" ENV_PUT_LINE="$1=$2" awk 'BEGIN{k=ENVIRON["ENV_PUT_KEY"]; line=ENVIRON["ENV_PUT_LINE"]; done=0}
    $0 ~ "^"k"=" {if(!done) print line; done=1; next} {print} END{if(!done) print line}' "$DEPLOY_DIR/.env" > "$tmp"
  cat "$tmp" > "$DEPLOY_DIR/.env"; rm -f "$tmp"
}
if [[ -z "${STALWART_MANAGEMENT_LOGIN:-}" ]]; then
  MGMT_USER="${STALWART_MANAGEMENT_USER:-nightware-admin}"
  role_id() { call '[["x:Role/query",{},"q"],["x:Role/get",{"#ids":{"resultOf":"q","name":"x:Role/query","path":"/ids"},"properties":["description"]},"g"]]' \
    | jq -r --arg d "$1" '.methodResponses[1][1].list[]? | select(.description==$d) | .id' | head -n1; }
  PERMS='{"sysAccountGet":true,"sysAccountQuery":true,"sysAccountCreate":true,"sysAccountUpdate":true,"sysAccountDestroy":true,"sysDomainGet":true,"sysDomainQuery":true,"sysDomainCreate":true,"sysDomainUpdate":true,"sysDkimSignatureGet":true,"sysDkimSignatureQuery":true,"sysActionCreate":true}'
  USER_ROLE=$(role_id "User")
  ROLE_ID=$(role_id "Nightware admin console")
  if [[ -z "$ROLE_ID" ]]; then
    log "Stalwart: creating role 'Nightware admin console'"
    ROLE_ID=$(call "$(jq -cn --argjson p "$PERMS" '[["x:Role/set",{"create":{"r":{description:"Nightware admin console",enabledPermissions:$p,disabledPermissions:{},roleIds:{}}}},"c"]]')" \
      | jq -r '.methodResponses[0][1].created.r.id // empty')
  else
    call "$(jq -cn --arg id "$ROLE_ID" --argjson p "$PERMS" '[["x:Role/set",{"update":{($id):{enabledPermissions:$p}}},"c"]]')" >/dev/null
  fi
  if [[ -z "$ROLE_ID" || -z "$USER_ROLE" ]]; then
    echo "Stalwart: WARNING could not find or create the admin console role (User role: '${USER_ROLE}')" >&2
  else
    ROLES=$(jq -cn --arg r "$ROLE_ID" --arg u "$USER_ROLE" '{"@type":"Custom",roleIds:{($r):true,($u):true}}')
    MGMT_ID=$(call "$(jq -cn --arg n "$MGMT_USER" --arg d "$DOMAIN_ID" '[["x:Account/query",{"filter":{"name":$n,"domainId":$d}},"q"]]')" | jq -r '.methodResponses[0][1].ids[0] // empty')
    if [[ -z "$MGMT_ID" ]]; then
      log "Stalwart: creating the admin console account ${MGMT_USER}@${MAIL_DOMAIN}"
      MGMT_ID=$(call "$(jq -cn --arg n "$MGMT_USER" --arg d "$DOMAIN_ID" --argjson r "$ROLES" '[["x:Account/set",{"create":{"m":{"@type":"User",name:$n,domainId:$d,description:"Nightware admin console (not a person)",roles:$r}}},"c"]]')" \
        | jq -r '.methodResponses[0][1].created.m.id // empty')
    else
      call "$(jq -cn --arg id "$MGMT_ID" --argjson r "$ROLES" '[["x:Account/set",{"update":{($id):{roles:$r}}},"c"]]')" >/dev/null
    fi
    if [[ -z "$MGMT_ID" ]]; then
      echo "Stalwart: WARNING could not create the admin console account" >&2
    elif api_key_works "${STALWART_MANAGEMENT_API_KEY:-}"; then
      log "Stalwart: admin console API key in .env still works"
    else
      log "Stalwart: minting an API key for ${MGMT_USER}@${MAIL_DOMAIN} -> .env STALWART_MANAGEMENT_API_KEY"
      # Usable only from the console's container (it reaches Stalwart directly on the stack network).
      ADMIN_IP="${NIGHTWARE_ADMIN_ADDRESS:-172.31.250.249}/32"
      KEY=$(call "$(jq -cn --arg a "$MGMT_ID" --arg ip "$ADMIN_IP" '[["x:ApiKey/set",{"accountId":$a,"create":{"k":{description:"Nightware admin console",permissions:{"@type":"Inherit"},allowedIps:{($ip):true}}}},"c"]]')" \
        | jq -r '.methodResponses[0][1].created.k.secret // empty')
      if [[ -n "$KEY" ]]; then
        env_put STALWART_MANAGEMENT_API_KEY "$KEY"
        STALWART_MANAGEMENT_API_KEY="$KEY"
      else
        echo "Stalwart: WARNING could not mint an API key for the admin console" >&2
      fi
    fi
  fi
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
# The console's principal must be able to list accounts. Reported, not fatal:
# the rest of the stack works without it.
if [[ -z "${STALWART_MANAGEMENT_LOGIN:-}" ]]; then
  if api_key_works "${STALWART_MANAGEMENT_API_KEY:-}"; then
    log "Stalwart: the admin console's API key can manage accounts (restart nightware-admin to pick up a new key)"
  else
    echo "Stalwart: WARNING the admin console's API key does not work; see deploy/README.md 'Admin console'" >&2
  fi
fi
