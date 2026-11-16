#!/usr/bin/env bash
# Copy a TLS certificate chain and its private key from another host over ssh
# into deploy/.certs, for CADDY_TLS="/certs/fullchain.crt /certs/privkey.key":
#
#   deploy/scripts/fetch-cert.sh          (or: make cert)
#
# Where from, in deploy/.env (no defaults: they name your own machines):
#   CERT_SOURCE_HOST   ssh destination, e.g. certs.example.com or user@host
#   CERT_SOURCE_CERT   path of the full chain (leaf first) on that host
#   CERT_SOURCE_KEY    path of the private key on that host
#
# The key is read with `cat` when the ssh user may; a root-only key is read by
# a throwaway container instead (the ssh user needs docker there):
#   docker run --rm --network none -v <key dir>:/src:ro alpine:latest cat /src/<key>
# The alpine image is removed again only when this script pulled it.
#
# Both files are checked before anything is replaced: the key must match the
# certificate, the certificate must cover PUBLIC_HOST and stay valid for 7
# more days. They land in deploy/.certs/fullchain.crt and privkey.key (mode
# 600), and a running Caddy reloads them. Run it again after the source host
# renews its certificate (Let's Encrypt: every 60-90 days); see
# docs/operations/runbook.md "TLS certificate renewal".
source "$(dirname "$0")/lib.sh"

if [[ -z "${CERT_SOURCE_HOST:-}" || -z "${CERT_SOURCE_CERT:-}" || -z "${CERT_SOURCE_KEY:-}" ]]; then
  sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//' >&2
  echo >&2
  echo "fetch-cert: set CERT_SOURCE_HOST, CERT_SOURCE_CERT and CERT_SOURCE_KEY in deploy/.env" >&2
  exit 2
fi

CERT_DIR="${DEPLOY_DIR}/.certs"
mkdir -p "$CERT_DIR"
umask 077
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

log "Fetching ${CERT_SOURCE_CERT} from ${CERT_SOURCE_HOST}"
ssh "$CERT_SOURCE_HOST" cat "$CERT_SOURCE_CERT" > "$tmp/fullchain.crt"

log "Fetching ${CERT_SOURCE_KEY} from ${CERT_SOURCE_HOST}"
# shellcheck disable=SC2029 # the paths are meant to expand here, on this side
if ! ssh "$CERT_SOURCE_HOST" cat "$CERT_SOURCE_KEY" > "$tmp/privkey.key" 2>/dev/null || [[ ! -s "$tmp/privkey.key" ]]; then
  log "Key not readable by the ssh user; reading it with a throwaway container"
  key_dir=$(dirname "$CERT_SOURCE_KEY") key_name=$(basename "$CERT_SOURCE_KEY")
  had_alpine=0
  ssh "$CERT_SOURCE_HOST" docker image inspect alpine:latest >/dev/null 2>&1 && had_alpine=1
  ssh "$CERT_SOURCE_HOST" docker run --rm --network none -v "${key_dir}:/src:ro" alpine:latest cat "/src/${key_name}" > "$tmp/privkey.key"
  if (( ! had_alpine )); then
    ssh "$CERT_SOURCE_HOST" docker image rm alpine:latest >/dev/null 2>&1 || true
  fi
fi

# ------------------------------------------------------------------ checks
fail() { echo "fetch-cert: $*; nothing replaced" >&2; exit 1; }
openssl x509 -in "$tmp/fullchain.crt" -noout 2>/dev/null || fail "${CERT_SOURCE_CERT} is not a PEM certificate"
openssl pkey -in "$tmp/privkey.key" -noout 2>/dev/null || fail "${CERT_SOURCE_KEY} is not a PEM private key"
# Public keys, not -modulus: that is RSA-only, and Let's Encrypt keys are often EC.
[[ "$(openssl x509 -in "$tmp/fullchain.crt" -noout -pubkey)" == "$(openssl pkey -in "$tmp/privkey.key" -pubout)" ]] \
  || fail "the key does not belong to the certificate"
openssl x509 -in "$tmp/fullchain.crt" -noout -checkend 604800 >/dev/null \
  || fail "the certificate expires within 7 days ($(openssl x509 -in "$tmp/fullchain.crt" -noout -enddate))"
openssl x509 -in "$tmp/fullchain.crt" -noout -checkhost "$PUBLIC_HOST" 2>/dev/null | grep -q 'does match' \
  || fail "the certificate does not cover PUBLIC_HOST=${PUBLIC_HOST}"

chmod 600 "$tmp/fullchain.crt" "$tmp/privkey.key"
mv -f "$tmp/fullchain.crt" "$CERT_DIR/fullchain.crt"
mv -f "$tmp/privkey.key" "$CERT_DIR/privkey.key"
log "Installed .certs/fullchain.crt and .certs/privkey.key ($(openssl x509 -in "$CERT_DIR/fullchain.crt" -noout -subject -enddate | tr '\n' ' '))"

if [[ "$CADDY_TLS" != "/certs/fullchain.crt /certs/privkey.key" ]]; then
  log "CADDY_TLS is '${CADDY_TLS}': set CADDY_TLS=\"/certs/fullchain.crt /certs/privkey.key\" in .env to serve these files"
fi

# --force: the Caddyfile itself is unchanged, and an unchanged config is not
# reloaded (nor are its certificate files re-read) without it.
if dc ps --status running --services 2>/dev/null | grep -qx caddy; then
  dc exec -T caddy caddy reload --config /etc/caddy/Caddyfile --force
  log "Caddy reloaded"
else
  log "Caddy is not running; it loads the files when it starts"
fi
