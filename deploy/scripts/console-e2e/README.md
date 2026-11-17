# Admin console walk-through (dev/test stacks only)

Drives the console at `https://${PUBLIC_HOST}:${HTTPS_PORT}/console` through curl, signing in through
Keycloak with OTP (it captures the TOTP secret at first sign-in). It creates entities and users
(`acme.test`, `beta.test`, carol, gina, …), offboards and deletes data. **Never run it against a real
deployment.** It needs a seeded dev stack (alice and bob, `TEST_USER_PASSWORD`) with the `api` profile up.

```bash
python3 -I deploy/scripts/console-e2e/console_e2e.py deploy signin create-entity dns plan-bob create-carol offboard-carol delete-carol-mail grant-global signout
python3 -I deploy/scripts/console-e2e/console_more.py deploy signin-alice create-gina aliases grant-bob-entity-admin signin-bob bob-scope gina-signs-in gina-graph
```

Cookies, captured OTP secrets and ids are kept next to the scripts (`*.jar`, `*.state.json`; git-ignored).
`delete-carol-mail` needs a sign-in younger than five minutes or completes the forced re-authentication.
