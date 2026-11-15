# Upgrade policy

## Pinned versions

Every image in `deploy/docker-compose.yml` is pinned to an exact version tag (`keycloak:26.8.2`,
`stalwart:v1.1.0`, `nextcloud:35.0.3-apache`, `postgres:17.12-alpine`, `redis:8.6.8-alpine`,
`caddy:2.11.9-alpine`); the façade's Dockerfile pins the .NET 11 SDK/runtime major; NuGet versions
are pinned centrally in `Directory.Packages.props`; `global.json` pins the SDK feature band. Never
use `latest` or a floating major. Bump one component per change, in its own commit, with the
release notes linked in the message.

## Before any upgrade

1. Take a backup ([backup-restore.md](backup-restore.md)).
2. Read the release notes for breaking changes in what the façade depends on (below).
3. Upgrade a copy first: a scratch compose project restored from the backup, then `deploy/smoke.sh`
   and `make integration` against it. `deploy/recordings/` can be re-recorded
   (`deploy/scripts/record.sh`) and diffed to spot protocol changes.

## Per component

**Keycloak** — the most sensitive. The façade depends on: standard token exchange (RFC 8693, V2)
on `nightware-api`; the audience mappers producing `aud: nightware-api` on NightMail tokens and
`aud: stalwart|nextcloud` on exchanged tokens; the `kc-admin-api` client scope, which since 26.8 an
admin-API token must carry (without it the admin API answers 401 — directory lookups then fail with
503); declared user-profile attributes for mailboxSettings (Keycloak 26 drops undeclared ones);
`KC_HOSTNAME`/`KC_HOSTNAME_BACKCHANNEL_DYNAMIC` behaviour for issuer vs. internal URLs. After an
upgrade, check `deploy/get-token.sh --claims alice`, `--exchange stalwart alice` and `--service`.
Upgrade without an outage of the token endpoint: run a second instance on the new version against
the same database, switch Caddy, then retire the old one — never let `auth.<domain>` answer 502/503
(NightMail signs users out). Keycloak migrates its database on first start of a new version; that
is one-way, hence the backup.

**Stalwart** — the façade depends on JMAP core/mail/submission/vacation, `Email/changes` semantics,
the session's `maxSizeUpload`/`maxObjectsInGet`, the OIDC directory accepting exchanged tokens, and
the master-user separator. Minor versions are usually drop-in; read the "breaking changes" notes for
directory or storage migrations.

**Nextcloud** — upgrade one major at a time (Nextcloud refuses to skip majors). Run
`occ upgrade` and `occ db:add-missing-indices` after the image bump. Re-check the apps the façade
needs and their versions (calendar, contacts, tasks, user_oidc with bearer validation,
calendar_resource_management) — `deploy/nextcloud/configure.sh` lists them. Expect 503s from DAV
during the upgrade (safe for NightMail).

**Postgres** — minor versions are drop-in; a major version needs `pg_dump`/`pg_restore` (or
`pg_upgrade`) into a new volume.

**Caddy** — drop-in for minor versions; check the Caddyfile syntax with `caddy validate`.

**The façade** — rebuild and replace the container (`make api`). It is stateless: rolling restarts
are safe as long as every instance shares `DeltaLinks:SigningKey`. Run `make ci` before building
the image. NuGet bumps: `make vulnerable` must stay clean; OpenTelemetry packages move together.
