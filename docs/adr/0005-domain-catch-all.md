# ADR 0005 — A domain's catch-all user

Status: accepted (2026-11-19). Builds on ADR 0002 (the admin console).

## Context

Mail to an address no one has on an entity's domain is refused at RCPT
(`550 5.1.1 Mailbox does not exist`). Some entities want it kept instead: a
mistyped or retired address still reaches someone. Stalwart 1.1 does this per
domain with `x:Domain.catchAllAddress` ("receives messages addressed to unknown
local recipients").

## Decisions

### D1 — One user per domain, Keycloak the record
An entity chooses, per domain, at most one of its own users to receive that mail.
The choice is an organization attribute **`nightware_catch_all:<domain>`** holding
the user's id, beside the SPF and DMARC ones, and is forgotten with the domain.
The mail server is then given the user's **primary address**. When that fails the
choice stays and the entity page says so; saving again sends it again. Repair
(global admins, offered while the mail server lacks the domain or its DKIM keys)
also restores it to a domain it recreates.
A catch-all set on the mail server with no record is shown, never cleared.

### D2 — Who may choose
Entity admins, for their own entity, as well as global admins: they can already
give their users any alias on the domain, which is the same reach. Every change is
audited (`entity.domain.catch-all`).

### D3 — Only someone who reads mail
The user must belong to the entity, be enabled and have their own mailbox (its
link is Linked). Stalwart does not check the address: one no mailbox holds is
accepted at RCPT and then bounced to the sender (backscatter). So:

- **Deleting the catch-all user's mail is refused**, naming the domains, until
  someone else is chosen. Deleting anyone else's mail now says their address will
  go to the catch-all user afterwards, rather than bounce.
- **Offboarding keeps it.** Mail keeps arriving and is kept, as it is for the
  user's own address (ADR 0002 D10); the entity page flags that no one reads it.

### D4 — Stalwart findings (1.1.0, local stack)
- `x:Domain/set update {catchAllAddress}` applies at the next RCPT with **no**
  `ReloadSettings`; `null` refuses unknown addresses again.
- Sub-addresses (`unknown+tag@`) reach the catch-all too. Probed with it set:
  a user's mail stays theirs, a list's reaches its recipients and a shared
  mailbox's lands in its own Inbox; none reaches the catch-all user. A restricted
  list's `550 5.7.1` refusal was not re-probed.
- The address may be on another domain of the server.
- The console's custom Stalwart role already holds `sysDomainUpdate`: no deploy
  change.

## Consequences
- The catch-all user also receives spam sent to made-up addresses; the entity
  page says so.
- Verified live: `tests/Nightware.Integration.Tests/Administration/CatchAllLiveTests.cs`
  (choose → RCPT 250 for an unknown address; clear → 550).
