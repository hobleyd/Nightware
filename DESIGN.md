---
name: Nightware console
description: The admin console for Nightware; one flat panel where every user's link to Keycloak, Stalwart and Nextcloud is visible.
colors:
  accent: "#2f5bd3"
  ok: "#1f7a3f"
  warn: "#9a5b00"
  bad: "#b3261e"
  text: "#1d1f23"
  muted: "#5d6470"
  line: "#dcdfe4"
  bg: "#f7f7f5"
  panel: "#ffffff"
  chip-bg: "#eef0f3"
  accent-dark: "#7ea2ff"
  ok-dark: "#5fcf86"
  warn-dark: "#e6a74b"
  bad-dark: "#ff7b72"
  text-dark: "#e6e8eb"
  muted-dark: "#9aa1ab"
  line-dark: "#33373d"
  bg-dark: "#15171a"
  panel-dark: "#1d2024"
  chip-bg-dark: "#2a2e34"
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
    textColor: "#ffffff"
    rounded: "{rounded.control}"
    padding: "6px 14px"
  button-secondary:
    backgroundColor: "transparent"
    textColor: "{colors.accent}"
    rounded: "{rounded.control}"
    padding: "6px 14px"
  button-danger:
    backgroundColor: "{colors.bad}"
    textColor: "#ffffff"
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

The console is a switchboard: one quiet panel where every line from a person to every backend is visible, and the state of each line is plain. Nothing on the board is decorative. The surface stays neutral so that the few coloured marks (a green link, an amber pending, a red conflict) are the first things the eye finds. An admin who has not visited in months should be able to read the board cold.

Density is moderate and the structure is document-like. Pages are a single centred column of headings, flat panels, tables and forms in reading order, server-rendered with no script. Buttons, inputs, checkboxes and radios are the browser's own, lightly styled, so they behave exactly as the platform expects. The theme follows the operating system: every colour has a light value and a dark value, and the dark theme is a full counterpart, not an inversion.

The system is **restrained and exact**. Restraint is the default; exactness lives in the state colours, the confirmation copy and the one-time secret box.

**Key Characteristics:**
- Flat, shadow-free surfaces separated by 1px hairlines
- System UI type at 15px, browser-default heading scale
- One accent for actions and links; three state colours for meaning only
- Automatic light and dark themes from `prefers-color-scheme`
- Tables become stacked records below 640px
- No script, no icons, no imagery

## Colors

A cool neutral field with a single blue accent and three state signals (green, amber, red). Every colour exists in a light and a dark value (frontmatter keys ending `-dark`). The dark values are lightened and desaturated so they hold contrast on the near-black panels.

### Primary
- **Switchboard Blue** (`accent`): links, primary and secondary buttons, the link-style sign-out. It means "you can act here" and nothing else.

### Status
- **Line Green** (`ok`): a linked backend, a healthy system, the success notice stripe.
- **Signal Amber** (`warn`): missing or pending links, cautionary callouts, and the dashed border of the one-time secret box.
- **Fault Red** (`bad`): conflicts, errors, unavailable systems, destructive buttons and the danger-zone border.

### Neutral
- **Ink** (`text`): all body and heading text.
- **Slate** (`muted`): secondary text, such as the signed-in identity, hints, `<small>` subtitles and disabled rows.
- **Hairline** (`line`): every border: panels, table rules, inputs, fieldsets, the header's bottom edge.
- **Paper** (`bg`): the page field behind panels, and the background of read-only DNS fields.
- **Panel White** (`panel`): panels, tables, the header bar, inputs and callouts.
- **Chip Grey** (`chip-bg`): the fill behind every state chip.

### Named Rules
**The Signal-Only Rule.** Green, amber and red appear only where they report a state or warn about a consequence. They are never decoration, section colour or emphasis.

**The One Accent Rule.** Switchboard Blue is the only colour that means "interactive". A second accent dilutes it.

**The Paired Theme Rule.** A new colour is not finished until it has its `-dark` counterpart defined in the same `prefers-color-scheme` block.

## Typography

**Body Font:** system-ui (with -apple-system, Segoe UI, sans-serif)
**Mono Font:** ui-monospace (with Menlo, Consolas, monospace)

**Character:** the operating system's own interface face. It is familiar on every machine and feels like a tool, not a brand. Monospace is reserved for things an admin copies: usernames, DNS names and values, one-time passwords.

### Hierarchy
- **Headline** (bold, 30px, 1.5): one `h1` per page naming the subject ("DNS records for example.com", a user's display name). A muted `<small>` at 0.6em may carry the subject's key identifier, such as an email address.
- **Title** (bold, 22.5px, 1.5): `h2` per panel ("Systems", "Password", "Access", "Danger zone"). In a multi-step form these are numbered ("1. Who", "2. What will happen").
- **Body** (400, 15px, 1.5): everything else, including table cells, form labels and button text.
- **Hint** (400, 13.5px, Slate): one-line explanations beside controls and under headings, such as what Repair does or what a field expects.
- **Label** (400, 12.75px): state chips only; bold when the state is a conflict or error.
- **Mono** (inherits size; 1.3em inside the secret box): copyable values.

### Named Rules
**The Plain Words Rule.** Hierarchy comes from the heading scale and the copy, not from extra weights, uppercase labels or letter-spacing. The system has two weights, regular and bold.

## Layout

A single centred column with a maximum width of 1080px and 16px padding. A full-width header bar sits above it, holding the product name, the role-gated navigation (Entities, plus Global admins for global admins) and, pushed to the far edge, the signed-in identity with the sign-out link. The header wraps onto several lines on narrow screens rather than collapsing into a menu.

The spacing steps are 6, 8, 12 and 16px. Panels sit 16px apart and have 16px inner padding. Form rows use a 12px gap. Chips use a 6px gap. Table cells have 8px padding.

Forms flow top to bottom. Related fields share a wrapping row in which each field is at least 200px wide and grows to fill the space. Every input is full width within its label. Labels wrap their controls; radio and checkbox labels put the control before the text.

**Responsive:** one breakpoint, at 640px. Below it, every table becomes a stack of records: the header row is hidden, each row is a block separated by a hairline, and each cell becomes a line. Nothing scrolls sideways.

### Named Rules
**The Reading-Order Rule.** A page reads top to bottom in the order the admin acts: what this is, its state, what you can do, and what is destructive (last).

## Elevation & Depth

The system is flat. There are no shadows anywhere. Depth is a single tonal step: Panel White panels sit on the slightly darker Paper field, outlined by a 1px Hairline. In the dark theme the same step is near-black Paper (`bg-dark`) to a lifted charcoal panel (`panel-dark`).

### Named Rules
**The Flat Board Rule.** Nothing floats. If something needs attention, it earns it through state colour or position, never a shadow or overlay.

## Shapes

The shapes are gently rounded and low-key. Controls (buttons, inputs, fieldsets) have a 6px radius, panels and the secret box 8px, and chips a full pill. Tables and the header bar are square. Borders are always 1px solid Hairline, with three deliberate exceptions that carry meaning: a red border on the danger zone, a 2px dashed amber border on the secret box, and a 4px leading stripe on callouts (see Components).

## Components

### Buttons
Native `<button>` elements, compact and unadorned.
- **Shape:** 6px radius, 6px × 14px padding, body type.
- **Primary:** Switchboard Blue fill with white text. Used for the step that commits: "Create and link", "Reinstate".
- **Secondary:** transparent with a blue outline and blue text. Used for safe or preparatory actions such as "Check what will happen", "Repair", "Save" and the password actions.
- **Danger:** Fault Red fill with white text. Used for "Offboard" and the "Delete …" links (the link styling makes them look like buttons).
- **Link:** no chrome, blue text. Used for sign-out only.
- **Hover / Focus:** browser defaults; there are no custom hover or focus styles.

### Chips (state)
- **Style:** pill, Chip Grey fill, 12.75px, no border, never wrapping.
- **State:** the text colour carries the state. Linked is green. Missing and pending are amber. Conflict and error are red and bold. Disabled is Slate. The chip's word is the backend's state as the console computed it.

### Panels
- **Corner Style:** 8px.
- **Background:** Panel White on the Paper field.
- **Border:** 1px Hairline; Fault Red for the danger zone.
- **Internal Padding:** 16px.
- **Use:** one panel per topic on a detail page (Systems, Password, Access, Danger zone). A form may itself be a panel.

### Inputs / Fields
- **Style:** full width, 1px Hairline border, 6px radius, Panel White fill, 6px × 8px padding, inherited type.
- **Read-only copy fields:** the DNS table uses read-only inputs and textareas in mono at 0.85em on Paper, so an admin can select and copy a value.
- **Fieldset:** a Hairline border at 6px radius groups radio choices under a legend ("First password").

### Callouts
- **Notice** (success, after a redirect): Panel White with a 4px green leading stripe and 8px × 12px padding. It has `role=status`.
- **Warning:** the same shape with an amber stripe. Used inline for partial results and blocked steps.
- **Errors:** the validation summary as a list with a red stripe and `role=alert`. It is hidden when there are no errors.
- The design detector flags the 4px leading stripe as a common pattern in generated UIs. It is recorded here because it exists, not endorsed. Do not spread it to new components.

### Navigation
- **Style:** a Panel White header bar with a Hairline bottom edge. The product name is bold Ink with no underline, and the nav items are plain blue links 14px apart.
- **States:** browser link defaults. The current page has no active state.
- **Mobile:** the bar wraps and nothing collapses.

### Secret Box (signature)
The one moment of emphasis in the system. A one-time credential appears in a box with a 2px dashed amber border at 8px radius, with the bolded instruction "Temporary password — shown once." above it and the value in mono at 1.3em. The value is set to `user-select: all`, so one click selects all of it. It is never shown twice and never reused for anything else.

### Plan Table (signature)
The "What will happen" step: before any write, a table lists the action the console will take in each backend. Rows that conflict turn Fault Red. The commit button only appears when no row conflicts.

## Do's and Don'ts

### Do:
- **Do** define every new colour twice: in `:root` and in the `prefers-color-scheme: dark` block.
- **Do** report state with the chip vocabulary (linked, missing, pending, conflict, error, disabled). Don't invent a new indicator.
- **Do** put destructive actions last on the page, in a panel with a red border, behind a confirming checkbox or retyped value.
- **Do** set copyable values in mono and make them easy to select.
- **Do** keep tables usable below 640px by letting them collapse into stacked records.

### Don't:
- **Don't** add shadows, gradients, overlays or modals. The board is flat and there is no script.
- **Don't** use green, amber or red for anything that is not a state or a consequence.
- **Don't** add a second accent colour.
- **Don't** spread the 4px leading-stripe callout to new components; it is under review.
- **Don't** treat today's neutral grey and generic blue as the NightMail family identity. PRODUCT.md commits the console to the NightMail family (name and the `../nightmail/assets/logo.png` icon), and that link is **not yet expressed visually**. Close the gap deliberately, for example through `colorize` or `polish`. Don't drift into it piecemeal.
