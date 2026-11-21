# ADR 0004 — Shared mailboxes

Status: accepted (2026-11-19). Builds on ADR 0002 (the admin console) and ADR 0003
(groups); takes "shared mailboxes" from ADR 0002 OQ6. Plan and step 0 findings:
[`docs/phases/phase-10-shared-mailboxes.md`](../phases/phase-10-shared-mailboxes.md).

## Context

A shared mailbox is one inbox (`support@`, `accounts@`) with its own address,
folders and sent items that several people open beside their own. NightMail
already supports it as Graph does: the signed-in user adds the mailbox by
address and the client calls `/users/{address}/...` with the user's own token
(`docs/contract/endpoints.md`, "mailbox base"). Delegated access is the
backend's decision (ADR 0001 §19). Until now nothing could create one, and
`/users/{shared}` itself was a 404, which NightMail's account setup treats as an
error.

## Decisions

### D1 — A separate object, not a group facet
ADR 0003 D1 kept shared mailboxes apart from groups: a group *forwards* copies
to its members' own inboxes, a shared mailbox is *one* inbox they open. They
have separate pages ("Shared mailboxes" under the entity) and separate records,
built the same way as groups.

### D2 — Keycloak is the record
A shared mailbox is a subgroup of the organization group **`mailboxes`** of its
entity (beside `groups`, created on first use), so neither is ever read as the
other. The subgroup's **name is the mailbox's address** (immutable; Keycloak
26.8 accepts `@` and `.` in a subgroup name and refuses a second one). Its
members are the people who may open it. Attributes (admin-only):

| Attribute | Meaning |
|---|---|
| `nightware_name` | display name |
| `nightware_mailbox_aliases` | multivalued |

Rejected: Stalwart as the record. It needs no Repair, but loses what the record
gives groups: an offboarded member keeps their membership so reinstating
restores it (ADR 0003 D8), and entity scope comes from the organization.

### D3 — A Stalwart group account, membership pushed
On the mail server a shared mailbox is an `x:Account` of type **`Group`**
(`description` = the name). A user account opens it while the group's id is in
its `memberGroupIds`. Verified on Stalwart 1.1.0:

- the group appears in the member's JMAP session as a non-personal account
  named by its address, with its own Inbox/Drafts/Sent/Deleted/Junk and an
  identity sending as it; the member's own account also gains a send-as
  identity for the address;
- removing the id takes the account **and** both identities away at once;
- mail to the address (and its aliases) is delivered to the group's Inbox;
- `x:Account/query {name, domainId}` finds group accounts, and
  `{memberGroupIds: id}` lists a group's members;
- membership is patched one id at a time (`memberGroupIds/<id>`), so a user's
  other shared mailboxes are untouched;
- a group with members cannot be destroyed (`objectIsLinked`).

`SharedMailboxSync` pushes the record: the enabled members' own mailboxes
(found by address) are the members; anything else, including members added by
hand in Stalwart, is taken out. A member without a mailbox of their own yet is
waiting, not out of step. A group account with a role or permissions of its own
(not the `Default` role it is created with) is never given members: they would
gain them.

### D4 — The façade answers the profile
`GET /users/{address}` (NightMail's account setup and signature prefill through
the mailbox base) falls back, when the directory has no such user, to the
caller's JMAP session (`ISharedMailboxDirectory`): an account the caller may
open at that address is described by its address and its identity's name.
Anyone else still gets the directory's 404, so the profile is shown to members
only. The address itself is not secret: it is for mailing, like a user's, so
`/people` suggests every shared mailbox of the caller's organizations to everyone
in them, read from the Keycloak record (named by the address and titled by
`nightware_name`) as the `nightware-api` service account (`view-organizations`).
Every other `/users/{shared}/...` route already worked through
`JmapAccountResolver`; nothing else changed in the façade.

### D5 — Lifecycle
- Offboarding takes the user out of every shared mailbox (membership kept in
  the record); reinstating puts them back; a failure fails the step, as for
  groups.
- Removing a domain is refused while a shared mailbox has an address on it,
  naming it.
- Addresses stay unique everywhere (ADR 0003 D7): the user wizard, aliases,
  groups and shared mailboxes refuse each other's addresses, naming the holder.
- **Deleting a shared mailbox deletes its mail**, so it is a data deletion:
  global admins (or entity admins where `EntityAdminsMayDeleteData` is on), a
  fresh strong sign-in, and the address typed again. The adapter takes every
  member out first, then destroys the account; the record goes last.

### D6 — A calendar of its own (added 2026-11-21, phase 17)
A shared mailbox has a calendar in Nextcloud. Nextcloud keeps calendars for its
own users only, so the mailbox gets one: a local Nextcloud user
**`mbx-{address}`**, carrying the mailbox's address as its email (the façade's
`principal-property-search` finds it, as for any person) and its name, with a
random password nobody keeps. Its `personal` calendar is shared read-write
with a Nextcloud group of the same name holding the enabled members' Nextcloud
uids (their usernames), so membership changes touch only the group.
- `SharedMailboxSync` creates and repairs the group, its members and the
  calendar (`ISharedCalendars`) with the mail; Inspect reports it as the
  Groupware link. A member who has never signed in to Nextcloud is waiting.
- Only a calendar's owner can share it, so each repair sets the user a fresh
  random password over OCS and acts as it once over WebDAV; nothing is stored.
  A new account's sample event is removed. The user is created without an
  email and given it afterwards: created with one, Nextcloud mails a welcome
  to it (the shared mailbox's inbox). A password reset sends nothing.
- The share must name the group URL-encoded (`principals/groups/mbx-a%40b`):
  Nextcloud accepts the raw `@` and grants nothing by it.
- Deleting the mailbox deletes its Nextcloud user (and so the calendar and
  events) and the group, before the record.
- Through the façade, `/users/{shared}/calendars`, `calendarView` and `events`
  work for members with no façade change (calendars shared with the caller,
  owned by the mailbox's principal). An event a member creates there with
  attendees has the mailbox as ORGANIZER, and Nextcloud invites the attendees
  as it ("{name} via Nextcloud").

## Consequences
- No deploy change: the console's Stalwart role already holds `sysAccount*`.
- A new member sees the mailbox in NightMail within the façade's session cache
  (`Jmap:SessionCacheDuration`, 5 minutes) or at their next token; a removed
  member is refused by Stalwart at once.
- Renaming changes the record and `description` only: identities keep the name
  they were created with (the console cannot write another account's
  identities), and a member's own send-as identity carries the member's name.
- A shared mailbox has a calendar (D6) but no contacts or files in Nextcloud;
  those routes answer as for any address Nextcloud does not know.

## Verified
Against the local stack on 2026-11-19 (Keycloak 26.8.2, Stalwart 1.1.0): step 0
in the phase plan, and
`tests/Nightware.Integration.Tests/Administration/SharedMailboxesLiveTests.cs`:
create, add alice, NightMail's profile and inbox probe through the in-process
façade with alice's token, sending as the mailbox (it arrives from its
address), its other users-form routes answering without a server error,
remove → 404, delete with a member. Every Stalwart call the adapter makes was
also run as a principal holding only the console's custom role ("Nightware
admin console" + User, as `configure.sh` sets it when global admins do not
administer the servers): no permission beyond `sysAccount*` is needed.
