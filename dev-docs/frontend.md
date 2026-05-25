# Vue frontend

The admin and user frontend is a Vue 3 SPA at `src/frontend-vue/`.
Flat layout — no `apps/` wrapper.

## Tech stack

| Technology | Purpose |
|---|---|
| **Vue 3** | UI framework, Composition API with `<script setup>` |
| **Vue Router 5** | Client-side routing |
| **Pinia 3** | State management (auth store, settings store) |
| **Vite 8** | Dev server + bundler |
| **Tailwind 4** | Utility CSS |
| **@cocoar/vue-ui** | Design-system components (CoarSidebar, CoarMenu, CoarButton, CoarCard, ...) |
| **@cocoar/vue-data-grid** | AG Grid wrapper including CoarGridBuilder |
| **@cocoar/signalarrr** | TypeScript SignalARRR client (typed RPC) |
| **@cocoar/vue-localization** | i18n |
| **@cocoar/vue-fragment-parser** | URL-fragment-routed modals |
| **@cocoar/vue-script-editor** | Monaco-based editor for membership scripts |

## Project layout

```
src/frontend-vue/
├── src/
│   ├── App.vue
│   ├── main.ts
│   ├── assets/
│   ├── components/
│   ├── composables/
│   │   ├── useUI.ts
│   │   ├── useEntityService.ts
│   │   ├── useHttpClient.ts
│   │   ├── useSignalR.ts
│   │   └── usePreferences.ts
│   ├── layouts/
│   ├── models/
│   ├── router/
│   ├── stores/
│   └── views/
│       ├── admin/         ← Admin UI (User, Group, Role, OAuth, ...)
│       ├── auth/          ← Login, Register, Reset, Setup, MagicLink, Passkey
│       ├── dashboard/
│       └── profile/       ← Self-service: Profile, Sessions, Privacy
├── index.html
├── vite.config.ts
└── package.json
```

## Composables

### `useUI()`

Page-layout control. Sets header title, footer buttons (Save/Delete),
content mode (standard/wide) declaratively from the view.

```typescript
const ui = useUI()
ui.setHeader({ title: 'Users', subtitle: 'Manage user accounts' })
ui.setFooter({
  button1: { label: 'Save', icon: 'check', onClick: save },
  button2: { label: 'Delete', icon: 'trash', variant: 'danger', onClick: del }
})
```

By convention, footer button2 is the delete button (danger variant),
visible only in edit mode.

### `useEntityService()`

Generic CRUD service including auto-resubscribe to SignalARRR streams.
A view declares "I manage resource X" and gets a list, refresh, and
live updates for free.

```typescript
const usersService = useEntityService<UserDto>({
  resource: 'users',
  api: '/api/admin/users'
})

await usersService.refresh()
const items = computed(() => usersService.items.value)

// On a SignalR UserChangedEvent → automatic list refresh
```

### `useHttpClient()`

Immutable fluent builder for HTTP calls. Distinguishes between regular
API calls and auth calls (for token-refresh hooks).

```typescript
const http = useHttpClient()

const user = await http
  .get('/api/admin/users/{id}')
  .pathParam('id', userId)
  .json<UserDto>()
```

### `useModal()` + `useRoutedModals()`

Programmatic modals (`useModal`) and URL-fragment-routed modals
(`useRoutedModals` via `@cocoar/vue-fragment-parser`):

```
/admin/oauth/clients#new        → modal "New Client"
/admin/oauth/clients/123#edit   → modal "Edit Client"
```

Browser back closes the modal (fragment cleared).

## Auth store (Pinia)

`stores/auth.store.ts`:

```typescript
export const useAuthStore = defineStore('auth', () => {
  const currentUser = ref<UserMe | null>(null)
  const status = ref<'initial' | 'loading' | 'authenticated' | 'unauthenticated' | 'requires-2fa'>('initial')
  const permissions = ref<string[]>([])

  // Mirrors the backend PermissionEvaluator. Permission strings are
  // fully qualified as "<app>:<resource>:<action>".
  // Bypasses: realm:admin > <app>:admin > <app>:<resource>:admin.
  function hasPermission(permission: string): boolean {
    if (permissions.value.includes('realm:admin')) return true
    if (permissions.value.includes(permission)) return true
    const parts = permission.split(':')
    if (parts.length === 3) {
      if (permissions.value.includes(`${parts[0]}:admin`)) return true
      if (permissions.value.includes(`${parts[0]}:${parts[1]}:admin`)) return true
    }
    return false
  }

  // ... loadCurrentUser, login, logout
})
```

Initialised in `main.ts` **before** `app.mount()` — prevents flash of
unauthenticated content.

## Two top-level admin areas

The sidebar has **two** admin sidebar entries, each with its own 2nd-level
nav rail:

| Sidebar entry | Wrapper | Audience |
|---|---|---|
| **Administration** (`cog` icon, `/admin/*`) | `views/admin/AdminView.vue` | Tenant-/realm-admin work — "who can do what" |
| **Plattform** (`server` icon, `/plattform/*`) | `views/platform/PlatformView.vue` | Operator-facing IdP config — "how this IdP instance is set up" |

See [Plattform overview](../plattform/) for the rationale and what lives
where.

## Per-resource sidebar gating

In `views/admin/AdminView.vue` and `views/platform/PlatformView.vue` each
sidebar item declares which permissions make it visible. The permission
strings mirror the backend `RequiresPermission(...)` calls (bare 2-segment
form — the app context `modgud:` is implicit in this codebase):

```typescript
interface NavItemDef {
  label: string
  icon: string
  to: string
  /** Any-of: matches if the user holds any of these. */
  requirePermissions: string[]
  /** Optional operator-level feature flag gate (both must pass). */
  requireFeature?: 'PageBuilder'
}

const sections = computed<SectionDef[]>(() => [
  {
    key: 'authorization',
    heading: t('admin.section.authorization', {}, 'Autorisierung'),
    items: [
      { label: 'nav.users', icon: 'users', to: '/admin/users', requirePermissions: ['user:read'] },
      { label: 'admin.serviceAccounts.title', icon: 'cpu', to: '/admin/service-accounts', requirePermissions: ['service-account:read'] },
      // ...
    ],
  },
  // OAuth & Federation, System sections follow ...
])

function canSee(item: NavItemDef): boolean {
  if (item.requireFeature && !appConfig.config.Features[item.requireFeature]) return false
  return item.requirePermissions.some((p) => authStore.hasPermission(p))
}
```

Items are passed to `SubNavLayoutGrouped` with `visible: canSee(item)`
pre-filtering. Groups whose every item is hidden are dropped entirely by
the layout — a user with only `user:read` sees only "Autorisierung →
Benutzer", no OAuth section, no System section.

The Plattform entry itself is gated by `hasAnyPlatformPermission` in
`MainLayout.vue` — visible if the user has any of
`realm-settings:read`, `asset:read`, `observability:read`,
`inbox-settings:read`, or `realm:admin`.

The current `/admin/*` section layout:

| Section | Items |
|---|---|
| **Autorisierung** | Benutzer, Service Accounts, Rollen, Gruppen |
| **OAuth & Federation** | Login-Provider, OAuth-Clients, OAuth-Scopes, OAuth-APIs |
| **System** | Anwendungen, Realms, Realm-Einstellungen, Auth Log, Scheduled Jobs, Änderungsanfragen |

The current `/plattform/*` section layout:

| Section | Items |
|---|---|
| **Anpassung** | Branding, Pages (PageBuilder-gated), Asset-Library |
| **Betrieb** | Observability, Inbox-Einstellungen, App-Einstellungen |

## Reusable Sub-Nav layouts

Two layouts under `src/layouts/` power both wrapper views:

- **`SubNavLayout.vue`** — single flat menu; menu scrolls **internally** if items overflow
- **`SubNavLayoutGrouped.vue`** — multiple menus under section headings; the **whole container** scrolls if total content overflows, individual menus keep their natural height (no inner per-menu scrollbars)

Both share `sub-nav-types.ts` (`SubNavItem` + `SubNavGroup`). An item with
`visible: false` is filtered out by the layout; a group whose items are
all filtered out is dropped from rendering. Pass `to: RouteLocationRaw`
for navigation (RouterLink wraps the item — Ctrl/Cmd-click opens in a new
tab natively), or `onClick` for action items.

Adding a third top-level area is a 3-step recipe: (1) write a wrapper
component that consumes `SubNavLayoutGrouped` with your `SubNavGroup[]`,
(2) register the wrapper's route + its children in `router/index.ts`, (3)
add a `<CoarSidebarItem>` to `MainLayout.vue` with a permission gate.

## Header with breadcrumb trail

`useUI()` exposes a reactive `header` slot for the title bar. The
`subTitle` field accepts either a plain string or a `UIBreadcrumb[]` —
the array form renders chevron-separated entries with all but the last as
`<RouterLink>`s:

```typescript
import type { UIBreadcrumb } from '@/composables/useUI'

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Plattform')
  // Plain string form — most page views:
  ctx.header.subTitle = t('admin.observability.title', {}, 'Observability')

  // Breadcrumb form — when a deeper page wants navigable parent crumbs:
  // ctx.header.subTitle = [
  //   { label: t('admin.customization.pages.title', {}, 'Pages'), to: '/plattform/customization/pages' },
  //   { label: pageSlot.name },
  // ] satisfies UIBreadcrumb[]
  ctx.header.icon = 'activity'
}), { immediate: true })
```

The `UIBreadcrumb` interface (`{ label: string; to?: string | null }`)
keeps the breadcrumb purely declarative — no RouterLink wrapping at the
call site.

## SignalR lifecycle (important!)

The SignalR client starts **only after login** and tears down cleanly
on logout. Logout uses a `window.location` reload instead of a Vue
Router navigation:

```typescript
// composables/useAuth.ts
async function logout() {
  await http.post('/api/account/logout').void()
  window.location.href = '/login'   // hard reload!
}
```

Otherwise an old SignalR subscription stays bound to the old user and
the backend keeps trying to push notifications there.

## URL routing

Vue Router runs with `createWebHistory('/')`. There is **no realm path
prefix** anymore (modgud does realm routing via the domain, not
the path). Routes:

| Route | View |
|---|---|
| `/login` | LoginView |
| `/register` | RegisterView |
| `/bootstrap?token=…` | BootstrapView (consumes a first-admin invite — see [First-time setup](../getting-started/first-time-setup)) |
| `/2fa` | MfaLoginView |
| `/forgot-password` / `/reset-password` | Password-reset flows |
| `/consent` | OAuth consent screen |
| `/confirm-deletion?token=...` | ConfirmDeletionView (GDPR-delete confirm) |
| `/profile` | ProfileView (tabs: account, sessions, privacy) |
| `/dashboard` | DashboardView |
| **Administration** (`/admin/*`) | `AdminView.vue` wrapper |
| `/admin/users` | UserList + routed-fragment user-details modal |
| `/admin/service-accounts` | ServiceAccountsView + details modal |
| `/admin/roles` | RoleList + routed-fragment details modal |
| `/admin/groups` | GroupList + routed-fragment details modal |
| `/admin/oauth/clients` | ClientList + details modal |
| `/admin/oauth/scopes` | ScopeList + details modal |
| `/admin/oauth/apis` | ApiList + details modal |
| `/admin/login-providers` | LoginProviderList + details modal |
| `/admin/apps` | AppList + details modal |
| `/admin/realms` | RealmList + details modal (control-plane realm only) |
| `/admin/realm-settings` | RealmSettingsView |
| `/admin/auth-log` | AuthLogView |
| `/admin/scheduled-jobs` | ScheduledJobList + details modal (Schedule/Config/History tabs) |
| `/admin/change-requests` | ChangeRequestsView |
| **Plattform** (`/plattform/*`) | `PlatformView.vue` wrapper |
| `/plattform/customization/branding` | BrandingView |
| `/plattform/customization/pages` | PagesView (PageBuilder feature-gate) |
| `/plattform/customization/pages/:slug` | PageEditorView |
| `/plattform/customization/assets` | AssetsView |
| `/plattform/observability` | AdminObservabilityView |
| `/plattform/inbox-settings` | InboxSettingsView |
| `/plattform/settings` | AppSettingsView |

## Vite dev setup

The Vite dev server runs on `localhost:4300`, the backend on
`localhost:9099`. Vite proxies all backend paths:

```typescript
// vite.config.ts (simplified)
proxy: {
  '/api':         { target: 'http://localhost:9099' },
  '/connect':     { target: 'http://localhost:9099' },
  '/.well-known': { target: 'http://localhost:9099' },
  '/signalr':     { target: 'http://localhost:9099', ws: true },
}
```

In production: the frontend is built to `dist/` and served as static
assets from `wwwroot/` via `app.UseSpaUI()`.

## Component conventions

- **Grids:** `CoarGridBuilder` (via `@cocoar/vue-data-grid`) for all
  lists — declarative column definitions, AG Grid underneath
- **Context menus:** `useContextMenu()` + `CoarContextMenu` for
  right-click actions on grid rows
- **Double-click:** on a grid row → edit view
- **Footer buttons:** Button1 = Save (primary), Button2 = Delete (danger)
- **Error state:** `undefined` (not `''`) for "no error" on inputs

## Locale & themes

- `useI18n()` from `@cocoar/vue-localization` for translations
- Dark mode via `.dark-mode` class on `<html>`, persisted in
  `localStorage` as `coar-theme`
- Sidebar token defaults from the design system — do not override
