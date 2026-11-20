# tools/capture

Records real Microsoft Graph responses as conformance fixtures for the
Graph-parity phases. The rules it serves are in ADR 0006, D2.
`capture_graph.py` runs the scenarios in `scenarios.py` against a Microsoft 365
mailbox. It scrubs the output and writes one fixture per operation id to `out/`,
which git ignores. A phase moves its fixtures into
`tests/Nightware.Conformance.Tests/Fixtures` together with the matching
`endpoints.json` entries. Standard library only.

## Before you run it

- **Use a dedicated test mailbox.** The scenarios create and delete things in
  it: folders, rules, categories, calendars, contacts and To Do lists. They
  also send mail to the mailbox itself. Everything is named `Nightware <tag>`
  or `nw-capture-<tag>`, and the cleanup steps run even after a failure.
- **Get a delegated token for that mailbox.** In
  [Graph Explorer](https://developer.microsoft.com/graph/graph-explorer), sign
  in as the test user. Consent to `Mail.ReadWrite`, `Mail.Send`,
  `MailboxSettings.ReadWrite`, `Calendars.ReadWrite`, `Contacts.ReadWrite` and
  `Tasks.ReadWrite` (Modify permissions). Then copy the token from the
  *Access token* tab. It lasts about an hour, so run one phase at a time if
  needed. The script warns when a scope is missing.

```bash
export GRAPH_TOKEN='eyJ0…'
python3 tools/capture/capture_graph.py --phase all --dry-run   # what would be sent
python3 tools/capture/capture_graph.py --phase 11 --phase 12
python3 tools/capture/capture_graph.py --phase 13 --colleague someone@yourtenant.example
python3 tools/capture/capture_graph.py --phase 14 --phase 15
```

`--colleague` names a second mailbox in the tenant. Calendar sharing is
captured only when it's given.

## Phase 16: change notifications

Graph has to reach a public HTTPS URL, both to validate the subscription and to
deliver notifications. The script listens on `127.0.0.1:8765`. Put a temporary
tunnel in front of it, for example:

```bash
cloudflared tunnel --url http://127.0.0.1:8765     # prints https://<name>.trycloudflare.com
python3 tools/capture/capture_graph.py --phase 16 \
  --notification-url https://<name>.trycloudflare.com/notify
```

It records:

- the subscription calls;
- Graph's validation request (`subscriptions.validation`);
- the first notification after a mail to the mailbox itself
  (`subscriptions.notification`).

Both of those are marked `"direction": "outbound"`.

## Scrubbing

These are replaced everywhere, including the percent-encoded forms in paths and
`@odata.context`:

| What | Replaced with |
|---|---|
| Tenant and user ids | Fixed GUIDs |
| The mailbox's address, UPN and domain | `me@example.com` / `example.com` |
| Its display name, given name and surname | `Me Example`, `Me`, `Example`: in name fields, plus the display name as a whole phrase anywhere. Keys and other words are never touched, so a user called "Test User" leaves `Users/…` alone |
| The colleague's address | `colleague@example.com` |
| The tunnel URL | `https://webhook.example.com/notify` |
| Every Graph id (`id`, `…Id`, `changeKey`, `iCalUId`) | Stable placeholders |
| Delta/skip tokens in request queries | `{token}` |
| Binary bodies | Length and SHA-256 |

A leak check then searches each fixture for every original value. A fixture
that fails it isn't written, and the exit code is 1. `--allow-leaks` writes it
anyway, so you can inspect it.

Offline tests: `python3 -m unittest tools/capture/test_capture_graph.py`.

**Read `out/` before handing it over.** Mail bodies and signatures can carry
tenant text the scrubber doesn't know about.
