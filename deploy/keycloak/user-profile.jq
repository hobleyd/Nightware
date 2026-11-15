# Merges Nightware's declarations into Keycloak's realm user-profile config
# (GET/PUT /admin/realms/nightware/users/profile). Input: the current config.
# $want: the mailboxSettings attributes (keycloak/configure.sh). Read-modify-
# write: every other attribute, group and setting is kept as it is.
#
# - mailboxSettings attributes: user-viewable, admin-editable, single-valued;
#   declared when missing (`locale` is Keycloak's own and keeps its settings).
# - email and username: only `permissions.edit` changes, to admins only. The
#   email claim is the user's identity (Stalwart's claimUsername, the façade's
#   /me mailbox); a self-service edit could claim another mailbox's address.
def admin_only: {view: ["admin", "user"], edit: ["admin"]};

.attributes as $have
| .attributes = ($have | map(
    if (.name as $n | $want | map(.name) | index($n)) and .name != "locale"
    then .permissions = admin_only | .multivalued = false
    elif .name == "email" or .name == "username"
    then .permissions = ((.permissions // {view: ["admin", "user"]}) + {edit: ["admin"]})
    else . end))
  + [ $want[] | select(.name as $n | ($have | map(.name) | index($n)) | not)
      | . + {permissions: admin_only, multivalued: false} ]
