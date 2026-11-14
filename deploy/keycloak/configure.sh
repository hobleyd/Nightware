#!/usr/bin/env bash
# Post-import Keycloak configuration (idempotent):
#   - declare `zoneinfo` in the realm's user profile. Keycloak 26 drops
#     undeclared ("unmanaged") attributes, and the façade writes zoneinfo and
#     locale (mailboxSettings.timeZone / language) through the admin API.
#     `locale` is built in once internationalization is enabled (realm import).
#   - seed zoneinfo/locale for the test users, writing as the nightware-api
#     service account (realm-management manage-users) to prove that path.
source "$(dirname "$0")/../scripts/lib.sh"

log "Keycloak: waiting for realm"
wait_for "keycloak realm" ccurl -f "${ISSUER}/.well-known/openid-configuration"

ADMIN=$(kc_admin_token)
API="${AUTH_URL}/admin/realms/nightware"

profile=$(ccurl -H "Authorization: Bearer ${ADMIN}" "${API}/users/profile")
if jq -e '.attributes[] | select(.name=="zoneinfo")' >/dev/null <<<"$profile"; then
  log "Keycloak: user profile already declares zoneinfo"
else
  log "Keycloak: adding zoneinfo to the user profile"
  jq '.attributes += [{
        name: "zoneinfo", displayName: "Time zone (IANA)",
        permissions: {view: ["admin","user"], edit: ["admin","user"]},
        multivalued: false, validations: {length: {max: 64}}
      }]' <<<"$profile" \
    | ccurl -f -X PUT -H "Authorization: Bearer ${ADMIN}" -H 'Content-Type: application/json' \
        "${API}/users/profile" --data-binary @- >/dev/null
fi

SVC=$(service_token)
seed() {
  local user="$1" tz="$2" locale="$3" rep
  rep=$(ccurl -H "Authorization: Bearer ${SVC}" "${API}/users?username=${user}&exact=true" | jq '.[0]')
  local id; id=$(jq -r .id <<<"$rep")
  jq --arg tz "$tz" --arg l "$locale" '.attributes = ((.attributes // {}) + {zoneinfo: [$tz], locale: [$l]})' <<<"$rep" \
    | ccurl -f -X PUT -H "Authorization: Bearer ${SVC}" -H 'Content-Type: application/json' \
        "${API}/users/${id}" --data-binary @- >/dev/null
  log "Keycloak: ${user} zoneinfo=${tz} locale=${locale} (written by service-account-nightware-api)"
}
seed alice "Australia/Sydney" "en"
seed bob "Europe/London" "en"
