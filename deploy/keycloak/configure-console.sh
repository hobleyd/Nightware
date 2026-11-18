#!/usr/bin/env bash
# Keycloak configuration for the admin console (ADR 0002), idempotent and safe on
# a live realm (nothing here needs a fresh volume or a realm re-import):
#   1. realm: Organizations on (with their identity-first login step off, so
#      NightMail's sign-in page is unchanged), admin events kept,
#      acr.loa.map {pwd:1, mfa:2}, SMTP through Stalwart when the realm has none,
#      passkeys named "Nightware" and their registration action enabled
#   2. realm roles nightware-global-admin and nightware-entity-admin
#   3. user profile: nightware_aliases (multivalued), the two deletion
#      markers and nightware_access_history (multivalued), admin-editable only
#   4. a step-up browser flow (password = level 1, + passkey or OTP = level 2)
#      bound to the console's client only; NightMail's sign-in is not touched.
#      It replaces the OTP-only flow nightware-console-browser, which is deleted
#      once the client is bound to the new one
#   5. client nightware-admin (confidential, code + PKCE, own secret, minimum
#      acr "mfa" unless ADMIN_REQUIRED_ACR is empty) and its service account's
#      realm-management roles
#   6. the default entity: organization ${NIGHTWARE_DEFAULT_ENTITY_NAME} with
#      MAIL_DOMAIN, its users with an @MAIL_DOMAIN address as unmanaged members
#   7. global admins named in ADMIN_BOOTSTRAP_USERS (usernames or addresses;
#      alice on a seeded dev stack)
# The step-4 flow (and its migration from the OTP-only flow) is verified on
# Keycloak 26.8.2: no factor -> OTP set-up; OTP, passkey, or both (passkey first,
# "Try another way" to OTP) -> acr mfa. UNVERIFIED against a live Keycloak: the
# organization realm-management role names in step 5. Each step reports what it
# could not do and carries on; the console fails closed (no MFA acr, no sign-in)
# rather than open.
source "$(dirname "$0")/../scripts/lib.sh"

log "Keycloak (console): waiting for realm"
wait_for "keycloak realm" ccurl -f "${ISSUER}/.well-known/openid-configuration"

ADMIN=$(kc_admin_token)
API="${AUTH_URL}/admin/realms/nightware"
kc()  { ccurl -f -H "Authorization: Bearer ${ADMIN}" "$@"; }
kcj() { ccurl -f -H "Authorization: Bearer ${ADMIN}" -H 'Content-Type: application/json' "$@"; }
warn() { echo "Keycloak (console): WARNING $*" >&2; }

REQUIRED_ACR="${ADMIN_REQUIRED_ACR-mfa}"
CLIENT_ID=nightware-admin
CONSOLE_URL="${PUBLIC_URL}/console"

# ------------------------------------------------------------------ 1. realm
realm=$(kc "${API}")
want=$(jq --arg host stalwart --arg from "noreply@${MAIL_DOMAIN}" '
  .organizationsEnabled = true
  | .adminEventsEnabled = true
  | .adminEventsDetailsEnabled = true
  | .attributes = ((.attributes // {}) + {"acr.loa.map": "{\"pwd\":1,\"mfa\":2}", "adminEventsExpiration": "7776000"})
  | if (.webAuthnPolicyRpEntityName // "keycloak") == "keycloak" then .webAuthnPolicyRpEntityName = "Nightware" else . end
  | if ((.smtpServer // {}) | has("host")) then . else
      .smtpServer = {host: $host, port: "25", from: $from, fromDisplayName: "Nightware", auth: "false", ssl: "false", starttls: "false"}
    end' <<<"$realm")
if [[ "$(jq -S . <<<"$realm")" != "$(jq -S . <<<"$want")" ]]; then
  log "Keycloak (console): realm -> organizations on, admin events kept 90 days, acr.loa.map, SMTP if unset, passkeys named Nightware"
  kcj -X PUT "${API}" --data-binary @- <<<"$want" >/dev/null
fi

# Organizations switch on the "Organization" identity-first step in Keycloak's
# browser flow (username first, password on a second page). Nightware does not
# route sign-in by organization, so turn that step off in the realm's bound
# browser flow: NightMail's sign-in stays one page, as before (verified).
BROWSER_FLOW=$(jq -r '.browserFlow // "browser"' <<<"$want")
org_step=$(kc "${API}/authentication/flows/$(jq -rn --arg a "$BROWSER_FLOW" '$a|@uri')/executions" \
  | jq -c '[.[] | select(.level==0 and .displayName=="Organization" and .requirement!="DISABLED")] | first // empty')
if [[ -n "$org_step" ]]; then
  log "Keycloak (console): disabling the organization identity-first step in the '${BROWSER_FLOW}' flow"
  kcj -X PUT "${API}/authentication/flows/$(jq -rn --arg a "$BROWSER_FLOW" '$a|@uri')/executions" \
    --data-binary @- <<<"$(jq -c '.requirement="DISABLED"' <<<"$org_step")" >/dev/null
fi

# Passkeys: the console's "Add a passkey" asks Keycloak for this action (kc_action).
passkey_action() { kc "${API}/authentication/required-actions/webauthn-register" 2>/dev/null || true; }
action=$(passkey_action)
if [[ -z "$action" ]]; then
  log "Keycloak (console): registering the passkey set-up action"
  kcj -X POST "${API}/authentication/register-required-action" -d '{"providerId":"webauthn-register","name":"Webauthn Register"}' >/dev/null \
    || warn "could not register webauthn-register; administrators cannot add passkeys"
  action=$(passkey_action)
fi
if [[ -n "$action" && "$(jq -r .enabled <<<"$action")" != true ]]; then
  log "Keycloak (console): enabling the passkey set-up action"
  kcj -X PUT "${API}/authentication/required-actions/webauthn-register" --data-binary @- <<<"$(jq -c '.enabled=true' <<<"$action")" >/dev/null
fi

# ------------------------------------------------------------------ 2. roles
for role in nightware-global-admin nightware-entity-admin; do
  if ! kc "${API}/roles/${role}" >/dev/null 2>&1; then
    log "Keycloak (console): creating realm role ${role}"
    kcj -X POST "${API}/roles" -d "$(jq -cn --arg n "$role" '{name:$n, description:"Nightware admin console (ADR 0002)"}')" >/dev/null
  fi
done

# ----------------------------------------------------------- 3. user profile
CONSOLE_ATTRS='[
  {"name":"nightware_aliases","displayName":"Secondary addresses","multivalued":true,"validations":{"length":{"max":254}}},
  {"name":"nightware_mail_deleted_at","displayName":"Mail deleted at","multivalued":false},
  {"name":"nightware_groupware_deleted_at","displayName":"Nextcloud data deleted at","multivalued":false},
  {"name":"nightware_access_history","displayName":"Access history","multivalued":true,"validations":{"length":{"max":255}}}
]'
profile=$(kc "${API}/users/profile")
updated=$(jq --argjson want "$CONSOLE_ATTRS" '
  .attributes as $have
  | .attributes = $have + [ $want[] | select(.name as $n | ($have | map(.name) | index($n)) | not)
      | . + {permissions: {view: ["admin"], edit: ["admin"]}} ]' <<<"$profile")
if [[ "$(jq -S . <<<"$profile")" != "$(jq -S . <<<"$updated")" ]]; then
  log "Keycloak (console): declaring nightware_aliases, the deletion markers and the access history (admin-only)"
  kcj -X PUT "${API}/users/profile" --data-binary @- <<<"$updated" >/dev/null
fi

# ------------------------------------------------------- 4. step-up flow
# Keycloak's documented step-up pattern: Cookie | (level 1: password; level 2: second factor).
# Level 2 is a passkey or OTP, whichever the user has; Keycloak offers the one
# whose credential they ranked first and "Try another way" for the other. A user
# with neither is asked to set up OTP (the set-up subflow runs only when the
# second-factor subflow did not).
FLOW=nightware-console-mfa
OLD_FLOW=nightware-console-browser  # OTP only; replaced by FLOW
SECOND="${FLOW} second factor"
SETUP="${FLOW} second factor setup"
flow_id() { kc "${API}/authentication/flows" | jq -r --arg a "$1" '.[] | select(.alias==$a) | .id'; }
executions() { kc "${API}/authentication/flows/$(jq -rn --arg a "$1" '$a|@uri')/executions"; }
set_requirement() { # flow, displayName or flow alias, requirement
  local rep; rep=$(executions "$1" | jq -c --arg n "$2" 'map(select(.displayName==$n)) | first // empty')
  [[ -n "$rep" ]] || { warn "no execution '$2' in $1"; return 1; }
  kcj -X PUT "${API}/authentication/flows/$(jq -rn --arg a "$1" '$a|@uri')/executions" \
    --data-binary @- <<<"$(jq -c --arg r "$3" '.requirement=$r' <<<"$rep")" >/dev/null
}
add_execution() { # flow, provider, requirement
  kcj -X POST "${API}/authentication/flows/$(jq -rn --arg a "$1" '$a|@uri')/executions/execution" -d "{\"provider\":\"$2\"}" >/dev/null \
    && set_requirement "$1" "$(executions "$1" | jq -r --arg p "$2" 'map(select(.level==0 and .providerId==$p)) | last | .displayName')" "$3"
}
add_subflow() { # parent, alias, requirement
  kcj -X POST "${API}/authentication/flows/$(jq -rn --arg a "$1" '$a|@uri')/executions/flow" \
    -d "$(jq -cn --arg a "$2" '{alias:$a, type:"basic-flow", provider:"registration-page-form", description:"Nightware admin console"}')" >/dev/null \
    && set_requirement "$1" "$2" "$3"
}
condition_config() { # flow, condition provider, config
  local id; id=$(executions "$1" | jq -r --arg p "$2" 'map(select(.level==0 and .providerId==$p)) | first | .id // empty')
  [[ -n "$id" ]] && kcj -X POST "${API}/authentication/executions/${id}/config" \
    -d "$(jq -cn --arg a "$1-condition" --argjson c "$3" '{alias:$a, config:$c}')" >/dev/null
}
loa_condition() { # subflow, level, max-age seconds
  add_execution "$1" conditional-level-of-authentication REQUIRED \
    && condition_config "$1" conditional-level-of-authentication "$(jq -cn --arg l "$2" --arg m "$3" '{"loa-condition-level":$l, "loa-max-age":$m}')"
}
if [[ -z "$(flow_id "$FLOW")" ]]; then
  log "Keycloak (console): building the step-up browser flow ${FLOW}"
  if ! { kcj -X POST "${API}/authentication/flows" \
       -d "$(jq -cn --arg a "$FLOW" '{alias:$a, providerId:"basic-flow", topLevel:true, builtIn:false, description:"Nightware admin console: password, then a passkey or OTP for acr mfa"}')" >/dev/null \
     && add_execution "$FLOW" auth-cookie ALTERNATIVE \
     && add_subflow "$FLOW" "${FLOW} forms" ALTERNATIVE \
     && add_subflow "${FLOW} forms" "${FLOW} level 1" CONDITIONAL \
     && loa_condition "${FLOW} level 1" 1 28800 \
     && add_execution "${FLOW} level 1" auth-username-password-form REQUIRED \
     && add_subflow "${FLOW} forms" "${FLOW} level 2" CONDITIONAL \
     && loa_condition "${FLOW} level 2" 2 300 \
     && add_subflow "${FLOW} level 2" "$SECOND" CONDITIONAL \
     && add_execution "$SECOND" conditional-user-configured REQUIRED \
     && add_execution "$SECOND" webauthn-authenticator ALTERNATIVE \
     && add_execution "$SECOND" auth-otp-form ALTERNATIVE \
     && add_subflow "${FLOW} level 2" "$SETUP" CONDITIONAL \
     && add_execution "$SETUP" conditional-sub-flow-executed REQUIRED \
     && condition_config "$SETUP" conditional-sub-flow-executed "$(jq -cn --arg f "$SECOND" '{flow_to_check:$f, check_result:"not-executed"}')" \
     && add_execution "$SETUP" auth-otp-form REQUIRED; }; then
    warn "could not build ${FLOW}; the console will refuse sign-ins until MFA works (or ADMIN_REQUIRED_ACR is empty)"
  fi
fi
FLOW_ID=$(flow_id "$FLOW")

# --------------------------------------------------------------- 5. client
: "${NIGHTWARE_ADMIN_CLIENT_SECRET:?run scripts/init-env.sh}"
export NIGHTWARE_ADMIN_CLIENT_SECRET  # read by jq from the environment, not passed on its command line
client_rep=$(jq -cn --arg id "$CLIENT_ID" \
  --arg url "$CONSOLE_URL" --arg acr "$REQUIRED_ACR" --arg flow "$FLOW_ID" '{
    clientId: $id, name: "Nightware admin console",
    description: "Admin console (ADR 0002): sign-in for administrators, and its own service account for the Keycloak admin API.",
    enabled: true, publicClient: false, secret: $ENV.NIGHTWARE_ADMIN_CLIENT_SECRET, clientAuthenticatorType: "client-secret", protocol: "openid-connect",
    standardFlowEnabled: true, implicitFlowEnabled: false, directAccessGrantsEnabled: false, serviceAccountsEnabled: true,
    redirectUris: [($url + "/signin-oidc")], webOrigins: ["-"],
    attributes: ({"pkce.code.challenge.method": "S256", "post.logout.redirect.uris": ($url + "/signout-callback-oidc"),
                  "client.session.idle.timeout": "1200", "client.session.max.lifespan": "28800"}
                 + (if $acr == "" then {} else {"minimum.acr.value": $acr} end)),
    authenticationFlowBindingOverrides: (if $flow == "" then {} else {browser: $flow} end),
    defaultClientScopes: ["acr", "basic", "profile", "email", "roles"],
    optionalClientScopes: ["kc-admin-api"]}')
CLIENT_UUID=$(kc "${API}/clients?clientId=${CLIENT_ID}" | jq -r '.[0].id // empty')
if [[ -z "$CLIENT_UUID" ]]; then
  log "Keycloak (console): creating client ${CLIENT_ID}"
  kcj -X POST "${API}/clients" --data-binary @- <<<"$client_rep" >/dev/null
  CLIENT_UUID=$(kc "${API}/clients?clientId=${CLIENT_ID}" | jq -r '.[0].id')
else
  current=$(kc "${API}/clients/${CLIENT_UUID}")
  merged=$(jq --argjson want "$client_rep" '. + ($want | del(.attributes)) | .attributes = ((.attributes // {}) + $want.attributes)
    | if ($want.attributes | has("minimum.acr.value")) then . else .attributes |= del(."minimum.acr.value") end' <<<"$current")
  if [[ "$(jq -S 'del(.secret)' <<<"$current")" != "$(jq -S 'del(.secret)' <<<"$merged")" ]] \
     || [[ "$(kc "${API}/clients/${CLIENT_UUID}/client-secret" | jq -r .value)" != "$NIGHTWARE_ADMIN_CLIENT_SECRET" ]]; then
    log "Keycloak (console): updating client ${CLIENT_ID}"
    kcj -X PUT "${API}/clients/${CLIENT_UUID}" --data-binary @- <<<"$merged" >/dev/null
  fi
fi

# The OTP-only flow goes once nothing is bound to it (the client was rebound above).
OLD_FLOW_ID=$(flow_id "$OLD_FLOW")
if [[ -n "$OLD_FLOW_ID" && -n "$FLOW_ID" ]] \
   && [[ "$(kc "${API}/clients/${CLIENT_UUID}" | jq -r '.authenticationFlowBindingOverrides.browser // empty')" == "$FLOW_ID" ]]; then
  log "Keycloak (console): deleting the OTP-only flow ${OLD_FLOW}"
  kc -X DELETE "${API}/authentication/flows/${OLD_FLOW_ID}" >/dev/null || warn "could not delete ${OLD_FLOW}"
fi

# Its service account: users (lookup, create, enable, role mapping), the realm's
# roles (read), and organizations. No realm administration.
SA=$(kc "${API}/clients/${CLIENT_UUID}/service-account-user" | jq -r .id)
RM=$(kc "${API}/clients?clientId=realm-management" | jq -r '.[0].id')
have=$(kc "${API}/users/${SA}/role-mappings/clients/${RM}" | jq -r '.[].name')
available=$(kc "${API}/users/${SA}/role-mappings/clients/${RM}/available")
# view-realm: GET /roles/{name} and /roles/{name}/users answer 403 without it (verified on 26.8), and the
# console needs both to grant and list global admins. It is read-only; Keycloak masks the realm's secrets.
# No view-clients: that would expose every client's secret.
for role in view-users query-users manage-users query-groups query-clients view-realm view-organizations query-organizations manage-organizations; do
  grep -qx "$role" <<<"$have" && continue
  rep=$(jq -c --arg n "$role" '[.[] | select(.name==$n)]' <<<"$available")
  if [[ "$rep" == "[]" ]]; then
    warn "realm-management role ${role} does not exist in this Keycloak; the console may not see organizations"
    continue
  fi
  log "Keycloak (console): service account gets realm-management ${role}"
  kcj -X POST "${API}/users/${SA}/role-mappings/clients/${RM}" -d "$rep" >/dev/null
done

# -------------------------------------------------------- 6. default entity
ENTITY_NAME="${NIGHTWARE_DEFAULT_ENTITY_NAME:-Nightware}"
ENTITY_ALIAS="${NIGHTWARE_DEFAULT_ENTITY_ALIAS:-$(tr '[:upper:] ' '[:lower:]-' <<<"$ENTITY_NAME" | tr -cd 'a-z0-9-')}"
ORG_ID=$(kc "${API}/organizations?search=$(jq -rn --arg d "$MAIL_DOMAIN" '$d|@uri')&exact=true" | jq -r '.[0].id // empty')
if [[ -z "$ORG_ID" ]]; then
  log "Keycloak (console): creating entity ${ENTITY_NAME} (${ENTITY_ALIAS}) for ${MAIL_DOMAIN}"
  kcj -X POST "${API}/organizations" -d "$(jq -cn --arg n "$ENTITY_NAME" --arg a "$ENTITY_ALIAS" --arg d "$MAIL_DOMAIN" \
    '{name:$n, alias:$a, enabled:true, domains:[{name:$d, verified:false}]}')" >/dev/null \
    || warn "could not create the default entity"
  ORG_ID=$(kc "${API}/organizations?search=$(jq -rn --arg d "$MAIL_DOMAIN" '$d|@uri')&exact=true" | jq -r '.[0].id // empty')
fi
if [[ -n "$ORG_ID" ]]; then
  members=$(kc "${API}/organizations/${ORG_ID}/members?first=0&max=10000&briefRepresentation=true" | jq -r '.[].id')
  first=0
  while :; do
    page=$(kc "${API}/users?first=${first}&max=200&briefRepresentation=true&search=*@${MAIL_DOMAIN}")
    [[ "$(jq length <<<"$page")" == 0 ]] && break
    jq -r --arg d "@${MAIL_DOMAIN}" '.[] | select((.email // "" | ascii_downcase | endswith($d)) and (.serviceAccountClientId == null)) | .id' <<<"$page" \
      | while read -r uid; do
          grep -qx "$uid" <<<"$members" && continue
          kcj -X POST "${API}/organizations/${ORG_ID}/members" -d "\"${uid}\"" >/dev/null 2>&1 || true
        done
    first=$((first + 200))
  done
  log "Keycloak (console): users with an @${MAIL_DOMAIN} address are members of ${ENTITY_NAME}"
fi

# ---------------------------------------------------------- 7. global admins
BOOTSTRAP="${ADMIN_BOOTSTRAP_USERS:-}"
[[ -z "$BOOTSTRAP" && "${NIGHTWARE_KC_SEED:-1}" != "0" ]] && BOOTSTRAP=alice
role=$(kc "${API}/roles/nightware-global-admin")
for who in $BOOTSTRAP; do
  field=username; [[ "$who" == *@* ]] && field=email
  uid=$(kc "${API}/users?${field}=$(jq -rn --arg w "$who" '$w|@uri')&exact=true" | jq -r '.[0].id // empty')
  if [[ -z "$uid" ]]; then warn "ADMIN_BOOTSTRAP_USERS: no user ${who}"; continue; fi
  if ! kc "${API}/users/${uid}/role-mappings/realm" | jq -e '.[] | select(.name=="nightware-global-admin")' >/dev/null; then
    log "Keycloak (console): ${who} is a global administrator"
    kcj -X POST "${API}/users/${uid}/role-mappings/realm" -d "[${role}]" >/dev/null
  fi
done
log "Keycloak (console): configured (${CONSOLE_URL})"
