#!/usr/bin/env bash
# Print an access token from the dev stack's Keycloak.
#
#   ./get-token.sh [alice|bob]                 password grant (nightware-dev client)
#   ./get-token.sh --pkce [alice|bob]          real NightMail flow: nightmail public
#                                              client, auth code + PKCE S256,
#                                              loopback redirect; login form is
#                                              driven headlessly with curl
#   ./get-token.sh --exchange stalwart|nextcloud [alice|bob]
#                                              user token exchanged by nightware-api
#                                              (RFC 8693) for that audience
#   ./get-token.sh --service                   nightware-api service account
#                                              (client credentials, kc-admin-api)
#   add --claims to print the decoded payload instead of the token
source "$(dirname "$0")/scripts/lib.sh"
[[ -s "$CA_FILE" ]] || export_ca

mode=password audience="" claims=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --pkce) mode=pkce ;;
    --exchange) mode=exchange; audience="$2"; shift ;;
    --service) mode=service ;;
    --claims) claims=1 ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) user="$1" ;;
  esac
  shift
done
user="${user:-alice}"

pkce_token() {
  local verifier challenge state redirect jar page action location code port=49152
  verifier=$(openssl rand -base64 48 | tr -d '=+/\n' | cut -c1-64)
  challenge=$(printf '%s' "$verifier" | openssl dgst -sha256 -binary | openssl base64 | tr '+/' '-_' | tr -d '=\n')
  state=$(openssl rand -hex 12)
  redirect="http://127.0.0.1:${port}/callback"   # any port: loopback redirect
  jar=$(mktemp); trap 'rm -f "$jar"' RETURN
  page=$(ccurl -c "$jar" -b "$jar" -G "${ISSUER}/protocol/openid-connect/auth" \
    -d client_id=nightmail -d response_type=code --data-urlencode "scope=openid email profile offline_access" \
    --data-urlencode "redirect_uri=${redirect}" -d "state=${state}" \
    -d "code_challenge=${challenge}" -d code_challenge_method=S256)
  action=$(grep -o 'action="[^"]*"' <<<"$page" | head -n1 | sed -e 's/^action="//' -e 's/"$//' -e 's/&amp;/\&/g')
  [[ -n "$action" ]] || { echo "no login form (redirect URI rejected?)" >&2; return 1; }
  location=$(ccurl -c "$jar" -b "$jar" -o /dev/null -w '%{redirect_url}' "$action" \
    --data-urlencode "username=${user}" --data-urlencode "password=${TEST_USER_PASSWORD}" -d credentialId=)
  [[ "$location" == "${redirect}"* ]] || { echo "login did not redirect to ${redirect}: ${location}" >&2; return 1; }
  [[ "$location" == *"state=${state}"* ]] || { echo "state mismatch" >&2; return 1; }
  code=$(sed -E 's/.*[?&]code=([^&]+).*/\1/' <<<"$location")
  ccurl "$TOKEN_URL" -d grant_type=authorization_code -d client_id=nightmail \
    --data-urlencode "code=${code}" --data-urlencode "redirect_uri=${redirect}" \
    --data-urlencode "code_verifier=${verifier}" | jq -er .access_token
}

case "$mode" in
  password) tok=$(user_token "$user") ;;
  pkce) tok=$(pkce_token) ;;
  exchange) tok=$(exchange_token "$(user_token "$user")" "$audience") ;;
  service) tok=$(service_token) ;;
esac
if [[ $claims == 1 ]]; then jwt_claims "$tok" | jq .; else echo "$tok"; fi
