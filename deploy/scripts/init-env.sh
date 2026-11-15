#!/usr/bin/env bash
# Makes sure deploy/.env exists and holds the façade's root secret (idempotent):
#   - creates .env from .env.example when it is missing;
#   - generates NIGHTWARE_DELTA_LINK_KEY when the line is absent or empty.
# A non-empty key is never touched: replacing it invalidates every client's
# delta links and every in-flight upload/download URL (docs/operations/deployment.md).
# docker-compose.yml has no default for the key, so every compose command needs
# this to have run first; bootstrap.sh, `make stack` and `make api` call it.
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ ! -f .env ]]; then
  (umask 077; cp .env.example .env)
  echo "init-env: created .env from .env.example"
fi

# An empty value: nothing, whitespace, or an empty quoted string.
empty='^NIGHTWARE_DELTA_LINK_KEY=[[:space:]]*(""|'"''"')?[[:space:]]*$'
if grep '^NIGHTWARE_DELTA_LINK_KEY=' .env | grep -Evq "$empty"; then
  exit 0
fi

key=$(openssl rand -hex 32)
tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
# Fill the (first) empty placeholder line in place (.env.example has one) and
# drop any other empty ones, which would override it; else append.
awk -v line="NIGHTWARE_DELTA_LINK_KEY=${key}" -v empty="$empty" '
  $0 ~ empty { if (!done) print line; done = 1; next }
  { print }
  END { if (!done) print line }' .env > "$tmp"
cat "$tmp" > .env
echo "init-env: generated NIGHTWARE_DELTA_LINK_KEY in .env"
