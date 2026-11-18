#!/usr/bin/env bash
# Makes sure deploy/.env exists and holds every secret (idempotent):
#   - creates .env from .env.example when it is missing;
#   - fills each secret whose line is absent or empty with a random value: the
#     façade's root secret NIGHTWARE_DELTA_LINK_KEY, the admin and database
#     passwords, the Keycloak client secrets, the admin console's own
#     backend credentials and Grafana's;
#   - adds PUBLIC_HOST and MAIL_DOMAIN from a pre-single-host BASE_DOMAIN.
# A non-empty value is never replaced. A secret still holding a value this
# repository once published only gets a warning: the backends stored it when
# they were bootstrapped (Postgres, the imported realm), so changing it here
# alone would lock the stack out. For a stack that may be reached from a
# network, start from fresh volumes with a fresh .env instead.
# NIGHTWARE_DELTA_LINK_KEY is refused when weak (see below); replacing it
# invalidates every client's delta links and in-flight upload/download URLs
# (docs/operations/deployment.md).
# docker-compose.yml has no default for the secrets, so every compose command
# needs this to have run first; bootstrap.sh, `make stack` and `make api` call it.
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ ! -f .env ]]; then
  (umask 077; cp .env.example .env)
  echo "init-env: created .env from .env.example"
fi
# .env holds the root secret (and every backend password): owner-only, also for
# one created before this script existed (`cp .env.example .env` gave 0644).
chmod 600 .env

# value_of KEY: the last non-empty value of KEY (quotes stripped), or nothing.
value_of() {
  local line
  line=$(grep -E "^$1=" .env | tail -n1) || return 0
  line=${line#"$1"=}
  line=${line//\"/}; line=${line//\'/}
  printf '%s' "$line"
}

# put KEY VALUE: fill the (first) empty KEY= line in place (.env.example has
# one) and drop any other empty ones, which would override it; else append.
put() {
  local key="$1" value="$2" tmp
  local empty="^${key}="'[[:space:]]*(""|'"''"')?[[:space:]]*$'
  tmp=$(mktemp)
  awk -v line="${key}=${value}" -v empty="$empty" '
    $0 ~ empty { if (!done) print line; done = 1; next }
    { print }
    END { if (!done) print line }' .env > "$tmp"
  cat "$tmp" > .env
  rm -f "$tmp"
}

# --------------------------------------------------------- host and domain
# BASE_DOMAIN named five hosts (auth./mail./cloud./office./graph.) and the mail
# domain. Its successors default to it, so an old .env keeps its addresses.
base=$(value_of BASE_DOMAIN)
if [[ -n "$base" ]]; then
  for key in PUBLIC_HOST MAIL_DOMAIN; do
    if ! grep -qE "^${key}=" .env; then
      put "$key" "$base"
      echo "init-env: ${key}=${base} (from the deprecated BASE_DOMAIN; the stack is now one host with path prefixes)" >&2
    fi
  done
fi

# ---------------------------------------------------------------- secrets
# Hex only: `$` would be interpolated by compose, `"` and `\` break the realm
# JSON import, `:` breaks STALWART_RECOVERY_ADMIN (user:password) and curl -u.
# The values .env.example carried before it left them empty (bash 3.2: no
# associative arrays on macOS).
published() {
  case "$1" in
    KEYCLOAK_ADMIN_PASSWORD) echo nightware-kc-admin ;;
    KEYCLOAK_DB_PASSWORD) echo nightware-kc-db ;;
    NIGHTWARE_API_CLIENT_SECRET) echo nightware-api-dev-secret ;;
    NIGHTWARE_DEV_CLIENT_SECRET) echo nightware-dev-secret ;;
    NEXTCLOUD_OIDC_CLIENT_SECRET) echo nextcloud-oidc-dev-secret ;;
    STALWART_ADMIN_PASSWORD) echo nightware-stalwart-admin ;;
    NEXTCLOUD_ADMIN_PASSWORD) echo nightware-nc-admin ;;
    NEXTCLOUD_DB_PASSWORD) echo nightware-nc-db ;;
  esac
}
for key in KEYCLOAK_ADMIN_PASSWORD KEYCLOAK_DB_PASSWORD NIGHTWARE_API_CLIENT_SECRET NIGHTWARE_DEV_CLIENT_SECRET \
           NEXTCLOUD_OIDC_CLIENT_SECRET STALWART_ADMIN_PASSWORD NEXTCLOUD_ADMIN_PASSWORD NEXTCLOUD_DB_PASSWORD \
           NIGHTWARE_ADMIN_CLIENT_SECRET NEXTCLOUD_PROVISIONING_PASSWORD NIGHTWARE_GRAFANA_CLIENT_SECRET GRAFANA_ADMIN_PASSWORD; do
  value=$(value_of "$key")
  if [[ -z "$value" ]]; then
    put "$key" "$(openssl rand -hex 24)"
    echo "init-env: generated ${key} in .env"
  elif [[ "$value" == "$(published "$key")" ]]; then
    echo "init-env: ${key} in .env is a published development value; fine on loopback, but anything reachable from a network needs fresh volumes and a fresh .env (see deploy/README.md)" >&2
  fi
done

# ------------------------------------------------- NIGHTWARE_DELTA_LINK_KEY
value=$(value_of NIGHTWARE_DELTA_LINK_KEY)
if [[ -n "$value" ]]; then
  # Set: never replaced, but refused when weak. The façade itself only checks in
  # Production (SigningKeyValidator), and compose defaults to Development.
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

put NIGHTWARE_DELTA_LINK_KEY "$(openssl rand -hex 32)"
echo "init-env: generated NIGHTWARE_DELTA_LINK_KEY in .env"
