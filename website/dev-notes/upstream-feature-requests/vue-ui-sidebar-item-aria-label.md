# @cocoar/vue-ui — CoarSidebarItem: aria-label on the root menuitem

**Repo:** `@cocoar/vue-ui`
**Component:** `CoarSidebarItem`
**Reported by:** Cocoar.Auth onboarding (2026-05-11) — an integrator
trying to drive the admin UI via browser-automation could not address
the collapsed sidebar items because the icon-only buttons have neither
a `title` nor an `aria-label`.

## Problem

When the sidebar is collapsed, `CoarSidebarItem` renders as

```html
<div role="menuitem" class="coar-sidebar-item coar-sidebar-item--collapsed" tabindex="0">
  <span class="…">
    <CoarIcon name="layout-dashboard" />
  </span>
  <span class="…">Dashboard</span>  <!-- hidden by CSS in collapsed mode -->
</div>
```

A hover-tooltip is wired via a directive, but on the DOM there is no
`aria-label` / `title` on the root element. Effect:

- **Screen readers** announce "menuitem" without any context.
- **Browser-automation** (Playwright, chrome-devtools-mcp, etc.) can't
  use `getByRole('menuitem', { name: 'Dashboard' })` to address the
  collapsed item — the accessible name is empty.
- **Mouse-only sighted users** still get the tooltip on hover, so the
  experience degradation is invisible during dev unless one tabs
  through with a screen reader.

The text-label `<span>` is still in the DOM, but if it's hidden via
`display: none` (rather than `visibility: hidden` / `aria-hidden`)
its content is excluded from the accessible name computation.

## Proposed change

On the root element of `CoarSidebarItem`, always emit `aria-label`
sourced from the existing `label` prop:

```html
<div
  role="menuitem"
  :aria-label="label"
  :aria-current="active ? 'page' : undefined"
  …
>
  …
</div>
```

This is additive: it does not change visual rendering, does not break
existing consumers, and brings the collapsed state into compliance with
WCAG 2.1 SC 1.1.1 / 4.1.2. In the expanded state the visible text-label
already provides the accessible name, but a redundant `aria-label`
matching the visible text is fine — accessible-name calc returns the
`aria-label` value, which equals the visible text, no surprise.

If a redundant `aria-label` in expanded mode is undesirable, gate it
on collapsed-context:

```html
<div :aria-label="collapsed ? label : undefined" …>
```

(where `collapsed` is injected from the sidebar context the component
already consumes — same value driving the tooltip directive).

## Rationale

- **Accessibility:** every actionable element needs an accessible name.
  Icon-only buttons that rely on a hover-tooltip fail this for keyboard
  + screen-reader users.
- **Automation:** Playwright's `getByRole({ name: … })` and the
  Accessibility-Tree-based MCP automation in Claude Code / Cursor /
  Continue both walk accessible names. Adding `aria-label` makes the
  sidebar usable from those toolchains without resorting to
  CSS-class-based selectors (brittle).
- **Pattern alignment:** matches what every other major component
  library does for icon-only navigation (PrimeVue, Vuetify, Quasar all
  emit `aria-label` on their collapsed sidebar items).

## References

- WCAG 2.1 — Success Criterion 1.1.1 (Non-text Content) and 4.1.2
  (Name, Role, Value)
- ARIA Authoring Practices — Menu pattern
- Live example of the problem: https://auth.cocoar.dev/admin/users
  (collapse the sidebar → inspect any item → no `aria-label`)

## Workaround until fixed

None on the consumer side: child-component root-element attributes
cannot be injected from outside without `attrs` inheritance, and a
`role="menuitem"` is on the root, so wrapping doesn't help either.
The hover-tooltip-via-directive covers sighted mouse users; everything
else is a real gap.
