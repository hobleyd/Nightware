---
name: Nightware console
description: The admin console for Nightware; one flat board under a night band, showing every user's link to sign-in, mail and files.
colors:
  accent: "#3a3db0"
  on-accent: "#ffffff"
  ok: "#1f7a3f"
  warn: "#9a5b00"
  bad: "#b3261e"
  on-bad: "#ffffff"
  text: "#191a2c"
  muted: "#565a74"
  line: "#dcdde8"
  control-line: "#82869f"
  bg: "#f5f5f9"
  panel: "#ffffff"
  chip-bg: "#eeeff5"
  selection: "#d9dbff"
  night: "#121433"
  night-text: "#eceeff"
  night-muted: "#a9add6"
  night-link: "#c3c8ff"
  accent-dark: "#aab1ff"
  on-accent-dark: "#0c0e22"
  ok-dark: "#5fcf86"
  warn-dark: "#e6a74b"
  bad-dark: "#ff8a80"
  on-bad-dark: "#0c0e22"
  text-dark: "#e8e9f6"
  muted-dark: "#a3a7c6"
  line-dark: "#2c3055"
  control-line-dark: "#6d72a0"
  bg-dark: "#0c0e22"
  panel-dark: "#161935"
  chip-bg-dark: "#22264a"
  selection-dark: "#34397a"
  night-dark: "#181c56"
typography:
  headline:
    fontFamily: "system-ui, -apple-system, Segoe UI, sans-serif"
    fontSize: "30px"
    fontWeight: 700
    lineHeight: 1.5
  title:
    fontFamily: "system-ui, -apple-system, Segoe UI, sans-serif"
    fontSize: "22.5px"
    fontWeight: 700
    lineHeight: 1.5
  body:
    fontFamily: "system-ui, -apple-system, Segoe UI, sans-serif"
    fontSize: "15px"
    fontWeight: 400
    lineHeight: 1.5
  hint:
    fontFamily: "system-ui, -apple-system, Segoe UI, sans-serif"
    fontSize: "13.5px"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "system-ui, -apple-system, Segoe UI, sans-serif"
    fontSize: "12.75px"
    fontWeight: 400
    lineHeight: 1.5
  mono:
    fontFamily: "ui-monospace, Menlo, Consolas, monospace"
    fontSize: "15px"
    fontWeight: 400
rounded:
  control: "6px"
  panel: "8px"
  pill: "999px"
spacing:
  xs: "6px"
  sm: "8px"
  md: "12px"
  lg: "16px"
components:
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.on-accent}"
    rounded: "{rounded.control}"
    padding: "6px 14px"
  button-secondary:
    backgroundColor: "transparent"
    textColor: "{colors.accent}"
    rounded: "{rounded.control}"
    padding: "6px 14px"
  button-danger:
    backgroundColor: "{colors.bad}"
    textColor: "{colors.on-bad}"
    rounded: "{rounded.control}"
    padding: "6px 14px"
  button-link:
    backgroundColor: "transparent"
    textColor: "{colors.accent}"
    padding: "0"
  input:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.text}"
    rounded: "{rounded.control}"
    padding: "6px 8px"
  panel:
    backgroundColor: "{colors.panel}"
    rounded: "{rounded.panel}"
    padding: "16px"
  callout:
    backgroundColor: "{colors.panel}"
    rounded: "{rounded.panel}"
    padding: "8px 12px"
  night-band:
    backgroundColor: "{colors.night}"
    textColor: "{colors.night-text}"
    padding: "10px 16px"
  chip:
    backgroundColor: "{colors.chip-bg}"
    rounded: "{rounded.pill}"
    padding: "1px 8px"
    typography: "{typography.label}"
  chip-linked:
    backgroundColor: "{colors.chip-bg}"
    textColor: "{colors.ok}"
  chip-pending:
    backgroundColor: "{colors.chip-bg}"
    textColor: "{colors.warn}"
  chip-conflict:
    backgroundColor: "{colors.chip-bg}"
    textColor: "{colors.bad}"
  chip-disabled:
    backgroundColor: "{colors.chip-bg}"
    textColor: "{colors.muted}"
---

# Design System: Nightware console

## Overview

**Creative North Star: "The Switchboard"**

The console is a switchboard under a night sky. A band of NightMail night runs across the top: the app's moon-and-envelope mark, the product name, navigation and the find-a-user search. Below it is a quiet board where every line from a person to every system is visible, and the state of each line is plain. The surface stays neutral so that the few coloured marks (a green link, an amber pending, a red conflict) are the first things the eye finds. An admin who has not visited in months should be able to read the board cold.

Density is moderate and the structure is document-like. Pages are a single centred column with a breadcrumb, then headings, flat panels, tables and forms in the order the admin acts. Everything is server-rendered with no script, and the controls are the browser's own, styled from the palette. The theme follows the operating system. The dark theme is built on the night sky, not an inversion of the light one.

The system is **restrained and exact**. The brand lives in the night band and the envelope indigo; exactness lives in the state colours, the confirmation copy and the one-time secret box.

**Key Characteristics:**
- A NightMail night band with the app's mark, in both themes
- Flat, shadow-free panels separated by 1px hairlines
- System UI type at 15px, browser-default heading scale, two weights
- One indigo accent for actions and links; three state colours for meaning only
- Automatic light and dark themes from `prefers-color-scheme`
- Tables become labelled, stacked records below 640px
- No script; the only imagery is the family mark

## Colors

A cool, faintly indigo neutral field under a night band, with a single envelope-indigo accent and three state signals. Every colour exists in a light and a dark value (frontmatter keys ending `-dark`). Filled buttons carry their own text colours (`on-accent`, `on-bad`): white on the light theme's deep fills, dark ink on the dark theme's light fills.

### Primary
- **Envelope Indigo** (`accent`): links, primary and secondary buttons, focus rings. Taken from the envelope in NightMail's icon (`../nightmail/assets/logo.png`). It means "you can act here" and nothing else.

### Night band
- **Night** (`night`): the header band. In the dark theme it lifts to the envelope's own indigo (`night-dark`) so the band stays distinct from the page.
- **Starlight** (`night-text`), **Dusk** (`night-muted`), **Moonlit Link** (`night-link`): text, secondary text and links on the band.

### Status
- **Line Green** (`ok`): a linked system, a healthy system, the success notice outline.
- **Signal Amber** (`warn`): missing or pending links, cautionary callouts, the dashed border of the one-time secret box.
- **Fault Red** (`bad`): conflicts, errors, unavailable systems, destructive buttons, invalid fields, the danger-zone border.

### Neutral
- **Ink** (`text`): all body and heading text.
- **Slate** (`muted`): secondary text: hints, breadcrumbs, `<small>` subtitles, disabled rows, stacked-cell labels.
- **Hairline** (`line`): dividers: panels, table rules, fieldsets.
- **Control Edge** (`control-line`): the border of inputs, selects and textareas (at least 3:1 against panel and page).
- **Paper** (`bg`): the page field, and the background of read-only DNS fields.
- **Panel White** (`panel`): panels, tables, inputs and callouts.
- **Chip Grey** (`chip-bg`): the fill behind every state chip.
- **Selection** (`selection`): selected text.

### Named Rules
**The Signal-Only Rule.** Green, amber and red appear only where they report a state or warn about a consequence. They are never decoration, section colour or emphasis.

**The One Accent Rule.** Envelope Indigo is the only colour that means "interactive". A second accent dilutes it.

**The Paired Theme Rule.** A new colour is not finished until its `-dark` counterpart exists in the `prefers-color-scheme` block and both pass contrast.

**The Family Rule.** The NightMail link is the night band, the mark and the indigo. It does not spread into page backgrounds, illustrations or gradients.

## Typography

**Body Font:** system-ui (with -apple-system, Segoe UI, sans-serif)
**Mono Font:** ui-monospace (with Menlo, Consolas, monospace)

**Character:** the operating system's own interface face. It is familiar on every machine and feels like a tool, not a brand. Monospace is reserved for things an admin copies: usernames, DNS names and values, one-time passwords.

### Hierarchy
- **Headline** (bold, 30px, 1.5): one `h1` per page naming the subject. A muted `<small>` at 0.6em may carry the subject's key identifier.
- **Title** (bold, 22.5px, 1.5): `h2` per panel. In a multi-step form these are numbered ("1. Who", "2. What will happen"), because the order is the information.
- **Body** (400, 15px, 1.5): everything else.
- **Hint** (400, 13.5px, Slate): persistent explanations beside controls and inline backend detail. Guidance never lives only in a placeholder.
- **Label** (400, 12.75px): state chips only; bold when the state is a conflict or error.
- **Mono** (inherits size; 1.3em inside the secret box): copyable values.

### Named Rules
**The Plain Words Rule.** Hierarchy comes from the heading scale and the copy, not from extra weights, uppercase labels or letter-spacing. The system has two weights, regular (400) and bold (700).

**The One Name Rule.** Each system has one name everywhere, function first and product second: Sign-in (Keycloak), Mail (Stalwart), Files & calendar (Nextcloud). Chips use the short form (Sign-in, Mail, Files & calendar). The names live in `Pages/Systems.cs`. Text beside a system's name does not repeat its product ("No account for eve.", not "No Nextcloud user eve."). A failure leads with a plain sentence ("Mail gave no answer for this account.") and puts the system's own reason, host and port included, after it as a muted hint; entity admins also read "If this persists, tell your Nightware operator." where only the operator can act.

## Layout

A single centred column, at most 1080px wide with 16px padding, under a full-width night band. The band holds the mark and product name, the role-gated navigation (Entities, plus Global admins for global admins; the current section is marked with `aria-current`), the find-a-user search, and, pushed to the far edge, the signed-in identity, Change password and Sign out. On narrow screens the band wraps onto several lines rather than collapsing into a menu. A skip link leads past it to the content.

Entity, user, DNS and delete pages open with a breadcrumb that starts at Entities (Entities › Entity › Users › Person) above the h1; a global administrator in no entity is reached from Global admins. Dashboard Systems chips use the short names. The notice and errors for a request sit directly under the h1 (the `_Messages` partial), so they are read with the page they concern. The dashboard reads top-down as a worklist: Needs attention, then Systems, then Entities.

The spacing steps are 6, 8, 12 and 16px. Panels sit 16px apart with 16px inner padding, and a panel's first heading sits flush to its top. Form rows use a 12px gap; chips use a 6px gap; table cells have 8px padding.

Forms flow top to bottom. Related fields share a wrapping row in which each field is at least 200px wide. Labels wrap their controls; radio and checkbox labels put the control first.

**Responsive:** one breakpoint, at 640px. Below it every table becomes a stack of records: the header row is hidden, each row is a block separated by a hairline, and each cell is a line prefixed with its column label (`data-label`). Nothing scrolls sideways.

### Named Rules
**The Reading-Order Rule.** A page reads top to bottom in the order the admin acts: where you are, what this is, its state, what you can do, and what is destructive (last). On a user's page Access comes straight after Systems, so offboarding on a phone is one screen away. A domain's DNS page shows steps only while there is something to do: "Decide SPF and DMARC" while the domain has not chosen both records of its own (or on Edit, with Cancel), then "Publish to <host>" (Cloudflare, or the entity's other DNS host) while the zone differs; they are numbered only when both appear, decision first. In the decision, each record reads as one row: "Now:" with the record in use and the same weakness chips as the table, the field, and any suggestion; "Leave a field blank to keep the mail server's record" is said once, and Save is primary when deciding is the only step. While a decision is open, the table below is headed "In use now (before you save)". Otherwise the page is "Records for <domain>", read-only, with a status line (Published at Cloudflare, or publish them at your DNS provider) and "Edit SPF and DMARC". A weak SPF or DMARC record is marked where it is read: an amber "Weak" chip (red "Unsafe" for an SPF that lets anyone send) before each reason, in plain words, under the value, with an Edit link on the row. The DNS page uses the window's full width (`main.wide`), and its record tables give the value columns the space (fixed columns, values sized to show whole). Explanations a returning operator no longer needs fold into a "How … work" summary.

## Elevation & Depth

The system is flat. There are no shadows anywhere. Depth is a single tonal step: Panel White panels sit on the slightly darker Paper field, outlined by a 1px Hairline; in the dark theme the same step runs from the night-sky page (`bg-dark`) to a lifted indigo panel (`panel-dark`). The night band is the only full-colour region; the browser bar follows it (`theme-color` per scheme).

### Named Rules
**The Flat Board Rule.** Nothing floats. If something needs attention, it earns it through state colour or position, never a shadow or overlay.

## Shapes

Gently rounded and low-key. Controls (buttons, inputs, fieldsets) have a 6px radius; panels, callouts, the secret box and the mark have 8px or 6px; chips are full pills. Tables and the night band are square. Borders are 1px: Hairline for dividers, Control Edge for controls, and a state colour for callouts and the danger zone. The one heavier border is the secret box's 2px dashed amber.

## Components

### Buttons
Native `<button>` elements, compact and unadorned.
- **Shape:** 6px radius, 6px × 14px padding, body type.
- **Primary:** Envelope Indigo fill. A page has at most one, and it is the page's main or status-driven action: "Create and link" on the new-user plan, "Create entity", "Publish N changes" on the DNS page, "Add" on an entity that has no domain yet, and on a user's page the one action their status calls for ("Repair" when they need repair, "Finish offboarding" when an offboarding is incomplete, also on a refused Delete page). A commit local to one panel of a larger page is secondary.
- **Secondary:** transparent with an indigo outline. Used for safe, repeatable or preparatory actions and for rare, powerful ones that must not lead the page: "Check what will happen", "Repair" and "Finish offboarding" on dashboard rows and when not the status action, "Save", the password actions, "Reinstate", "Make global administrator", "Remove".
- **Danger:** Fault Red. Filled for the "Delete …" links, whose page is the deletion; a red outline (`danger quiet`) for "Offboard", which sits on routine pages and must not be their loudest mark.
- **Link:** no chrome, link colour. Used for Sign out only.
- **Focus:** a 2px outline in the accent (Moonlit Link on the night band), 2px offset.

### Chips (state)
- **Style:** pill, Chip Grey fill, 12.75px, no border, never wrapping.
- **State:** the text colour carries the state and the word says it: linked green, missing and pending amber, conflict and error red and bold, disabled Slate. An error link reads "Failed" (the system gave no usable answer for this account), never "Unreachable", which belongs to the dashboard's health check alone; on the worklist its reason follows in a hint. Signing keys on the entity page are a chip too. For an offboarded user, an account that does not exist reads "None" in Slate, not amber "Missing": nothing there can let them in, so it asks for nothing (`Systems.LinkLabel`). Detail for a conflict or error appears inline beneath the chips as a hint, never only in a tooltip.

### Status chip
- One status per user, derived once (`UserStatuses.Of`): Active (green), Needs repair (amber), Conflict and Offboarding incomplete (red, bold), Offboarded (Slate). The same chip and words sit beside the user's name, in an entity's user list, in Find and on the dashboard. Labels live in `Systems.StatusLabel`.

### Panels
- **Corner Style:** 8px. **Background:** Panel White. **Border:** 1px Hairline, Fault Red for the danger zone. **Padding:** 16px.
- **Use:** one panel per topic, on every page including the dashboard; a list that is a page's whole subject still sits in one (labelled, without a repeated heading).
- **Folded:** rare changes on a user's page (other addresses, administrator rights) share one `details.more` panel whose summary names what is inside; it opens itself when a post there fails.
- **Systems:** below the chips, global admins get one line of plain links to each configured system's own admin console (full names, ` · ` between, new tab), with a muted hint that each signs in separately. Entity admins never see it.
- **Incomplete offboarding:** the Access panel takes the Fault Red border itself, matching its red status chip; no callout card is nested inside a panel. The Danger zone is left out until the offboarding is complete, so one red panel carries the alarm and its fix.

### Callouts
- **Notice** (success after a redirect, `role=status`), **Warning** (partial results, blocked steps, an incomplete offboarding) and **Errors** (the validation summary, `role=alert`): Panel White with a full 1px border in the state colour, 8px radius, 8px × 12px padding. No side stripe.
- **Could not read** (`_Unread`): when a page's own data cannot be had, the page shows its heading and one warning, and nothing else. It keeps a breadcrumb and a heading that names what the page is (pages set `ViewData["Title"]` and `ViewData["Crumbs"]` before the check). A system that did not answer reads "Could not read this page's information" with "Try again" and the dashboard; a refusal (validation, conflict, not found) states its reason with "Nothing was changed" and a way back, and offers the fix it names when the page can (Finish offboarding on a refused Delete). No empty table or "none yet" ever stands in for either.
- An invalid field also takes a Fault Red border.

### Inputs / Fields
- **Style:** full width, 1px Control Edge border, 6px radius, Panel White fill, 6px × 8px padding, inherited type.
- **Read-only copy fields:** the DNS table uses read-only inputs and textareas in mono at 0.85em on Paper.
- **Fieldset:** groups radio choices under a legend that reads as the group's label; no border, so it never draws a box inside its panel.
- **Rows:** a button in a form row lines up with the bottom of the field beside it (labels in a row drop their bottom margin).
- **Read-only copy fields** carry a label naming their record ("MX record name", "TXT record value at _dmarc.example.com"), since every row has the same columns.

### Night Band (navigation)
- Night fill; the 28px mark with a 6px radius; the product name in bold Starlight; nav links in Moonlit Link, the current one in Starlight with a 2px underline.
- **Find a user:** a compact transparent search field with a Dusk outline and a visually hidden label. One match opens the user; several are listed with their entity.

### Confirmed changes
- Every change to who can get in names its consequence in a checkbox beside the button: offboard, reinstate, grant or remove global rights, grant or remove an entity administrator. Reopening access (reinstate, granting global rights) also asks for a fresh sign-in. A new SPF record for a domain, which decides who may send its mail, is reviewed before it is saved: Save shows a "Use this SPF record?" panel with the exact record posted, its weaknesses, and a box whose words are worked out from that record ("Only 81.2.69.160 and whatever mailer.example lists may send mail for acme.example; other senders only soft-fail…"), with a primary "Use this SPF record". Saving with SPF unchanged or blank needs no review. So is publishing a Cloudflare plan that removes another server's MX record, which moves all of the domain's mail here ("All mail for acme.example comes here: mail.oldhost.example stops receiving it").
- Reversals and removals live in their own block under a hairline with a small heading ("Undo offboarding", "Remove an administrator"), never in a table row beside a routine link, and use the secondary button.
- Removing a domain is the one exception: a red ✕ at the right end of its row (only for a domain no user's address is on) opens a confirmation in the browser's own popover — plain HTML (`popovertarget`), so the CSP that forbids every script cannot switch it off. It names the domain and the entity, offers "Also remove its mail records from Cloudflare" when the entity has a token (only records matching what the console publishes; if that clean-up fails, the domain stays), and has a red "Remove domain" and a Cancel. It is the console's only overlay.

### Needs attention (signature)
The dashboard's first panel: users whose offboarding is incomplete, who conflict or who need repair (most urgent first, each with the systems at fault), domains the mail server does not serve or that lack signing keys, shared mailboxes a system is missing, out of step with or could not answer for (a calendar member still waiting to sign in to Nextcloud is not), and any entity that could not be checked. Empty, it says "Nothing needs you." It is read on each visit and never stored.

A domain whose own SPF or DMARC lets anyone send as it (Unsafe) is listed too, with "Fix SPF and DMARC" to the edit form; merely Weak records are shown where they are read, not here. Each row carries its fix when the fix is safe to repeat: "Finish offboarding" (only for someone already offboarded; the dashboard never offboards an active user), "Repair" for a user, a shared mailbox (entity admins too, as on its own page) or, for global admins, a domain. A conflict needs a person to decide, so its row links to the user instead. A domain lacking keys links to its DNS records.

The Systems chips below agree with it: a system that answers its health check but failed for someone in the sweep reads "Reachable, failed for N" in amber, with the names beneath, never a plain "OK".

### Access history
At the foot of a user's Access panel, newest first and at most ten: when (UTC, in Slate, tabular numerals), what, by whom, and what each system said straight after ("18 Nov 2026, 09:12 UTC · Offboarded by Ops Admin: Sign-in: shut, Mail: failed, Files & calendar: none"; the admin by display name). It is kept on the user in Keycloak (`nightware_access_history`), never in the console, and shown only when there is something to show.

### Secret Box (signature)
The one moment of emphasis. A one-time credential appears in a box with a 2px dashed amber border at 8px radius, under the bolded instruction "Temporary password — shown once." and the advice to deliver it by another channel. The value is mono at 1.3em with `user-select: all`. It is never shown twice.

### Plan Table (signature)
The "What will happen" step: before any write, a table lists each system's action. Conflicting rows turn Fault Red, the commit button only appears when nothing conflicts, and the warning says how to resolve a conflict.

The DNS page's "Publish to Cloudflare" panel is the same idea for a zone. It lists only the records that would change (Add, Correct, Remove duplicate, Not published with its reason), with the value it will write beside the one Cloudflare holds now, and counts the records already right in one sentence. Records that stay but still matter (another provider's MX) are amber callouts below. The button names how many changes it makes, and publishes only the plan on screen. If the zone moved in the meantime, the new plan is shown instead.

### Write-only secrets
A credential the console keeps (an entity's DNS host token or key) is entered in a password field with `autocomplete="off"` and never rendered back, not even after a failed save. The panel states only whether one is set ("Connected to Cloudflare"), offers the one-token hosts in a plain select with the connected one selected (so replacing a token never changes the host unasked) and how to create a credential at each, gives Azure DNS (five fields) and Google Cloud DNS (a JSON key) folded forms of their own, the connected one's open, and puts the removal in its own confirmed block.

## Do's and Don'ts

### Do:
- **Do** define every new colour twice and check its contrast in both themes.
- **Do** report state with the chip vocabulary (linked, missing, pending, conflict, error, disabled), with the detail inline.
- **Do** name systems through `Systems.Name` / `Systems.ShortName`.
- **Do** put destructive actions last on the page, behind a confirming checkbox, a retyped value or a fresh sign-in.
- **Do** give every table cell a `data-label` so stacked rows stay readable on a phone.
- **Do** set copyable values in mono and make them easy to select.
- **Do** show a user's state with the status chip, in the same words on every page.
- **Do** put a confirming checkbox that names the consequence beside any change to who can get in.
- **Do** offer only the choices that can succeed (only removable domains, granting only to someone who can sign in), and say why when there are none.
- **Do** word a next step for the reader's role: an entity admin is sent to their operator, not to a button they lack.

### Don't:
- **Don't** add shadows, gradients, overlays or modals (the domain-removal popover is the single exception). The board is flat and there is no script; nothing may rely on one.
- **Don't** use green, amber or red for anything that is not a state or a consequence.
- **Don't** add a second accent colour, or spread the night palette beyond the band.
- **Don't** use a thick side stripe on callouts or panels.
- **Don't** put information only in a `title` tooltip or a placeholder.
- **Don't** use a weight other than 400 or 700.
- **Don't** let an empty list read as "all clear" when something could not be checked; name what was not checked.
