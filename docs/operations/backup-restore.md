# Backup and restore

The façade has nothing to back up (stateless; configuration lives in `.env`/environment). Back up
the three backends, plus `deploy/.env` (secrets — store it encrypted) and the Caddy data volume
(certificates; optional, they are re-issued).

Volume names below assume the compose project `nightware` (`nightware_<volume>`). Never run
`docker compose down -v`: it deletes every volume. Take backups to storage outside the host.

A consistent restore point needs all three backends from about the same time: an event's iMIP mail
lives in Stalwart, the event in Nextcloud, and the users in Keycloak.

## Keycloak (identity: users, realm, clients)

**Primary: the Postgres database** (online, consistent):

```bash
docker compose -p nightware exec -T keycloak-db pg_dump -U keycloak -d keycloak -Fc > keycloak-$(date +%F).dump
```

**Secondary: a realm export** (portable JSON, useful to diff configuration and to rebuild on a new
database). `kc.sh export` must not run inside the serving container; run it as a one-off container
on the same database:

```bash
docker compose -p nightware run --rm --no-deps -v "$PWD/kc-export:/export" keycloak \
  export --realm nightware --dir /export --users realm_file
```

Do **not** stop the serving Keycloak to back it up (see README: token refresh).

**Restore.**

```bash
docker compose -p nightware stop keycloak
docker compose -p nightware exec -T keycloak-db dropdb -U keycloak keycloak
docker compose -p nightware exec -T keycloak-db createdb -U keycloak keycloak
docker compose -p nightware exec -T keycloak-db pg_restore -U keycloak -d keycloak < keycloak-YYYY-MM-DD.dump
docker compose -p nightware start keycloak
```

Signing keys are in the database, so restored tokens validate; sessions issued after the backup are
gone and those users sign in again. Restoring only a realm export on a new database creates new
signing keys: every outstanding token (NightMail's included) becomes invalid.

## Stalwart (mail, Sieve, vacation responses, its configuration)

Stalwart keeps its data store in `nightware_stalwart-data` (`/var/lib/stalwart`) and configuration
in `nightware_stalwart-etc` (`/etc/stalwart`). For a crash-consistent copy, stop it briefly — mail
calls answer 503 meanwhile, which NightMail retries, and SMTP senders queue and retry:

```bash
docker compose -p nightware stop stalwart
for v in stalwart-data stalwart-etc; do
  docker run --rm -v nightware_$v:/v:ro -v "$PWD:/backup" alpine tar czf /backup/$v-$(date +%F).tgz -C /v .
done
docker compose -p nightware start stalwart
```

(With an external store — PostgreSQL, S3 blob store — back those up with their own tools instead.)
For per-account copies, Stalwart's CLI (`stalwart-cli export account <name>`) produces portable
exports.

**Restore.** Stop Stalwart, empty and untar both volumes, start it:

```bash
docker compose -p nightware stop stalwart
for v in stalwart-data stalwart-etc; do
  docker run --rm -v nightware_$v:/v -v "$PWD:/backup" alpine sh -c "rm -rf /v/* && tar xzf /backup/$v-YYYY-MM-DD.tgz -C /v"
done
docker compose -p nightware start stalwart
```

JMAP ids survive, so NightMail's cached ids stay valid; JMAP state strings may move backwards, in
which case delta answers `410 syncStateNotFound` and NightMail resyncs.

## Nextcloud (calendars, contacts, tasks, files, rooms)

Calendars, address books and tasks live in the **database** (`oc_calendarobjects`, `oc_cards`);
files in the **data** volume; configuration (`config.php`, installed apps) in the **html** volume.
Back up all three under maintenance mode so they agree (DAV calls answer 503 meanwhile — safe):

```bash
docker compose -p nightware exec -u www-data nextcloud php occ maintenance:mode --on
docker compose -p nightware exec -T nextcloud-db pg_dump -U nextcloud -d nextcloud -Fc > nextcloud-db-$(date +%F).dump
for v in nextcloud-data nextcloud-html; do
  docker run --rm -v nightware_$v:/v:ro -v "$PWD:/backup" alpine tar czf /backup/$v-$(date +%F).tgz -C /v .
done
docker compose -p nightware exec -u www-data nextcloud php occ maintenance:mode --off
```

Per-user portable copies (optional): each calendar's `?export` and each address book's `?export`
over WebDAV give `.ics`/`.vcf` files.

**Restore.** Maintenance mode on; restore the database (`dropdb`/`createdb`/`pg_restore` as for
Keycloak, user `nextcloud`); untar both volumes; then

```bash
docker compose -p nightware exec -u www-data nextcloud php occ maintenance:data-fingerprint
docker compose -p nightware exec -u www-data nextcloud php occ maintenance:mode --off
```

`data-fingerprint` tells sync clients the server went back in time. DAV sync tokens move backwards;
expect NightMail's calendar and contact caches to resync.

## Test your restores

Restore into a scratch project (`docker compose -p nightware-restore …` with its own volumes and
ports), run `deploy/smoke.sh` against it, and sign in from NightMail once.
