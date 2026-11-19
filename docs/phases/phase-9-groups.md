# Phase 9 — Groups (distribution lists and document access)

> **Status (2026-11-19): draft; step 0 done.** Nothing built. Decisions below
> were taken with the owner on 2026-11-19. Step 0 ran against the local stack
> the same day; its results are under "Step 0 findings" and override anything
> above them.

**Goal:** an entity administrator manages the entity's groups in the console.
A group is one list of people that can be an **e-mail distribution list**
(Stalwart), a **document-access group** (Nextcloud), or both, so the people who
receive `finance@acme.example` are the people who can open the Finance folder.

Depends on: phase 8 (merged). Builds on ADR 0002 (no store, link by key,
idempotent writes, the console is the system of record). Takes "mailing lists"
from OQ6; shared mailboxes stay in OQ6.

## Decisions

- **One object, two facets.** A *group* belongs to one entity and has members.
  It may carry:
  - a **mail facet**: an address on one of the entity's domains (plus aliases)
    → a Stalwart `x:MailingList`;
  - a **files facet** → a Nextcloud group, and optionally a Team folder
    (`groupfolders`) that only the group can see.
  Either facet can be switched on or off later; members are edited once.
- **Keycloak is the record.** A group is a Keycloak organization group of the
  entity, under a parent org group `groups` (so `admins` and any later system
  group are never listed or edited as groups). Settings live in group
  attributes (all admin-only):
  | Attribute | Meaning |
  |---|---|
  | `nightware_group_key` | immutable slug, unique within the entity (`finance`) |
  | `nightware_list_address` | primary list address, or absent (no mail facet) |
  | `nightware_list_aliases` | multivalued |
  | `nightware_external_recipients` | multivalued external addresses (mail facet only) |
  | `nightware_senders` | `members` \| `organization` \| `anyone` |
  | `nightware_files` | `true` when the files facet is on |
  | `nightware_team_folder` | `true` when a Team folder is wanted |
- **Members are entity users only; external addresses are recipients, not
  members.** Externals go to the Stalwart list's `recipients` and nowhere
  else; Nextcloud never sees them.
- **External recipients only on lists closed to outside senders** (G5,
  decided 2026-11-19). A list may hold external recipients only while its
  sender policy is `members` or `organization`: the copies Stalwart forwards
  then carry an envelope sender on one of our domains, which our SPF and DKIM
  cover, so external receivers accept them. Adding an external to an
  `anyone` list, or switching a list that has externals to `anyone`, is
  refused with the reason. External recipients cannot post to the list
  (they are not members and not in the organization).
- **Downstream keys never derive from the display name.**
  - Stalwart list: found by its address (`name` + `domainId`).
  - Nextcloud group id: `<entity-alias>-<group-key>` (Nextcloud group ids are
    global; two entities may both have `finance`). Display name is the group's
    name and may change freely.
  - Team folder: found by the folder whose group list contains exactly that
    Nextcloud group id and whose mount point is the group's name at creation;
    the folder id is stored as `nightware_team_folder_id` once created.
- **Push, not claims.** The console writes Nextcloud group membership through
  OCS at the moment of the change. `user_oidc` group provisioning from token
  claims is **not** enabled: a removal would only take effect at the member's
  next sign-in, and claim sync can strip groups it does not know (the `admin`
  group the provisioning account depends on).
- **Link chips and Repair, as for users.** Each group shows Identity / Mail /
  Groupware chips (`BackendLink`) with the existing `LinkState`s. *Repair*
  computes the difference between the Keycloak record and each backend
  (list recipients; Nextcloud group members; folder grant) and writes it.
  Drift shows as `Conflict` with both sides listed, never silently merged
  for anything other than membership (membership always follows Keycloak).
- **Who may send** is per list (`members` default). Stalwart lets anyone post
  to a list (docs), so the restriction is enforced by a server-side rule the
  console maintains (**spike**: Sieve system script at RCPT/DATA vs a spam-filter
  rule; it must reject at SMTP with a clear 5.7.1, not quarantine).
- **Documents, both ways.** With the files facet on, the Nextcloud group
  exists and users can share anything with it. With "Team folder" also on, the
  console creates a groupfolder named after the group, granted to that group
  only (read, write, share; delete per a checkbox). Advanced per-subfolder ACLs
  stay in Nextcloud's own UI (no reliable OCS API for them).
- **Address uniqueness both ways.** A list address must be in the entity's
  domains (`RequireAddressInEntity`), not reserved, and not held by any user,
  alias or other list. The new-user wizard and `SetUserAliases` refuse an
  address a list holds (`LinkStateEvaluator` already treats a non-`User`
  Stalwart holder as `Conflict`; extend it to `x:MailingList`).
- **Lifecycle.**
  - *Offboard user*: removed from every Nextcloud group of the entity and from
    every list's recipients; Keycloak group membership is **kept** so
    *Reinstate* puts them back. Offboarding plan lists the groups affected.
  - *Domain removal*: refused while a list address or alias is on the domain
    (same shape as the existing linked-object refusal).
  - *Delete group*: destroys the Stalwart list and the Nextcloud group, then
    the Keycloak group. A Team folder is **not** deleted by default (it is
    left unassigned, visible to Nextcloud admins); deleting it is a separate
    global-admin action behind fresh MFA and a typed group name, like
    `DeleteUserGroupwareData`.
- **Authorization.** Global admins: all groups. Entity admins: their own
  entity's groups. Members are chosen from the entity's users only.
- **NightMail is unchanged.** `endpoints.json` has no group surface. Mail to a
  list address is expanded by Stalwart at SMTP, so it works from any client;
  NightMail's recipient autocomplete will not suggest lists (out of scope).

## Screens

| Screen | Content | Who |
|---|---|---|
| Entity menu → **Groups** | list: name, address (or "–"), files ✓/–, team folder ✓/–, members count, chips | entity/global |
| New group | name, key (proposed from name, immutable), mail facet (local part + domain picker, aliases, senders), files facet (team folder checkbox) → plan → apply | entity/global |
| Group detail | members (add from entity users / remove), external recipients, facet switches, senders, chips with reasons, **Repair**, **Delete group** | entity/global |
| Delete team folder | what is deleted (folder, size), retype name, fresh MFA | global |
| User detail | new "Groups" section (read-only list with links) | scoped |

## File ownership (sketch)

- `src/Nightware.Domain/Administration/Groups.cs` — `Group(Id, EntityId, Key, Name, MailFacet?, FilesFacet?, Members, ExternalRecipients)`, `MailFacet(Address, Aliases, SenderPolicy)`, `FilesFacet(TeamFolder, TeamFolderId?)`, `enum SenderPolicy`.
- `src/Nightware.Application/Administration/Groups/` — ports `IEntityGroups` (Keycloak), `IMailingLists` (Stalwart), `IGroupwareGroups` (Nextcloud groups + team folders), `ISenderRestrictions` (spike decides the backend); use cases `ListGroups`, `GetGroup`, `PlanGroup`, `ApplyGroup`, `SetGroupMembers`, `SetGroupFacets`, `RepairGroup`, `DeleteGroup`, `DeleteTeamFolder`; `GroupLinkEvaluator` (pure, table-tested). `Offboarding`/`ReinstateUser`/`RemoveEntityDomain` gain the group steps above.
- `src/Nightware.Adapters.Directory/Administration/KeycloakEntityGroups.cs` (+ client calls for subgroup create/list and group attributes).
- `src/Nightware.Adapters.Jmap/Administration/StalwartMailingLists.cs` (`x:MailingList/query|get|set`), protocol records in `StalwartProtocol.cs`.
- `src/Nightware.Adapters.Dav/Administration/NextcloudGroupwareGroups.cs` (OCS `cloud/groups`, `cloud/users/{uid}/groups`, `apps/groupfolders/folders…`).
- `src/Nightware.Admin/Pages/Entities/Groups/Index|New|Detail|DeleteFolder`.
- Tests alongside each, as phase 8 (authorization matrix, WireMock per request
  shape and error, page tests per actor).

## Deploy

- `stalwart/configure.sh`: grant `sysMailingListGet|Query|Create|Update|Destroy`
  and `sysSieveSystemScriptGet|Query|Create|Update` (+ the `ReloadSettings`
  action) to `nightware-management`; create the empty `nightware-lists`
  system script and set `MtaStageRcpt.script` to run it (step 0 c).
- `nextcloud/configure.sh`: enable `groupfolders`; confirm `nightware-admin`
  (Nextcloud `admin` group) can call its OCS API.
- `keycloak/configure.sh` / `user-profile.jq`: none (group attributes are not
  part of the user profile); create the `groups` parent org group per entity
  lazily from the console, not at bootstrap.

## Ordered steps

0. **Spikes (dev stack, no src).**
   a. Keycloak 26.8: can an organization group have **subgroups** and
      **attributes** through the org REST API? If not, fall back to top-level
      org groups tagged `nightware_kind=group`.
   b. Stalwart 1.1: `x:MailingList/set` create with an external recipient;
      query by `name` + `domainId` (forum reports no `domainId` filter — page
      and match if so); delivery to internal + external recipients; an address
      that is also an account alias is refused?
   c. Stalwart sender restriction: smallest mechanism that rejects a non-member
      at SMTP per list, manageable over the management API without a restart.
   d. Nextcloud `groupfolders`: create folder, grant group with permissions,
      read back by id; behaviour when the group is deleted.
   e. OCS group membership for a `user_oidc` user that has never signed in
      (pre-provisioned) — the add must succeed.
1. Domain records + tests.
2. Application ports, evaluator, use cases + tests.
3. Keycloak adapter. 4. Stalwart adapter. 5. Nextcloud adapter.
6. Console pages (read-only), then writes.
7. Lifecycle hooks in offboarding/reinstate/domain removal.
8. Deploy scripts; walk-through on the dev stack.
9. Docs: ADR 0003 (or an ADR 0002 addendum) for the decisions above,
   `architecture.md`, runbook ("a member says they can't see the folder").

## Acceptance

- Entity admin of Acme creates **Finance** (`finance@acme.example`, files on,
  team folder on) with carol and dave plus `accountant@example.net`: mail from
  carol to the list reaches carol, dave and the external address; mail from a
  non-member is refused at SMTP; carol and dave see the *Finance* folder in
  Nextcloud, erin does not.
- Switching Finance to "anyone may send" is refused while
  `accountant@example.net` is on it; adding an external address to an
  "anyone" list is refused.
- Removing dave: his next mail to the list is refused, the folder disappears
  for him at once (no re-sign-in).
- Offboarding carol removes her from list and folder; reinstating restores both.
- Creating a user at `finance@acme.example` is refused; creating a list at an
  existing user's address is refused; an Acme admin cannot open or edit a
  Nightware group (403 from the use case).
- Deleting a domain that hosts a list address is refused and names the list.
- A list or Nextcloud group changed out of band shows drift on the chips;
  Repair restores membership.

## Step 0 findings (2026-11-19, local stack: Keycloak 26.8.2, Stalwart 1.1.0, Nextcloud + groupfolders 23.0.1)

**a. Keycloak organization groups — verified.**
- `POST /organizations/{org}/groups {name}` → 201 (`Location` holds the id);
  subgroups with `POST /organizations/{org}/groups/{parent}/children {name}` →
  201, path `/groups/finance`. Listing the parent's `children` returns them;
  `subGroupsCount=true` on the top-level list reports the count.
- **Attributes are ignored on create**; set them with
  `PUT /organizations/{org}/groups/{id} {name, attributes}` (204). A later `PUT`
  with only `{name}` (rename) **keeps** the attributes — unlike the
  organization `PUT`, which drops an absent attribute key.
- Members: `PUT …/groups/{id}/members/{userId}` (204); the member's org groups
  list shows the subgroup. Organization groups do **not** appear in the realm
  `GET /groups?search=` (they cannot be confused with realm groups).
- Not tried: adding a user who is not a member of the organization.

**b. Stalwart `x:MailingList` — verified, with two adjustments.**
- `x:MailingList/set create {name, domainId, description, recipients: {addr: true}}`
  works; `recipients` takes external addresses. Mail to the list from
  anyone is accepted and expanded at SMTP: local members got it in their
  inboxes; the external address went to the `remote` queue
  (`orcpt` = the list address).
- **`x:MailingList/query` supports neither `name` nor `domainId` filters**
  (`unsupportedFilter`). `text` works (`{"text":"finance"}`) → look up by
  `text` = local part, then confirm `emailAddress` on `get` (same pattern as
  accounts' primary-address lookup).
- **Stalwart enforces address uniqueness itself**, across accounts, account
  aliases, lists and list aliases, in every direction: `primaryKeyViolation`
  on `properties: ["email"]` with `objectId {object: Account|MailingList, id}`.
  The console still checks first (to explain the clash in the plan), but
  maps this `setError` to `Conflict` naming the holder.
- **Domain removal** is already refused by Stalwart with `objectIsLinked`
  listing `{object: "MailingList", id}` — the existing removal path just has to
  name lists in its message.
- **Risk — forwarding to externals.** The expanded copy keeps the original
  envelope sender (no SRS object exists in the 1.1 schema). External
  receivers checking SPF/DMARC may reject or junk list mail whose original
  sender has a strict DMARC policy. Resolved by G5: externals only on lists
  whose senders are internal, so the original sender is always ours.
  Bounces for externals go back to the original sender, not the list owner.

**c. Sender restriction — verified mechanism.**
- One system Sieve script, `x:SieveSystemScript` named `nightware-lists`
  (`isActive: true`), run at RCPT by setting the singleton
  `x:MtaStageRcpt.script` to `{"match":{},"else":"'nightware-lists'"}`.
  The script:
  ```sieve
  require ["envelope", "reject"];
  if allof (envelope :is "to" "finance@sharpblue.org",
            not envelope :is "from" ["bob@sharpblue.org"]) {
      reject "550 5.7.1 finance@sharpblue.org accepts mail from its members only";
  }
  ```
  gives `550 5.7.1 …` at RCPT for a non-member; members and mail to other
  addresses are unaffected. (Without a leading code Stalwart answers
  `503 5.5.3`.)
- **Changes take effect only after `x:Action/set {@type: ReloadSettings}`.**
- Design consequence: `stalwart/configure.sh` sets `MtaStageRcpt.script` once;
  the console only rewrites the script's `contents` (one `if` per restricted
  list, regenerated from Keycloak on every member/policy change) and reloads.
  The console principal needs `sysSieveSystemScript{Get,Query,Create,Update}`
  and the reload action, not MTA-stage permissions.
- Caveat: the check is on the **envelope sender**, which an outsider can
  forge. It relies on Stalwart's SPF/DMARC checks to stop spoofed local
  senders. A stricter `members`/`organization` test can use
  `authenticated_as` in the RCPT expression for mail submitted by our own
  users; settle in step 2.

**d. Nextcloud Team folders (`groupfolders` 23.0.1) — verified.**
- `occ app:install groupfolders` (add to `nextcloud/configure.sh`).
- As `nightware-admin` (Basic, `OCS-APIRequest: true`,
  `Accept: application/json`), under `/cloud/index.php/apps/groupfolders/`:
  `POST folders {mountpoint}` → `data.id`; `POST folders/{id}/groups {group}`;
  `POST folders/{id}/groups/{group} {permissions}` (15 = read, update,
  create, delete; 31 adds share); `GET folders/{id}` → `groups: {gid: perms}`.
- Members see the folder at once; **removing a member revokes access
  immediately**, even for a token issued before the removal.
- **Deleting the Nextcloud group leaves the folder and its files** with
  `groups: []` (no one but Nextcloud admins sees it). Recreating the group
  does not restore the grant; the folder must be granted again, after which
  the files are back. So "Delete group, keep folder" is safe, and Repair must
  check the folder's grant, not only the group.

**e. Nextcloud group membership for never-signed-in users — verified.**
Pre-provisioning `carol` through `user_oidc` and then
`POST /ocs/v2.php/cloud/users/carol/groups {groupid}` succeeds (status 200);
the group's user list shows her.

## Open questions

- **G1** Nested groups (a group as a member of another)? Proposed: no.
- **G2** Should group members be able to see the member list (e.g. in
  Nextcloud, or a reply-to behaviour on the list)? Proposed: default Stalwart
  behaviour, no header rewriting.
- **G3** Moderation / held messages for `anyone` lists? Proposed: out of scope.
- **G4** Expose groups to NightMail (`/groups`, recipient expansion) later —
  needs contract entries first.
- ~~**G5** External recipients and SPF/DMARC~~ — decided: externals only on
  lists whose sender policy is `members` or `organization` (see Decisions).
