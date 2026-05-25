---
title: Plattform
description: Operator-facing configuration of the Modgud instance.
---

# Plattform

The sidebar has **two** top-level admin areas, and the difference matters.
**Administration** is realm-admin work — users, groups, OAuth clients, realms; the
"who can do what" of the system. **Plattform** is operator-facing IdP config —
branding, observability, notification retention, app-level settings; the "how
this IdP-instance is configured".

## Why the split

Different audience, different cadence.

- **Administration** is touched daily by realm admins, user managers, and OAuth
  managers. The data inside is the live tenant content.
- **Plattform** is touched mostly during setup, on the occasional theme refresh,
  and when an operator needs to look at runtime telemetry or trim inbox
  retention. The data inside describes the instance, not the tenants in it.

Keeping them apart keeps the daily admin sidebar short, and gives operators a
predictable home for "where did I configure that scrape token / branding asset /
retention window" without scrolling past two dozen tenant grids.

## Sub-nav groups

The Plattform area is itself split into two thematic groups inside its own
sub-nav (see `PlatformView.vue`):

### Anpassung

| Item | Path | What it does |
| --- | --- | --- |
| [Branding](./branding) | `/plattform/customization/branding` | Per-realm SPA theming — product name, primary color, logo, favicon |
| [Pages](./pages) | `/plattform/customization/pages` | Page-builder editor (Beta) for login / logout / forgot-password — gated by the `PageBuilder` feature flag |
| [Asset Library](./assets) | `/plattform/customization/assets` | BYTEA store for logos, favicons, login illustrations; SVG sanitisation, 2 MB cap |

### Betrieb

| Item | Path | What it does |
| --- | --- | --- |
| [Observability](./observability) | `/plattform/observability` | Live IdP metrics + traces, with the OpenTelemetry pipeline behind it |
| Inbox-Einstellungen | `/plattform/inbox-settings` | Per-tenant notification retention windows |
| [Einstellungen](./settings) | `/plattform/settings` | Projection rebuild, 2FA enforcement, grace period, SMTP, …; the catch-all operator surface |

## Permission gating

The sidebar entry hides when the user holds **none** of these grants:

```ts
const PLATFORM_RESOURCE_PERMISSIONS = [
  'realm-settings:read',
  'asset:read',
  'observability:read',
  'inbox-settings:read',
  'realm:admin',
] as const
```

Source: `src/frontend-vue/src/layouts/MainLayout.vue` (`hasAnyPlatformPermission`).

Per-item gating lives inside `PlatformView.vue` on each `SubNavItem.visible` —
items the user can't read disappear from the sub-nav even when the wrapper is
shown. `realm:admin` is a realm-wide bypass and is honoured implicitly by
`authStore.hasPermission`. The Pages item adds a second gate on
`appConfig.config.Features.PageBuilder` so the editor stays hidden when the
operator hasn't switched the beta flag on.

## URL convention

Every Plattform route sits under `/plattform/*`. The wrapper redirects an empty
`/plattform` to `/plattform/customization/branding` (the always-on starting
point), so the area is link-safe even when the user has no other plattform
permission.

## Header pattern

Every Plattform view sets the same header shape via `useUI()`:

```ts
ui.header.title = 'Plattform'
ui.header.subTitle = 'Branding' // or 'Observability', 'Asset Library', …
```

So the breadcrumb the user sees is always `Plattform › <item>` — consistent
across the area regardless of which sub-page they landed on.

## Quick links

- [Branding](./branding) — per-realm logo, colors, product name
- [Pages](./pages) — page-builder editor (Beta)
- [Asset Library](./assets) — image upload + SVG sanitisation
- [Observability](./observability) — metrics, traces, live activity feed
- [Inbox](./inbox) — operator notification stream
- [Inbox-Einstellungen](./inbox-settings) — per-tenant notification retention
- [Einstellungen](./settings) — projections, SMTP, 2FA, grace period

::: tip Looking for tenant-admin work?
Users, groups, OAuth clients, realms — those live under
[Administration](../admin/), not here.
:::
