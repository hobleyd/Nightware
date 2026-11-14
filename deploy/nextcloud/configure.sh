#!/usr/bin/env bash
# Configure Nextcloud with occ (idempotent):
#   - apps: calendar, contacts, tasks, calendar_resource_management, user_oidc
#   - user_oidc provider "keycloak" with bearer-token validation, so
#     CalDAV/CardDAV/WebDAV/OCS accept Keycloak access tokens whose aud
#     contains "nextcloud" (the provider's client id)
#   - outgoing mail -> Stalwart SMTP (iMIP invitations)
#   - a building, a floor and two rooms via calendar_resource_management
source "$(dirname "$0")/../scripts/lib.sh"

log "Nextcloud: waiting for installation"
wait_for "nextcloud installed" bash -c "docker compose -p nightware exec -T -u www-data nextcloud php occ status --output=json | grep -q '\"installed\":true'"

# --------------------------------------------------------------------- apps
for app in calendar contacts tasks user_oidc calendar_resource_management; do
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
occ config:system:set overwritehost --value="cloud.${BASE_DOMAIN}${PUBLIC_PORT_SUFFIX}" >/dev/null
occ config:system:set overwrite.cli.url --value="${CLOUD_URL}" >/dev/null
occ config:system:set default_phone_region --value=AU >/dev/null
occ config:system:set maintenance_window_start --value=1 --type=integer >/dev/null
occ background:cron >/dev/null
# Trust Caddy's local CA for Nextcloud's HTTP client (anything fetched via the
# public names). Keycloak itself is reached on the backchannel, see below.
dc exec -T -u www-data nextcloud sh -c 'cat > /tmp/nightware-caddy-root.crt' < "$CA_FILE"
grep -q nightware-caddy-root.crt <<<"$(occ security:certificates)" || occ security:certificates:import /tmp/nightware-caddy-root.crt >/dev/null
# Validate bearer tokens locally against Keycloak's JWKS (default), keep the
# audience check on (aud must contain the provider client id "nextcloud").
occ config:system:set user_oidc selfencoded_bearer_validation --value=true --type=boolean >/dev/null
occ config:system:set user_oidc selfencoded_bearer_validation_audience_check --value=true --type=boolean >/dev/null
occ config:system:set user_oidc userinfo_bearer_validation --value=false --type=boolean >/dev/null
# Allow the normal Nextcloud login form too (admin), not only "Log in with Keycloak".
occ config:app:set user_oidc allow_multiple_user_backends --value=1 >/dev/null

# --------------------------------------------------------- user_oidc provider
# Discovery goes straight to keycloak:8080. With KC_HOSTNAME_BACKCHANNEL_DYNAMIC
# Keycloak still reports issuer=${ISSUER} (matches token `iss`) but serves
# jwks/token/userinfo on http://keycloak:8080. Going through Caddy does not
# work from this container: PHP's libcurl resolves any *.localhost name to
# 127.0.0.1 itself (RFC 6761) and never asks Docker's DNS.
log "Nextcloud: user_oidc provider 'keycloak' -> ${ISSUER} (via http://keycloak:8080)"
occ user_oidc:provider keycloak \
  --clientid=nextcloud \
  --clientsecret="${NEXTCLOUD_OIDC_CLIENT_SECRET}" \
  --discoveryuri="http://keycloak:8080/realms/nightware/.well-known/openid-configuration" \
  --scope="openid email profile" \
  --unique-uid=0 \
  --mapping-uid=preferred_username \
  --mapping-email=email \
  --mapping-display-name=name \
  --check-bearer=1 \
  --bearer-provisioning=1 >/dev/null

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
occ config:system:set mail_domain --value="${BASE_DOMAIN}" >/dev/null
# Stalwart offers STARTTLS with a self-signed certificate.
occ config:system:set mail_smtpstreamoptions ssl verify_peer --value=false --type=boolean >/dev/null
occ config:system:set mail_smtpstreamoptions ssl verify_peer_name --value=false --type=boolean >/dev/null
occ config:system:set mail_smtpstreamoptions ssl allow_self_signed --value=true --type=boolean >/dev/null
# Send invitations for internal attendees too? Nextcloud delivers iTIP to its
# own users internally; mail is only sent to attendees without a NC account.

# --------------------------------------------------------------------- rooms
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
    "boardroom@${BASE_DOMAIN}" "Meeting room" --capacity=12 --room-number=1.01 \
    --has-video-conferencing=1 --has-tv=1 --has-whiteboard=1 --wheelchair-accessible=1 >/dev/null
  occ calendar-resource:room:create "$STORY_ID" nightware-focus-1 "Focus Room 1" \
    "focus1@${BASE_DOMAIN}" "Phone booth" --capacity=2 --room-number=1.02 --has-phone=1 >/dev/null
fi
# The DAV app copies rooms into its principal table from a background job that
# normally runs every 5-60 minutes; run it now so the room principals exist.
ROOM_JOB=$(occ background-job:list --limit=500 | awk -F'|' '/UpdateCalendarResourcesRoomsBackgroundJob/ {gsub(/ /,"",$2); print $2}' | head -n1)
[[ -n "$ROOM_JOB" ]] && occ background-job:execute "$ROOM_JOB" --force-execute >/dev/null
occ calendar-resource:resources:list | sed -n '/^Rooms:/,/^Resources:/p'
log "Nextcloud: configured"
