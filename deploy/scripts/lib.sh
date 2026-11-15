# shellcheck shell=bash
# Shared helpers for bootstrap.sh / smoke.sh / get-token.sh. Source, don't run.
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$DEPLOY_DIR"

# .env (created from .env.example if missing) must hold NIGHTWARE_DELTA_LINK_KEY
# before any compose command: docker-compose.yml has no default for it. Notices go
# to stderr so get-token.sh's stdout stays a bare token.
"$DEPLOY_DIR/scripts/init-env.sh" >&2
set -a; source ./.env; set +a

: "${BASE_DOMAIN:=nightware.localhost}"
: "${PUBLIC_PORT_SUFFIX:=}"
AUTH_URL="https://auth.${BASE_DOMAIN}${PUBLIC_PORT_SUFFIX}"
MAIL_URL="https://mail.${BASE_DOMAIN}${PUBLIC_PORT_SUFFIX}"
CLOUD_URL="https://cloud.${BASE_DOMAIN}${PUBLIC_PORT_SUFFIX}"
GRAPH_URL="https://graph.${BASE_DOMAIN}${PUBLIC_PORT_SUFFIX}"
ISSUER="${AUTH_URL}/realms/nightware"
TOKEN_URL="${ISSUER}/protocol/openid-connect/token"
CA_FILE="${DEPLOY_DIR}/.certs/caddy-root.crt"

dc() { docker compose -p nightware "$@"; }

# Export Caddy's local root CA so host-side curl can verify the stack's TLS.
export_ca() {
  mkdir -p "${DEPLOY_DIR}/.certs"
  local i
  for i in $(seq 1 30); do
    if dc exec -T caddy cat /data/caddy/pki/authorities/local/root.crt > "$CA_FILE" 2>/dev/null && [[ -s "$CA_FILE" ]]; then
      return 0
    fi
    sleep 2
  done
  echo "could not export Caddy root CA" >&2; return 1
}

ccurl() { curl -sS --cacert "$CA_FILE" "$@"; }

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; }
skip() { printf '  \033[33mSKIP\033[0m %s\n' "$*"; }

# wait_for <description> <command...>: retry a command for up to ~5 minutes.
wait_for() {
  local what="$1"; shift
  local i
  for i in $(seq 1 150); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  echo "timed out waiting for ${what}" >&2; return 1
}

# Password-grant user token via the confidential dev client (aud nightware-api,
# exactly like a NightMail token). Prints the raw access token.
user_token() {
  local user="$1"
  ccurl "$TOKEN_URL" \
    -d grant_type=password \
    -d client_id=nightware-dev \
    --data-urlencode "client_secret=${NIGHTWARE_DEV_CLIENT_SECRET}" \
    --data-urlencode "username=${user}" \
    --data-urlencode "password=${TEST_USER_PASSWORD}" \
    -d scope=openid | jq -er .access_token
}

# RFC 8693 standard token exchange by the façade client: subject token (aud
# nightware-api) -> token whose aud is exactly <audience> (stalwart|nextcloud).
exchange_token() {
  local subject="$1" audience="$2"
  ccurl "$TOKEN_URL" \
    -u "nightware-api:${NIGHTWARE_API_CLIENT_SECRET}" \
    -d grant_type=urn:ietf:params:oauth:grant-type:token-exchange \
    --data-urlencode "subject_token=${subject}" \
    -d subject_token_type=urn:ietf:params:oauth:token-type:access_token \
    -d requested_token_type=urn:ietf:params:oauth:token-type:access_token \
    -d "audience=${audience}" | jq -er .access_token
}

# Client-credentials token for the nightware-api service account. Keycloak
# >= 26.8 only accepts admin-API calls from tokens carrying the `kc-admin-api`
# scope (its mapper adds the admin-API audience), an optional scope here.
service_token() {
  ccurl "$TOKEN_URL" -u "nightware-api:${NIGHTWARE_API_CLIENT_SECRET}" \
    -d grant_type=client_credentials -d scope=kc-admin-api | jq -er .access_token
}

# Keycloak master-realm admin token (bootstrap only).
kc_admin_token() {
  ccurl "${AUTH_URL}/realms/master/protocol/openid-connect/token" \
    -d grant_type=password -d client_id=admin-cli \
    --data-urlencode "username=${KEYCLOAK_ADMIN_USER}" \
    --data-urlencode "password=${KEYCLOAK_ADMIN_PASSWORD}" | jq -er .access_token
}

# Decode a JWT payload.
jwt_claims() {
  python3 -c 'import sys,base64,json; p=sys.argv[1].split(".")[1]; print(json.dumps(json.loads(base64.urlsafe_b64decode(p+"="*(-len(p)%4)))))' "$1"
}

# Stalwart management/JMAP API as the recovery admin (Basic auth).
stalwart_api() {
  ccurl -u "${STALWART_ADMIN_USER}:${STALWART_ADMIN_PASSWORD}" \
    -H 'Content-Type: application/json' "${MAIL_URL}/jmap/" -d "$1"
}

occ() { dc exec -T -u www-data nextcloud php occ "$@"; }
