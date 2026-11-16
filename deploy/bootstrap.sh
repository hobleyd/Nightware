#!/usr/bin/env bash
# One-time (idempotent) configuration of a running stack:
#   docker compose up -d --wait && ./bootstrap.sh
# Safe to re-run; each step converges.
source "$(dirname "$0")/scripts/lib.sh"

# lib.sh has already run scripts/init-env.sh: .env exists and holds a
# NIGHTWARE_DELTA_LINK_KEY (generated on first run, never replaced).

internal_tls && log "Exporting Caddy root CA -> .certs/caddy-root.crt"
export_ca

"$DEPLOY_DIR/keycloak/configure.sh"
"$DEPLOY_DIR/stalwart/configure.sh"
"$DEPLOY_DIR/nextcloud/configure.sh"

# Sign each test user in once on both backends. Stalwart creates/links the
# account on first token use, and Nextcloud's user_oidc provisions the user
# (uid = preferred_username) on its first bearer request.
for u in alice bob; do
  t=$(exchange_token "$(user_token "$u")" stalwart)
  ccurl -f -L -o /dev/null -H "Authorization: Bearer $t" "${MAIL_URL}/.well-known/jmap"
  t=$(exchange_token "$(user_token "$u")" nextcloud)
  ccurl -f -o /dev/null -X PROPFIND -H 'Depth: 0' -H "Authorization: Bearer $t" "${CLOUD_URL}/remote.php/dav/principals/users/${u}/"
  log "provisioned ${u} on Stalwart and Nextcloud"
done

log "Bootstrap complete. Run ./smoke.sh"
