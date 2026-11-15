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
# .env holds the root secret (and every backend password): owner-only, also for
# one created before this script existed (`cp .env.example .env` gave 0644).
chmod 600 .env

# An empty value: nothing, whitespace, or an empty quoted string.
empty='^NIGHTWARE_DELTA_LINK_KEY=[[:space:]]*(""|'"''"')?[[:space:]]*$'
if current=$(grep '^NIGHTWARE_DELTA_LINK_KEY=' .env | grep -Ev "$empty" | tail -n1); then
  # Set: never replaced, but refused when weak. The façade itself only checks in
  # Production (SigningKeyValidator), and compose defaults to Development.
  value=${current#NIGHTWARE_DELTA_LINK_KEY=}
  value=${value//\"/}; value=${value//\'/}
  case "$value" in
    dev-only-delta-link-signing-key | development-only-delta-link-signing-key-change-me)
      echo "init-env: NIGHTWARE_DELTA_LINK_KEY in .env is a published development value; replace it with \`openssl rand -hex 32\` (this invalidates clients' delta links)" >&2
      exit 1 ;;
  esac
  if (( ${#value} < 32 )); then
    echo "init-env: NIGHTWARE_DELTA_LINK_KEY in .env is shorter than 32 bytes; replace it with \`openssl rand -hex 32\` (this invalidates clients' delta links)" >&2
    exit 1
  fi
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
