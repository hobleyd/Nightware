#!/usr/bin/env bash
# Post-import Keycloak configuration (idempotent):
#   - declare the Graph mailboxSettings attributes in the realm's user profile
#     (Keycloak 26 drops undeclared "unmanaged" attributes): zoneinfo, locale,
#     nightware_working_hours (JSON string), nightware_date_format,
#     nightware_time_format. Viewable by users, editable only by admins / the
#     nightware-api service account.
#   - lock the identity attributes: `email` (the Stalwart account and the
#     façade's /me mailbox) and `username` are editable by admins only, so a user
#     cannot claim another mailbox's address in the account console; usernames
#     are not editable and the UPDATE_EMAIL required action stays disabled.
#   - seed them for the test users, writing as the nightware-api service
#     account (realm-management manage-users) to prove that path
#     (NIGHTWARE_KC_SEED=0 skips this).
# Every step reads the current state and writes only what differs (the user
# profile is merged, never replaced wholesale).
source "$(dirname "$0")/../scripts/lib.sh"

log "Keycloak: waiting for realm"
wait_for "keycloak realm" ccurl -f "${ISSUER}/.well-known/openid-configuration"

ADMIN=$(kc_admin_token)
API="${AUTH_URL}/admin/realms/nightware"

# Graph mailboxSettings attributes. Admin-editable (the nightware-api service
# account acts with realm-management manage-users), user-viewable, not
# user-editable. `locale` is Keycloak's built-in attribute (internationalization
# is enabled by the realm import); it is only declared here if missing.
ATTRS='[
  {"name":"zoneinfo","displayName":"Time zone (IANA)","validations":{"length":{"max":64}}},
  {"name":"locale","displayName":"Locale","validations":{"length":{"max":35}}},
  {"name":"nightware_working_hours","displayName":"Working hours (Graph workingHours JSON)","validations":{"length":{"max":4096}}},
  {"name":"nightware_date_format","displayName":"Date format (Graph dateFormat)","validations":{"length":{"max":64}}},
  {"name":"nightware_time_format","displayName":"Time format (Graph timeFormat)","validations":{"length":{"max":64}}}
]'
profile=$(ccurl -f -H "Authorization: Bearer ${ADMIN}" "${API}/users/profile")
updated=$(jq --argjson want "$ATTRS" -f "$DEPLOY_DIR/keycloak/user-profile.jq" <<<"$profile")
if [[ "$(jq -S . <<<"$profile")" == "$(jq -S . <<<"$updated")" ]]; then
  log "Keycloak: user profile already declares the mailboxSettings attributes and locks email/username"
else
  log "Keycloak: declaring zoneinfo, locale, nightware_working_hours, nightware_date_format, nightware_time_format; email/username admin-edit only"
  ccurl -f -X PUT -H "Authorization: Bearer ${ADMIN}" -H 'Content-Type: application/json' \
    "${API}/users/profile" --data-binary @- <<<"$updated" >/dev/null
fi

# Identity is the email claim (Stalwart's claimUsername, the façade's /me), so no
# self-service path may change it. Usernames: realm flag (also in the import).
realm=$(ccurl -f -H "Authorization: Bearer ${ADMIN}" "${API}")
if [[ "$(jq -r .editUsernameAllowed <<<"$realm")" != "false" ]]; then
  log "Keycloak: editUsernameAllowed -> false"
  ccurl -f -X PUT -H "Authorization: Bearer ${ADMIN}" -H 'Content-Type: application/json' \
    "${API}" -d '{"editUsernameAllowed":false}' >/dev/null
fi
# The update-email feature is on by default in Keycloak 26; its required action is
# a self-service email change (kc_action=UPDATE_EMAIL). Keep it disabled.
action=$(ccurl -f -H "Authorization: Bearer ${ADMIN}" "${API}/authentication/required-actions/UPDATE_EMAIL" || true)
if [[ -n "$action" && "$(jq -r .enabled <<<"$action")" != "false" ]]; then
  log "Keycloak: disabling the UPDATE_EMAIL required action"
  jq '.enabled = false | .defaultAction = false' <<<"$action" \
    | ccurl -f -X PUT -H "Authorization: Bearer ${ADMIN}" -H 'Content-Type: application/json' \
        "${API}/authentication/required-actions/UPDATE_EMAIL" --data-binary @- >/dev/null
fi

# NIGHTWARE_KC_SEED=0 applies only the realm/user-profile steps above, leaving
# the test users' attributes as they are (re-running on a live shared stack).
if [[ "${NIGHTWARE_KC_SEED:-1}" == "0" ]]; then
  log "Keycloak: NIGHTWARE_KC_SEED=0, not seeding the test users"
  exit 0
fi

SVC=$(service_token)
seed() {
  local user="$1" tz="$2" locale="$3" wh="$4" rep
  rep=$(ccurl -H "Authorization: Bearer ${SVC}" "${API}/users?username=${user}&exact=true" | jq '.[0]')
  local id; id=$(jq -r .id <<<"$rep")
  jq --arg tz "$tz" --arg l "$locale" '.attributes = ((.attributes // {}) + {zoneinfo: [$tz], locale: [$l],
      nightware_working_hours: [$wh], nightware_date_format: ["dd/MM/yyyy"], nightware_time_format: ["h:mm tt"]})' --arg wh "$wh" <<<"$rep" \
    | ccurl -f -X PUT -H "Authorization: Bearer ${SVC}" -H 'Content-Type: application/json' \
        "${API}/users/${id}" --data-binary @- >/dev/null
  log "Keycloak: ${user} zoneinfo=${tz} locale=${locale} (written by service-account-nightware-api)"
}
WH_SYD='{"daysOfWeek":["monday","tuesday","wednesday","thursday","friday"],"startTime":"09:00:00.0000000","endTime":"17:30:00.0000000","timeZone":{"name":"Australia/Sydney"}}'
WH_LON='{"daysOfWeek":["monday","tuesday","wednesday","thursday","friday"],"startTime":"08:30:00.0000000","endTime":"17:00:00.0000000","timeZone":{"name":"Europe/London"}}'
seed alice "Australia/Sydney" "en" "$WH_SYD"
seed bob "Europe/London" "en" "$WH_LON"
