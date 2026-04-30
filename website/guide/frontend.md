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

## Per-resource sidebar gating

In `views/admin/AdminView.vue` each sidebar item declares which
permissions make it visible:

```typescript
interface NavItem {
  section: 'authorization' | 'oauth' | 'identity' | 'system'
  label: string
  icon: string
  path: string
  requirePermissions: string[]   // mirrored 1:1 with backend strings
}

const allNavItems: NavItem[] = [
  { section: 'authorization', label: 'nav.users', icon: 'users',
    path: '/admin/users', requirePermissions: ['cocoar-auth:user:read'] },
  { section: 'oauth', label: 'admin.oauthClients.title', icon: 'app-window',
    path: '/admin/oauth/clients', requirePermissions: ['cocoar-auth:oauth-client:read'] },
  { section: 'system', label: 'nav.settings', icon: 'settings',
    path: '/admin/settings', requirePermissions: ['realm:admin'] },
  // ...
]

function canSee(item: NavItem): boolean {
  return item.requirePermissions.some((p) => authStore.hasPermission(p))
}
```

Sections are hidden when all of their items are filtered out. A user
with only `cocoar-auth:user:read` sees only "Authorization > Users"
— no OAuth, no System.

The four sections:

| Section | Items |
|---|---|
| **Authorization** | Users, Roles, Groups |
| **OAuth & Federation** | Clients, Scopes, APIs |
| **Identity Sources** | Login Providers, Identity Providers |
| **System** | Realms, Auth Log, Change Requests, Settings |

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
prefix** anymore (cocoar.auth does realm routing via the domain, not
the path). Routes:

| Route | View |
|---|---|
| `/login` | LoginView |
| `/register` | RegisterView |
| `/setup` | SetupView (first-time setup) |
| `/2fa` | MfaLoginView |
| `/profile` | ProfileView |
| `/profile/sessions` | SessionsView |
| `/profile/privacy` | PrivacyView (GDPR) |
| `/profile/confirm-deletion?token=...` | ConfirmDeletionView |
| `/admin/users` | UsersListView |
| `/admin/users/:id` | UserDetailsView |
| `/admin/groups` | GroupsListView |
| `/admin/roles` | RolesListView |
| `/admin/oauth/clients` | OAuthClientsListView |
| `/admin/oauth/scopes` | OAuthScopesListView |
| `/admin/oauth/apis` | OAuthApisListView |
| `/admin/login-providers` | LoginProvidersListView |
| `/admin/idp-config` | IdpConfigView |
| `/admin/realms` | RealmsListView (only in manager realms) |
| `/admin/auth-log` | AuthLogView |
| `/admin/change-requests` | ChangeRequestsView |
| `/admin/settings` | AppSettingsView |

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
