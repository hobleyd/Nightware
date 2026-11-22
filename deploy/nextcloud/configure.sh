#!/usr/bin/env bash
# Configure Nextcloud with occ (idempotent):
#   - apps: calendar, contacts, tasks, calendar_resource_management, user_oidc,
#     richdocuments (Nextcloud Office, on the collabora service), groupfolders
#     (Team folders: the admin console makes one per group that asks for it),
#     spreed (Talk: the room behind an event's isOnlineMeeting, phase 22)
#   - Talk's signaling server and TURN/STUN: the talk service
#   - user_oidc provider "keycloak" with bearer-token validation, so
#     CalDAV/CardDAV/WebDAV/OCS accept Keycloak access tokens whose aud
#     contains "nextcloud" (the provider's client id)
#   - outgoing mail -> Stalwart SMTP (iMIP invitations)
#   - a building, a floor and two rooms via calendar_resource_management
#     (demo data, like the test users: NIGHTWARE_KC_SEED=0 skips them)
source "$(dirname "$0")/../scripts/lib.sh"

log "Nextcloud: waiting for installation"
wait_for "nextcloud installed" bash -c "docker compose -p '${COMPOSE_PROJECT_NAME}' exec -T -u www-data nextcloud php occ status --output=json | grep -q '\"installed\":true'"

# --------------------------------------------------------------------- apps
# activity: the façade's drive delta reads its stream (ADR 0008 D5); a default install has it, but make sure.
for app in calendar contacts tasks user_oidc calendar_resource_management richdocuments groupfolders activity spreed; do
  if occ app:list --output=json | jq -e --arg a "$app" '.enabled | has($a)' >/dev/null; then
    log "Nextcloud: app ${app} enabled"
  else
    log "Nextcloud: installing ${app}"
    occ app:install "$app" >/dev/null 2>&1 || occ app:enable "$app"
  fi
done

# ------------------------------------------------------------ system config
log "Nextcloud: system config"
occ config:system:set allow_local_remote_servers --value=true --type=boolean >/dev/null
occ config:system:set overwriteprotocol --value=https >/dev/null
occ config:system:set overwritehost --value="${PUBLIC_HOST}${PUBLIC_PORT_SUFFIX}" >/dev/null
occ config:system:set overwritewebroot --value=/cloud >/dev/null
occ config:system:set overwrite.cli.url --value="${CLOUD_URL}" >/dev/null
# The overwrites above (host, protocol, /cloud webroot) apply only to requests
# from Caddy's pinned address. The façade calls http://nextcloud/ directly, and
# its DAV paths expect hrefs under /remote.php/dav/, not /cloud/remote.php/dav/.
# (APACHE_DISABLE_REWRITE_IP keeps REMOTE_ADDR the proxy's own address.)
CADDY_ADDRESS="${CADDY_ADDRESS:-172.31.250.253}"
occ config:system:set overwritecondaddr --value="^${CADDY_ADDRESS//./\\.}\$" >/dev/null
occ config:system:set default_phone_region --value=AU >/dev/null
occ config:system:set maintenance_window_start --value=1 --type=integer >/dev/null
# Deleted files go 30 days after deletion, sooner if space runs short. The
# default (auto) never expires them by age for a user without a quota.
occ config:system:set trashbin_retention_obligation --value="auto, 30" >/dev/null
occ background:cron >/dev/null
# Trust Caddy's local CA for Nextcloud's HTTP client (anything fetched via the
# public URL). Keycloak itself is reached on the backchannel, see below. A
# publicly trusted certificate (CADDY_TLS not internal) needs nothing here.
if internal_tls; then
  dc exec -T -u www-data nextcloud sh -c 'cat > /tmp/nightware-caddy-root.crt' < "$CA_FILE"
  grep -q nightware-caddy-root.crt <<<"$(occ security:certificates)" || occ security:certificates:import /tmp/nightware-caddy-root.crt >/dev/null
fi
# Validate bearer tokens locally against Keycloak's JWKS (default), keep the
# audience check on (aud must contain the provider client id "nextcloud").
occ config:system:set user_oidc selfencoded_bearer_validation --value=true --type=boolean >/dev/null
occ config:system:set user_oidc selfencoded_bearer_validation_audience_check --value=true --type=boolean >/dev/null
occ config:system:set user_oidc userinfo_bearer_validation --value=false --type=boolean >/dev/null
# Allow the normal Nextcloud login form too (admin), not only "Log in with Keycloak".
occ config:app:set user_oidc allow_multiple_user_backends --value=1 >/dev/null

# --------------------------------------------------------- user_oidc provider
# Discovery goes straight to keycloak:8080/auth. With KC_HOSTNAME_BACKCHANNEL_DYNAMIC
# Keycloak still reports issuer=${ISSUER} (matches token `iss`) but serves
# jwks/token/userinfo on http://keycloak:8080/auth. Going through Caddy does not
# work from this container with a *.localhost PUBLIC_HOST: PHP's libcurl
# resolves any *.localhost name to 127.0.0.1 itself (RFC 6761) and never asks
# Docker's DNS.
log "Nextcloud: user_oidc provider 'keycloak' -> ${ISSUER} (via http://keycloak:8080/auth)"
occ user_oidc:provider keycloak \
  --clientid=nextcloud \
  --clientsecret="${NEXTCLOUD_OIDC_CLIENT_SECRET}" \
  --discoveryuri="http://keycloak:8080/auth/realms/nightware/.well-known/openid-configuration" \
  --scope="openid email profile" \
  --unique-uid=0 \
  --mapping-uid=preferred_username \
  --mapping-email=email \
  --mapping-display-name=name \
  --check-bearer=1 \
  --bearer-provisioning=1 >/dev/null

# --------------------------------------------------------- Nextcloud Office
# richdocuments edits documents in the browser through Collabora (the
# collabora service). Three URLs, because *.localhost names do not work
# between containers (PHP's libcurl resolves them to its own loopback):
#   - Nextcloud fetches Collabora's discovery on the internal network, under
#     Collabora's service root (net.service_root=/office),
#   - the browser loads the editor from ${PUBLIC_URL}/office
#     (Collabora's server_name + service root, so discovery advertises it),
#   - Collabora calls Nextcloud's WOPI endpoints back on http://nextcloud,
#     the one host in its WOPI alias group (aliasgroup1).
# Nextcloud accepts WOPI requests only from the compose subnet, which is where
# Collabora's callbacks come from.
log "Nextcloud: richdocuments -> collabora:9980/office (browser: ${PUBLIC_URL}/office)"
wait_for "collabora discovery" dc exec -T nextcloud curl -fsS -o /dev/null http://collabora:9980/office/hosting/discovery
occ richdocuments:setup --wopi-url=http://collabora:9980/office --callback-url=http://nextcloud >/dev/null
occ config:app:set richdocuments wopi_allowlist --value="${DOCKER_SUBNET:-172.31.250.0/24}" >/dev/null

# --------------------------------------------------------------------- Talk
# Online meetings (phase 22): the talk service is Talk's signaling server
# (High-Performance Backend, behind Caddy at /standalone-signaling/) and its
# TURN/STUN server on ${TALK_TURN_HOST:-$PUBLIC_HOST}:${TALK_PORT}. Converged on every run: the
# lists are replaced, since Talk never shows a stored secret to compare. The
# default public STUN server (stun.nextcloud.com) goes: clients learn their
# address from ours instead of telling a third party about every call.
TALK_PORT="${TALK_PORT:-3478}"
SIGNALING_URL="${PUBLIC_URL}/standalone-signaling/"
TURN_HOST="${TALK_TURN_HOST:-$PUBLIC_HOST}:${TALK_PORT}"
log "Nextcloud: Talk -> signaling ${SIGNALING_URL}, TURN/STUN ${TURN_HOST}"
# Each occ inside a loop reads /dev/null: occ (docker compose exec) would
# otherwise swallow the rest of the list from the loop's stdin.
occ talk:signaling:list --output=json | jq -r '.servers?[]?.server // empty' | while IFS= read -r server; do
  occ talk:signaling:delete "$server" </dev/null >/dev/null
done
occ talk:signaling:add "$SIGNALING_URL" "$TALK_SIGNALING_SECRET" --verify >/dev/null
occ talk:turn:list --output=json | jq -r '.[]? | "\(.schemes)\t\(.server)\t\(.protocols)"' | while IFS=$'\t' read -r schemes server protocols; do
  occ talk:turn:delete "$schemes" "$server" "$protocols" </dev/null >/dev/null
done
occ talk:turn:add turn "$TURN_HOST" udp,tcp --secret="$TALK_TURN_SECRET" >/dev/null
# Ours first: Talk puts its default back whenever the STUN list is empty.
occ talk:stun:list --output=json | jq -e --arg s "$TURN_HOST" 'index($s)' >/dev/null || occ talk:stun:add "$TURN_HOST" >/dev/null
occ talk:stun:list --output=json | jq -r --arg s "$TURN_HOST" '.[]? | select(. != $s)' | while IFS= read -r stun; do
  occ talk:stun:delete "$stun" </dev/null >/dev/null
done

# ------------------------------------------------------ brute-force allow-list
# The façade reads public shares anonymously (/shares in Nightware), so every
# reader's lookups arrive from its one address and Nextcloud would count a few
# dead or password-protected links from anyone against all of them
# (MaxDelayReached). Exempt only the api container's pinned address
# (NIGHTWARE_API_ADDRESS in docker-compose.yml), never the whole subnet:
# Caddy is on it too, and it forwards every browser's sign-in attempts. The
# fixed id keeps this idempotent and out of the way of entries added in the
# admin UI (bruteforcesettings numbers those from 1).
API_ADDRESS="${NIGHTWARE_API_ADDRESS:-172.31.250.250}"
log "Nextcloud: brute-force allow-list ${API_ADDRESS}/32 (nightware-api)"
occ config:app:set bruteForce whitelist_1000 --value="${API_ADDRESS}/32" >/dev/null
occ config:app:set bruteForce whitelist_1000_comment --value="nightware-api (deploy/nextcloud/configure.sh)" >/dev/null

# ------------------------------------------------- admin console credential
# The admin console (ADR 0002 D8) provisions users through the OCS API and
# user_oidc's pre-provisioning endpoint as its own local administrator, never
# as NEXTCLOUD_ADMIN_USER. Nextcloud has no narrower role that may create users.
# The password is set on every run so .env stays the source of truth.
if [[ -n "${NEXTCLOUD_PROVISIONING_PASSWORD:-}" ]]; then
  PROV_USER="${NEXTCLOUD_PROVISIONING_USER:-nightware-admin}"
  if occ user:info "$PROV_USER" >/dev/null 2>&1; then
    OC_PASS="$NEXTCLOUD_PROVISIONING_PASSWORD" dc exec -T -u www-data -e OC_PASS nextcloud php occ user:resetpassword --password-from-env "$PROV_USER" >/dev/null
  else
    log "Nextcloud: creating the admin console account ${PROV_USER}"
    OC_PASS="$NEXTCLOUD_PROVISIONING_PASSWORD" dc exec -T -u www-data -e OC_PASS nextcloud php occ user:add --password-from-env \
      --display-name "Nightware admin console" --group admin "$PROV_USER" >/dev/null
  fi
  # The console's address, so brute-force protection never throttles provisioning.
  ADMIN_ADDRESS="${NIGHTWARE_ADMIN_ADDRESS:-172.31.250.249}"
  occ config:app:set bruteForce whitelist_1001 --value="${ADMIN_ADDRESS}/32" >/dev/null
  occ config:app:set bruteForce whitelist_1001_comment --value="nightware-admin (deploy/nextcloud/configure.sh)" >/dev/null
fi

# ------------------------------------------------------------- outgoing mail
# iMIP invitations go to Stalwart's SMTP listener (port 25) inside the Docker
# network. Stalwart accepts unauthenticated mail for its own local recipients
# (normal inbound MX behaviour); see README "iMIP".
log "Nextcloud: mail -> stalwart:25"
occ config:system:set mail_smtpmode --value=smtp >/dev/null
occ config:system:set mail_smtphost --value=stalwart >/dev/null
occ config:system:set mail_smtpport --value=25 --type=integer >/dev/null
occ config:system:set mail_smtpsecure --value='' >/dev/null
occ config:system:set mail_smtpauth --value=false --type=boolean >/dev/null
occ config:system:set mail_from_address --value=calendar >/dev/null
occ config:system:set mail_domain --value="${MAIL_DOMAIN}" >/dev/null
# Stalwart offers STARTTLS with a self-signed certificate.
occ config:system:set mail_smtpstreamoptions ssl verify_peer --value=false --type=boolean >/dev/null
occ config:system:set mail_smtpstreamoptions ssl verify_peer_name --value=false --type=boolean >/dev/null
occ config:system:set mail_smtpstreamoptions ssl allow_self_signed --value=true --type=boolean >/dev/null
# Send invitations for internal attendees too? Nextcloud delivers iTIP to its
# own users internally; mail is only sent to attendees without a NC account.

# --------------------------------------------------------------------- rooms
if [[ "${NIGHTWARE_KC_SEED:-1}" == "0" ]]; then
  log "Nextcloud: NIGHTWARE_KC_SEED=0, not seeding the demo rooms"
  log "Nextcloud: configured"
  exit 0
fi
if grep -q 'nightware-boardroom' <<<"$(occ calendar-resource:resources:list)"; then
  log "Nextcloud: rooms exist"
else
  log "Nextcloud: creating building / floor / rooms"
  # "create" prints "Created new <Type> with ID:\n<id>"; reuse a partial run's rows.
  row_id() { occ calendar-resource:resources:list | awk -F'|' -v n="$1" '{c=$3; gsub(/^ +| +$/,"",c); d=$4; gsub(/^ +| +$/,"",d)} !f && (c==n||d==n) {gsub(/ /,"",$2); print $2; f=1}'; }
  BUILDING_ID=$(row_id "Nightware HQ")
  [[ -n "$BUILDING_ID" ]] || BUILDING_ID=$(occ calendar-resource:building:create "Nightware HQ" \
    --address="1 Example Street, Sydney NSW 2000" --wheelchair-accessible=1 | tail -1 | tr -d '[:space:]')
  STORY_ID=$(row_id "Level 1")
  [[ -n "$STORY_ID" ]] || STORY_ID=$(occ calendar-resource:story:create "$BUILDING_ID" "Level 1" | tail -1 | tr -d '[:space:]')
  occ calendar-resource:room:create "$STORY_ID" nightware-boardroom "Boardroom" \
    "boardroom@${MAIL_DOMAIN}" "Meeting room" --capacity=12 --room-number=1.01 \
    --has-video-conferencing=1 --has-tv=1 --has-whiteboard=1 --wheelchair-accessible=1 >/dev/null
  occ calendar-resource:room:create "$STORY_ID" nightware-focus-1 "Focus Room 1" \
    "focus1@${MAIL_DOMAIN}" "Phone booth" --capacity=2 --room-number=1.02 --has-phone=1 >/dev/null
fi
# The DAV app copies rooms into its principal table from a background job that
# normally runs every 5-60 minutes; run it now so the room principals exist.
ROOM_JOB=$(occ background-job:list --limit=500 | awk -F'|' '/UpdateCalendarResourcesRoomsBackgroundJob/ {gsub(/ /,"",$2); print $2}' | head -n1)
[[ -n "$ROOM_JOB" ]] && occ background-job:execute "$ROOM_JOB" --force-execute >/dev/null
occ calendar-resource:resources:list | sed -n '/^Rooms:/,/^Resources:/p'
log "Nextcloud: configured"
