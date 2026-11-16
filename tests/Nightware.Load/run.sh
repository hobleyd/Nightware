#!/usr/bin/env bash
# Run a k6 script from this folder against the deploy/ stack, from inside its Docker network
# (so PUBLIC_HOST resolves to Caddy, and with CADDY_TLS=internal its local CA is trusted).
#   tests/Nightware.Load/run.sh poll-cycle.js      VUS=50 DURATION=10m POLL_INTERVAL=30 ...
#   tests/Nightware.Load/run.sh harness.js
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
deploy="$here/../../deploy"
script="${1:-poll-cycle.js}"
[[ -f "$deploy/.env" ]] || { echo "deploy/.env is required (run deploy/bootstrap.sh)" >&2; exit 1; }
set -a; source "$deploy/.env"; set +a
host="${PUBLIC_HOST:-${BASE_DOMAIN:-nightware.localhost}}"
public="https://${host}${PUBLIC_PORT_SUFFIX:-}"
trust=()
if [[ "${CADDY_TLS:-internal}" == internal ]]; then
  [[ -s "$deploy/.certs/caddy-root.crt" ]] || { echo "deploy/.certs/caddy-root.crt is required (run deploy/bootstrap.sh)" >&2; exit 1; }
  trust=(-v "$(cd "$deploy/.certs" && pwd)/caddy-root.crt:/certs/caddy-root.crt:ro" -e SSL_CERT_FILE=/certs/caddy-root.crt)
fi
mkdir -p "$here/results"
exec docker run --rm -i \
  --network "${COMPOSE_PROJECT_NAME:-nightware}_nightware" \
  -v "$here:/scripts:ro" -v "$here/results:/results" \
  ${trust[@]+"${trust[@]}"} \
  -e GRAPH_ORIGIN="${public}/graph" \
  -e GRAPH_BASE="${public}/graph/v1.0" \
  -e TOKEN_URL="${public}/auth/realms/nightware/protocol/openid-connect/token" \
  -e TEST_USER_PASSWORD -e NIGHTWARE_DEV_CLIENT_SECRET \
  -e VUS -e DURATION -e POLL_INTERVAL -e LOAD_USERS -e ALLOW_501 \
  "${K6_IMAGE:-grafana/k6:1.4.2}" run --summary-export "/results/${script%.js}-summary.json" "/scripts/$script"
