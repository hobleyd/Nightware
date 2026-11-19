# Phase 10 — Shared mailboxes

> **Status (2026-11-19): implemented.** Decisions are in
> [ADR 0004](../adr/0004-shared-mailboxes.md). Not done: a shared mailbox's
> calendar or contacts (no backend for them).

**Goal:** an entity administrator creates a shared mailbox (`support@acme.example`),
chooses who may open it, and those people add it in NightMail beside their own
mailbox, read its mail and send as it.

Depends on: phase 9 (groups). Takes "shared mailboxes" from ADR 0002 OQ6.

## Step 0 findings (local stack, Stalwart 1.1.0, Keycloak 26.8.2)

- `x:Account/set create {"@type":"Group", name, domainId, description}` makes a
  group account with role `{"@type":"Default"}` and address `name@domain`.
- Adding its id to a user's `memberGroupIds` puts it in that user's JMAP session
  (`isPersonal: false`, every mail capability), with standard folders and an
  identity named after `description`; the user's own account gains a send-as
  identity for the address (named after the user).
- Mail submitted to the address lands in the group's Inbox; a member's
  `EmailSubmission` from the group account delivers as the address.
- Removing the id: `accountNotFound` on the next call, the account gone from
  the session, both identities gone.
- Filters: `{name, domainId}` finds group accounts; `{memberGroupIds: id}` lists
  members; `{"@type": "Group"}` lists groups.
- `memberGroupIds/<id>: true|null` patches one membership.
- Destroying a group with members: `objectIsLinked` naming them. Destroying a
  user who is a member: allowed.
- Keycloak: an organization subgroup can be named `support@sharpblue.org`.

## Built

- Domain: `SharedMailbox`, `NewSharedMailbox`.
- Application: ports `IEntityMailboxes` (Keycloak) and `ISharedMailAccounts`
  (Stalwart); `SharedMailboxAccess`, `SharedMailboxSync`; use cases
  `List/Get/Create/UpdateSharedMailbox`, `Add/RemoveSharedMailboxMember`,
  `RepairSharedMailbox`, `DeleteSharedMailbox`, `ListUserSharedMailboxes`;
  offboarding, reinstating and domain removal steps; `AddressAvailability`
  (shared with groups); `MailAccount.IsSharedMailbox`.
- Directory (façade): `ISharedMailboxDirectory`, used by `GetUser` after a
  directory miss on an address.
- Adapters: `KeycloakEntityMailboxes` (+ `OrganizationParentGroup`, shared with
  groups), `StalwartSharedMailboxes`, `JmapSharedMailboxDirectory`.
- Console: Entity menu → **Shared mailboxes** (list, new, detail with members,
  settings and Repair, delete page); a user's page lists their shared mailboxes.

## Acceptance

- An Acme admin creates *Support* at `support@acme.example`, adds carol: carol's
  NightMail adds the account and sees its Inbox; mail to `support@` lands there;
  she can send as it. Erin, not a member, gets 404 for the profile and the probe.
- Removing carol takes the mailbox away at once (Stalwart refuses).
- Offboarding carol takes her out; reinstating puts her back.
- A user, alias, list or second shared mailbox at `support@` is refused, naming
  the holder; removing `acme.example` is refused naming *Support*.
- Deleting *Support* needs the right to delete data, a fresh sign-in and the
  address typed; its mail is gone afterwards.
