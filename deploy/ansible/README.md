# Deploying with Ansible

A playbook that puts the whole of `deploy/docker-compose.yml` on one Docker host and configures
it: Caddy, Keycloak, Stalwart, Nextcloud, Collabora, the Graph façade, the admin console, and
Grafana with the façade's traces, metrics and logs wired in. It follows
[`docs/operations/deployment.md`](../../docs/operations/deployment.md); read that first.

## What it does

1. Checks the repository out at `nightware_dir` (`/opt/nightware`), at `nightware_version`.
2. Renders a production realm (`roles/nightware/files/production-realm.jq`): no test users, no
   `nightware-dev` client, brute-force lockout and a password policy. Keycloak imports it once,
   into an empty database.
3. Writes `deploy/.env` from your variables (`NIGHTWARE_ENVIRONMENT=Production`, publicly trusted
   TLS), carrying over the console's Stalwart API key that `stalwart/configure.sh` minted.
4. `docker compose --profile api up -d --wait`, building the façade and console when the checkout
   moved.
5. Runs `keycloak/configure.sh`, creates `nightware_users` in Keycloak (temporary
   `nightware_user_initial_password`), then `keycloak/configure-console.sh` (console and Grafana
   clients, MFA, global admins: the users with `admin: true`), `stalwart/configure.sh` (their
   mailboxes) and `nextcloud/configure.sh`.

Every step converges; re-run it after pulling a new version.

## Use

Needs Docker Engine with the compose plugin on the host, and a remote user in the `docker` group
(package installs use `become`). `PUBLIC_HOST` must resolve to the host, and 80/443 reach it for
Let's Encrypt.

```bash
cd deploy/ansible
cp inventory.example.yml inventory.yml
cp vars.example.yml vars.yml       # fill in, then: ansible-vault encrypt vars.yml
ansible-playbook -i inventory.yml site.yml --ask-vault-pass
```

Afterwards:

| URL | |
|---|---|
| `https://<host>/console/` | Admin console: users with `admin: true`, password + OTP |
| `https://<host>/grafana/` | Grafana: the same people, through Keycloak. Also linked from the console's Systems panel |
| `https://<host>/.well-known/nightware` | What NightMail reads; users type `<host>` into NightMail |

`/console` and `/grafana` are open to `nightware_admin_allowed_cidrs`; narrow it.

## Telemetry

The façade exports over OTLP to the `grafana` service (`grafana/otel-lgtm`: an OpenTelemetry
Collector, Prometheus, Tempo, Loki and Grafana). Set `nightware_otlp_endpoint` to send it to your
own collector instead, or to `""` to turn export off. See
[`docs/operations/monitoring.md`](../../docs/operations/monitoring.md).
