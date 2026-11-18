# deploy/keycloak/nightware-realm.json is the development stack's. For a public
# host: no test users (alice and bob, with a published password), no
# nightware-dev client (the password grant, for scripts), and Keycloak's
# brute-force lockout and a password policy.
.users |= map(select(.username != "alice" and .username != "bob"))
| .clients |= map(select(.clientId != "nightware-dev"))
| .bruteForceProtected = true
| .permanentLockout = false
| .failureFactor = 10
| .passwordPolicy = "length(12) and notUsername and notEmail"
