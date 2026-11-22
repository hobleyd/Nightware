# Phase 21 — The directory: groups, manager, organization

> **Status (2026-11-22): done, local only.** Asked for: "the directory (groups,
> manager, organization)", which ADR 0006 had left out of scope; now its D11.
> Read-only Graph routes over what the console records, plus the console's
> manager field. Phase 20 is OneDrive (ADR 0008), built in parallel on its own
> branch. No tenant captures were possible: every fixture is constructed from
> the Graph reference examples and says so; `tools/capture` phase 21 replaces
> them.

## Step 0 findings (local stack, 2026-11-22)
- **Keycloak 26 drops undeclared user attributes**, so the manager needs a
  declared one: `nightware_manager` (admin-edit, user-view) in
  `deploy/keycloak/configure.sh`. Once declared, `GET /users?q=nightware_manager:<id>`
  finds the direct reports.
- **`nightware-api` can already read the console's groups.** It has
  realm-management `view-organizations` (for the people typeahead) and
  `view-users`: an organization's top-level groups, a group's children (no
  attributes, as ADR 0003 notes), one group in full, a group's members, and a
  member's groups (each with its `parentId`, so `admins` and shared mailboxes
  can be told apart).
- **Graph's shapes** (reference pages): `manager` is a user led by
  `@odata.type` (`directoryObjects/$entity`), 404 when none is set
  (`Request_ResourceNotFound`); `directReports`, `memberOf` and `members` are
  `directoryObjects` collections whose items carry their type; `/organization`
  is a collection of one, and a User.Read caller sees only `id`, `displayName`
  and `verifiedDomains`. Most `$filter`/`$search`/`$count` on these needs
  `ConsistencyLevel: eventual`.

## Done
| Gap | Version |
|---|---|
| `me`/`users/{x}` `manager`, `directReports`, `memberOf`, `transitiveMemberOf`; `groups`, `groups/{id}`, its `members` and `transitiveMembers`; `organization`, `organization/{id}`; the console's manager field (verified live) | 1.57.0 |

## Decisions
- **What a Graph group is.** One of the console's groups (ADR 0003), in the
  caller's organizations only (Graph's tenant boundary). The key is
  `mailNickname`. A list makes it mail-enabled with `SMTP:`/`smtp:` proxy
  addresses (a distribution group: `groupTypes` empty); a files facet makes it
  security-enabled (it grants access to documents). Shared mailboxes and
  `admins` are never groups. Unset Graph properties are null or empty.
- **Who is visible.** Members are a group's enabled people (as `/users` lists
  enabled people). Another user's `memberOf` holds only the groups in the
  organizations the caller shares with them. A manager is shown even when
  disabled (as a former colleague's card is); a deleted one is no manager.
- **The manager is set in the console**, on the user's page, by address or
  username: another member of the same entity, never the user, and never a
  choice that would make a loop (the chain up from the candidate is walked,
  at most 100 steps). It is stored by Keycloak id, so a changed address keeps
  it. Graph's `PUT manager/$ref` is not offered: it needs administrator
  rights, which here belong to the console.
- **Transitive forms are the same lists**: groups don't nest and hold only
  people.
- **The organization** is the caller's entity (or entities): `displayName` its
  name, `verifiedDomains` its mail domains in name order, the first the default
  and initial one (Keycloak keeps no order of its own). `tenantType` is `AAD`;
  everything Graph hides from a User.Read caller is null or empty.
- **Query options**: lists take `$select`, `$top` and `$skiptoken`;
  `$filter`, `$search`, `$orderby`, `$count` and `$expand` are 400 (Keycloak
  can't evaluate them, and Graph needs `ConsistencyLevel: eventual` for most).
  Under `$select`, items lose `@odata.type`, as every `$select`ed Nightware
  response does (ADR 0001 §14); real Graph keeps it on `directoryObjects`.
  `$expand=manager` on `/me` is not offered.
