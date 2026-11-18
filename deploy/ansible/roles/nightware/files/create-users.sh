#!/usr/bin/env bash
# Creates the people in NIGHTWARE_USERS (a JSON list of {username, email,
# first_name, last_name}) that are missing from the nightware realm, with
# NIGHTWARE_USER_INITIAL_PASSWORD as a temporary password. Existing users are
# left alone. Uses the checkout's scripts/lib.sh (NIGHTWARE_DIR), whose curl
# reaches Caddy on NIGHTWARE_CONNECT_ADDRESS under the public name (Caddy
# needs SNI). Prints "created <username>" per user it creates.
source "${NIGHTWARE_DIR}/deploy/scripts/lib.sh"

ADMIN=$(kc_admin_token)
API="${AUTH_URL}/admin/realms/nightware"
export NIGHTWARE_USER_INITIAL_PASSWORD  # read by jq from the environment, not its command line

jq -c '.[]' <<<"${NIGHTWARE_USERS}" | while read -r user; do
  username=$(jq -r .username <<<"$user")
  existing=$(ccurl -f -H "Authorization: Bearer ${ADMIN}" "${API}/users?exact=true&username=$(jq -rn --arg u "$username" '$u|@uri')")
  [[ "$(jq length <<<"$existing")" == 0 ]] || continue
  jq -c '{username, email, emailVerified: true, firstName: .first_name, lastName: .last_name, enabled: true,
          credentials: [{type: "password", value: $ENV.NIGHTWARE_USER_INITIAL_PASSWORD, temporary: true}]}' <<<"$user" \
    | ccurl -f -H "Authorization: Bearer ${ADMIN}" -H 'Content-Type: application/json' -X POST "${API}/users" --data-binary @- >/dev/null
  echo "created ${username}"
done
