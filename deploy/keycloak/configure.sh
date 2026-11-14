#!/usr/bin/env bash
# Post-import Keycloak configuration (idempotent):
#   - declare the Graph mailboxSettings attributes in the realm's user profile
#     (Keycloak 26 drops undeclared "unmanaged" attributes): zoneinfo, locale,
#     nightware_working_hours (JSON string), nightware_date_format,
#     nightware_time_format. Viewable by users, editable only by admins / the
#     nightware-api service account.
#   - seed them for the test users, writing as the nightware-api service
#     account (realm-management manage-users) to prove that path.
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
profile=$(ccurl -H "Authorization: Bearer ${ADMIN}" "${API}/users/profile")
updated=$(jq --argjson want "$ATTRS" '
  .attributes as $have
  | .attributes = ($have | map(
      if (.name as $n | $want | map(.name) | index($n)) and .name != "locale"
      then .permissions = {view:["admin","user"], edit:["admin"]} | .multivalued = false
      else . end))
  + [ $want[] | select(.name as $n | ($have | map(.name) | index($n)) | not)
      | . + {permissions:{view:["admin","user"], edit:["admin"]}, multivalued:false} ]' <<<"$profile")
if [[ "$(jq -S . <<<"$profile")" == "$(jq -S . <<<"$updated")" ]]; then
  log "Keycloak: user profile already declares the mailboxSettings attributes"
else
  log "Keycloak: declaring zoneinfo, locale, nightware_working_hours, nightware_date_format, nightware_time_format"
  ccurl -f -X PUT -H "Authorization: Bearer ${ADMIN}" -H 'Content-Type: application/json' \
    "${API}/users/profile" --data-binary @- <<<"$updated" >/dev/null
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
