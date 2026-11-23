# Phase 25 — Presence over Nextcloud user status

> **Status (2026-11-23): planned.** Independent of phases 23 and 24. Decisions
> in ADR 0009 D6; Step 0 in phase 23.

## Operations
`docs/contract/endpoints.json`, phase 25 (8 operations): presence get (`/me`,
`/users/{id}`, `/communications/presences/{id}`), `getPresencesByUserId`,
`setPresence`, `clearPresence`, `setUserPreferredPresence`,
`clearUserPreferredPresence`, `setStatusMessage`.

## Build notes
- Feature folder `Presence/` in every project; the user status client in
  `Adapters.Dav/Presence/`.
- uid ↔ id through phase 23's `IChatUserIds` port (merge order: if phase 23 has
  not merged, mock the port and wire it after).
- `outOfOfficeSettings` comes from the mailbox settings reader (auto-replies).
- Setting presence for another user is 403.
- Step 0 left: when Talk reverts "In a call" after a call (it still read busy
  right after leaving); Nextcloud's automatic `away`; what a user who never set
  a status returns from `statuses/{uid}`; whether `clearAt` applies to a
  predefined message.

## Acceptance
- bob sets DoNotDisturb with a message over Graph; alice reads it through all
  three presence routes; Nextcloud's own status menu shows the same.
- In a Talk call, presence reads Busy / InACall.
