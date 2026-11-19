# ADR 0003 — Groups: distribution lists and Nextcloud access

Status: accepted (2026-11-19). Builds on ADR 0002 (the admin console); takes
"mailing lists" from its OQ6. Plan and step 0 findings:
[`docs/phases/phase-9-groups.md`](../phases/phase-9-groups.md).

## Context

An entity needs two things that are really one list of people: an e-mail
address that reaches a set of colleagues, and a way to give that same set
access to documents in Nextcloud. Kept apart, the two drift ("who gets
finance@ mail?" vs "who sees the Finance folder?"). The console is the system
of record for the stack (it replaces the backends' own admin UIs), and ADR 0002
already rules out a store of its own.

## Decisions

### D1 — One group, two facets
A **group** belongs to one entity and has members (the entity's users). It can
have a **mail facet** — a Stalwart `x:MailingList` at an address on one of the
entity's domains, with aliases, a sender policy and optional outside
recipients — and a **files facet** — a Nextcloud group, optionally with a
**team folder** (`groupfolders`) shared with that group only. Either facet is
switched on or off later; members are edited once.

Shared mailboxes (one inbox several people open) are a different feature:
ADR 0004.

### D2 — Keycloak is the record
A group is a subgroup of the organization group **`groups`** of its entity
(created on first use), so `admins` and any later system group can never be
read or edited as one. The subgroup's **name is the group's key** (immutable;
Keycloak refuses a second subgroup with the same name, which keeps keys
unique). Everything else is an admin-only group attribute:

| Attribute | Meaning |
|---|---|
| `nightware_name` | display name (changes freely) |
| `nightware_list_address` | the list's address; absent without a mail facet |
| `nightware_list_aliases` | multivalued |
| `nightware_external_recipients` | multivalued outside addresses |
| `nightware_senders` | `members` \| `organization` \| `anyone` |
| `nightware_files` | `true` with a files facet |
| `nightware_team_folder` | `true` when a team folder is wanted |
| `nightware_team_folder_id` | the folder's id once created |

Keycloak 26.8 ignores attributes on create and leaves them out of child
listings, so the adapter writes them with a `PUT` after creating and reads
each group on its own.

### D3 — Derived, pushed, repaired
`GroupSync` derives what each backend should hold from the record and writes
the difference at once: list recipients are the **enabled** members' addresses
plus the outside recipients; Nextcloud membership is the enabled members'
usernames; the team folder is created (its id recorded before it is shared)
and shared with the group. Membership changes take effect immediately in
Nextcloud, also for tokens issued earlier. `user_oidc` group provisioning from
token claims is **not** used: a removal would wait for the member's next
sign-in, and claim sync can strip groups it does not know (the provisioning
account's `admin`).

A group's page shows a chip per backend (Members, List, Nextcloud group) with
the existing link states; "out of step" lists what Repair adds and removes. A
member who has never signed in to Nextcloud is waiting, not out of step.

### D4 — Downstream names
- Stalwart list: found by address (`x:MailingList/query` supports only `text`;
  the address is confirmed on `get`).
- Nextcloud group id: `<entity alias>-<key>` (ids are global; two entities may
  both have `finance`). Its display name follows the group's name.
- Team folder: by the recorded id; mount point is the group's name at creation.

### D5 — Who may send
Per list: members only (default), anyone at the entity's domains, or anyone.
Stalwart lets anyone post to a list, so the console keeps one **system Sieve
script**, `nightware-lists`, which `deploy/stalwart/configure.sh` creates and
makes the RCPT stage run (`MtaStageRcpt.script`). It holds one marked block per
restricted list:

```sieve
# nightware-list finance@acme.example
if allof (envelope :is "to" ["finance@acme.example", "accounts@acme.example"],
          not envelope :is "from" ["bob@acme.example", "carol@acme.example"]) {
    reject "550 5.7.1 finance@acme.example accepts mail from its members only";
}
# end nightware-list finance@acme.example
```

(`not envelope :domain :is "from" [...]` for the organisation policy.) The
console rewrites only its list's block and reloads the settings in the same
request. Members may send from their aliases too.

**Senders who did not sign in** (G6, decided 2026-11-19). Stalwart's SPF,
DMARC and reverse-IP checks are `relaxed` (they score, never refuse), so an
envelope sender alone proves nothing. Two rules close that, both set by
`deploy/stalwart/configure.sh` and both treating the stack's Docker network
(`DOCKER_SUBNET`) and `MAIL_TRUSTED_NETWORKS` as trusted:

1. **Own domains need a signed-in sender.** At MAIL FROM, unauthenticated mail
   from outside the trusted networks whose sender is on one of the server's own
   domains is refused (`MtaStageMail.isSenderAllowed`, `550 5.7.1 Sender
   address not allowed`). Nobody on the internet can send as a colleague, to a
   list or to anyone else. Stalwart's default spam-block clause is kept.
2. **Restricted lists need a signed-in sender.** The RCPT stage runs a second
   script, `nightware-lists-unauthenticated`, for unauthenticated senders
   outside the trusted networks; the console writes a block there for every
   `members` or `organization` list that refuses all of them (`550 5.7.1 …
   accepts mail from signed-in senders only`). `nightware-lists` (the policy
   above) runs for everyone else.

Verified on the local stack (an untrusted sender simulated by narrowing the
trusted network): a forged colleague is refused at MAIL FROM, an outsider and
bounces still pass; an unauthenticated sender is refused by a restricted list;
a member's JMAP submission (NightMail's path) reaches the list. On the live VM
inbound SMTP keeps the sender's real address (Docker publishes port 25 without
the userland proxy), so the trusted network is only the stack's own.
Consequence: a third party that sends as our domains from its own servers (a
newsletter service, a scanner) must sign in or be added to
`MAIL_TRUSTED_NETWORKS`.

### D6 — Outside recipients only on closed lists (G5)
Stalwart 1.1 forwards without rewriting the envelope sender (no SRS). A copy
forwarded outside therefore passes the receiver's SPF/DMARC only when the
original sender is on one of our domains. So a list may have outside
recipients only while its sender policy is `members` or `organization`;
adding one to an `anyone` list, or opening a list that has them, is refused
with the reason. Outside recipients cannot post (they are neither members nor
the organisation).

### D7 — Addresses are unique, everywhere
A list address or alias must be on the entity's domains, not reserved, and not
any user's, mailbox's, alias's or list's. The console checks first, to say
whose it is; Stalwart enforces the same across accounts, aliases and lists
(`primaryKeyViolation`), which the adapters map to a Conflict naming the
holder — so the new-user wizard and aliases now refuse a list's address too.

### D8 — Lifecycle
- Offboarding a user brings every group they are in back into line: as a
  disabled member they leave the lists and Nextcloud groups. Their Keycloak
  membership is kept, so reinstating puts them back. A group that cannot be
  brought into line fails the offboarding (finish it again later).
- Removing a domain is refused while a group's list has an address on it,
  naming the group; Stalwart's own refusal now counts lists apart from accounts.
- Deleting a group destroys its list (and opens its sender rule), deletes the
  Nextcloud group, then the record, last. The **team folder is kept**, shared
  with no one (Nextcloud administrators still see it); a folder is only taken
  away by **Delete team folder**: global admins (or entity admins where
  `EntityAdminsMayDeleteData` is on), a fresh strong sign-in, and the group's
  name typed again — as for deleting a user's data.

### D9 — Authorization
Entity admins manage their own entity's groups; members are chosen from the
entity's users only. Every use case loads the group through `GroupAccess`
(`RequireEntity`), and the Keycloak adapter only ever returns subgroups of
`groups`.

## Consequences
- The console's Stalwart role gains `sysMailingList*` and
  `sysSieveSystemScript{Get,Query,Create,Update}`; Nextcloud needs the
  `groupfolders` app (both in the configure scripts).
- NightMail is unchanged: mail to a list works from any client, but address
  autocomplete does not suggest lists (no `/groups` in the Graph contract).
- Bounces for outside recipients go to the original sender, not the list.
- A hand-edited `nightware-lists` block that is left unterminated is dropped on
  the next change; anything outside the marked blocks is kept.

## Verified
All against the local stack on 2026-11-19 (Keycloak 26.8.2, Stalwart 1.1.0,
Nextcloud with groupfolders 23.0.1): step 0 in the phase plan, the live test
`tests/Nightware.Integration.Tests/Administration/GroupsLiveTests.cs`, and the
generated sender rules over SMTP from the trusted Docker subnet (non-members
550 5.7.1 at RCPT; members, organisation senders and mail to ordinary
addresses accepted), and the G6 rules with an untrusted sender simulated (D5).
