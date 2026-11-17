# Product

<!-- impeccable:product-schema 1 -->

Scope: this record covers the Nightware admin console (`src/Nightware.Admin`),
the only human-facing surface in this repository. The Graph façade
(`Nightware.Api`) has no UI; its clients are NightMail installs.

## Platform

web

## Users

- **Primary: the operator.** A technical person (or small IT team) who hosts
  Nightware for several client organisations and acts as global admin. They
  create entities, attach mail domains and get DNS right, add the first users,
  and fix link conflicts between Keycloak, Stalwart and Nextcloud.
- **Secondary: entity admins** at client organisations, scoped to their own
  entity. They are less technical than the operator, and the console must make
  sense to them without the operator explaining the backends.

## Product Purpose

The console lets an administrator manage entities (organisations owning mail
domains) and their users without ever opening Keycloak's, Stalwart's or
Nextcloud's own admin consoles. It links accounts that already exist in those
backends and creates the ones that do not, so a person reliably exists in all
three systems with matching keys.

Success: an entity goes from nothing to working mail, calendar and files for
its people in one sitting. Offboarding someone shuts off every sign-in to their
mailbox, and the admin can be sure it did.

## Positioning

Each self-hosted mail stack ships its own admin UI, so an admin has to work
across three of them. The console is one place that understands how the three
fit together. It shows each user's link state across Keycloak, Stalwart and
Nextcloud by matching keys at read time, and every action is an idempotent
"make sure this exists and matches", so running it twice is safe. It sits in
front of the backends, which remain the systems of record.

## Operating Context

- **Cadence: heavy during setup, then rare.** Most use happens when an entity
  is set up (domains, DNS records, initial users). After that, visits are
  sporadic and task-driven: one onboarding, one offboarding, one fix. Admins
  return having forgotten the details, so each screen must explain itself.
- Admins sign in with OIDC (Keycloak client `nightware-admin`) and must pass
  MFA. Sensitive actions require them to sign in again (`Account/Reauthenticate`).
- **Devices:** desktop for setup work. Phones and tablets must be able to
  complete urgent actions, offboarding above all.
- The console is served at `https://${PUBLIC_HOST}/console/` alongside the rest
  of the self-hosted stack (`deploy/`).

## Capabilities and Constraints

- Areas: dashboard with backend health (Keycloak, Stalwart, Nextcloud); entities
  (list, detail, domains, DNS guidance); users (list, new, detail, link state
  per backend, offboard/delete); global admins (global admins only).
- Roles: global admin (realm role `nightware-global-admin`); entity admin
  (member of the organization group `admins`). Scope is re-checked against
  Keycloak on every request, so a revoked role stops working within about a
  minute.
- **No data of record.** Keycloak is the system of record (entity = Keycloak
  Organization). Link states are computed on every read and never stored.
  Writes are idempotent and are not rolled back. A partial result is a real
  state the UI must show honestly.
- **Current implementation:** server-rendered Razor Pages. The CSP forbids
  scripts. One stylesheet, `wwwroot/console.css`. Decisions:
  `docs/adr/0002-admin-console.md`, `docs/phases/phase-8-admin.md`.
- **Terminology:** entity (never "tenant" or "organisation" in the UI), domain,
  user, link / linked / pending / conflict, offboard, global admin, entity
  admin.

## Brand Commitments

- The console belongs to the NightMail / Nightware family and should read as
  part of it. The product name is "Nightware console".
- NightMail's existing brand asset is the app icon at `../nightmail/assets/logo.png`
  (a moon rising out of an open envelope against a night sky). Use the family
  name and assets; do not invent a separate identity.

## Evidence on Hand

- Live walk-through scripts: `deploy/scripts/console-e2e/`.
- No user research, testimonials or usage metrics exist. Do not fabricate any.

## Product Principles

1. **Tell the truth about state.** Show partial, pending and conflicting link
   states plainly. Never paper over a backend that failed or disagrees.
2. **Re-entry over speed.** Sporadic admins must be able to orient instantly:
   say where they are, what is wrong and what to do next, without relying on
   memory.
3. **Destructive actions are deliberate and conclusive.** Offboarding and
   deletion are hard to undo by accident, and they confirm exactly what was
   shut off.
4. **One place, no backend tourism.** If an admin has to open Keycloak,
   Stalwart or Nextcloud to finish a task, the console has failed.

## Accessibility & Inclusion

- Usable on phone and tablet widths, at minimum for offboarding and checking a
  user's state.
- No formal conformance target has been set yet.
