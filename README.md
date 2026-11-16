# Nightware

Self-hosted, Microsoft Graph–compatible backend for the
[NightMail](../nightmail) client: a stateless Graph façade over Stalwart (mail),
Nextcloud (calendar, contacts, tasks, files) and Keycloak (identity).

- Architecture and conventions: [`docs/architecture.md`](docs/architecture.md)
- Delivery plan: [`docs/phases/`](docs/phases)
- Running the stack: [`deploy/README.md`](deploy/README.md)

```bash
dotnet build
dotnet test
```

## Licence

GNU General Public License v3.0 — see [LICENSE](LICENSE).
