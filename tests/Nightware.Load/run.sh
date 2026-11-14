#!/usr/bin/env bash
# Run a k6 script from this folder against the deploy/ stack, from inside its Docker network
# (so the public *.nightware.localhost names resolve to Caddy and its local CA is trusted).
#   tests/Nightware.Load/run.sh poll-cycle.js      VUS=50 DURATION=10m POLL_INTERVAL=30 ...
#   tests/Nightware.Load/run.sh harness.js
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
deploy="$here/../../deploy"
script="${1:-poll-cycle.js}"
[[ -f "$deploy/.env" && -s "$deploy/.certs/caddy-root.crt" ]] || { echo "deploy/.env and deploy/.certs/caddy-root.crt are required (run deploy/bootstrap.sh)" >&2; exit 1; }
set -a; source "$deploy/.env"; set +a
domain="${BASE_DOMAIN:-nightware.localhost}"
suffix="${PUBLIC_PORT_SUFFIX:-}"
mkdir -p "$here/results"
exec docker run --rm -i \
  --network nightware_nightware \
  -v "$here:/scripts:ro" -v "$here/results:/results" \
  -v "$(cd "$deploy/.certs" && pwd)/caddy-root.crt:/certs/caddy-root.crt:ro" \
  -e SSL_CERT_FILE=/certs/caddy-root.crt \
  -e GRAPH_ORIGIN="https://graph.${domain}${suffix}" \
  -e GRAPH_BASE="https://graph.${domain}${suffix}/v1.0" \
  -e TOKEN_URL="https://auth.${domain}${suffix}/realms/nightware/protocol/openid-connect/token" \
  -e TEST_USER_PASSWORD -e NIGHTWARE_DEV_CLIENT_SECRET \
  -e VUS -e DURATION -e POLL_INTERVAL -e LOAD_USERS -e ALLOW_501 \
  "${K6_IMAGE:-grafana/k6:1.4.2}" run --summary-export "/results/${script%.js}-summary.json" "/scripts/$script"
