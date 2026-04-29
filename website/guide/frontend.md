# Vue-Frontend

Der Admin- und User-Frontend ist eine Vue-3-SPA unter
`src/frontend-vue/`. Flat-Layout — kein `apps/`-Wrapper.

## Tech-Stack

| Technologie | Zweck |
|---|---|
| **Vue 3** | UI-Framework, Composition API mit `<script setup>` |
| **Vue Router 5** | Client-side Routing |
| **Pinia 3** | State-Management (Auth-Store, Settings-Store) |
| **Vite 8** | Dev-Server + Bundler |
| **Tailwind 4** | Utility-CSS |
| **@cocoar/vue-ui** | Design-System-Komponenten (CoarSidebar, CoarMenu, CoarButton, CoarCard, ...) |
| **@cocoar/vue-data-grid** | AG-Grid-Wrapper inkl. CoarGridBuilder |
| **@cocoar/signalarrr** | TypeScript-SignalARRR-Client (typed RPC) |
| **@cocoar/vue-localization** | i18n |
| **@cocoar/vue-fragment-parser** | URL-Fragment-Routed Modals |
| **@cocoar/vue-script-editor** | Monaco-basierter Editor für ABAC-Scripts |

## Projekt-Layout

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
│       ├── admin/         ← Admin-UI (User, Group, Role, OAuth, ...)
│       ├── auth/          ← Login, Register, Reset, Setup, MagicLink, Passkey
│       ├── dashboard/
│       └── profile/       ← Self-Service: Profile, Sessions, Privacy
├── index.html
├── vite.config.ts
└── package.json
```

## Composables

### `useUI()`

Page-Layout-Steuerung. Setzt Header-Title, Footer-Buttons (Save/Delete),
Content-Mode (Standard/Wide) deklarativ aus dem View.

```typescript
const ui = useUI()
ui.setHeader({ title: 'Users', subtitle: 'Manage user accounts' })
ui.setFooter({
  button1: { label: 'Save', icon: 'check', onClick: save },
  button2: { label: 'Delete', icon: 'trash', variant: 'danger', onClick: del }
})
```

Footer-Button2 ist konventionell der Delete-Button (Danger-Variant), nur
sichtbar im Edit-Mode.

### `useEntityService()`

Generischer CRUD-Service inkl. Auto-Resubscribe auf SignalARRR-Streams.
Ein View deklariert "ich pflege Resource X" und bekommt Liste, Refresh,
Live-Updates frei Haus.

```typescript
const usersService = useEntityService<UserDto>({
  resource: 'users',
  api: '/api/admin/users'
})

await usersService.refresh()
const items = computed(() => usersService.items.value)

// Bei SignalR UserChangedEvent → automatische Liste-Refresh
```

### `useHttpClient()`

Immutable Fluent-Builder für HTTP-Calls. Unterscheidet zwischen
gewöhnlichen API-Calls und Auth-Calls (für Token-Refresh-Hooks).

```typescript
const http = useHttpClient()

const user = await http
  .get('/api/admin/users/{id}')
  .pathParam('id', userId)
  .json<UserDto>()
```

### `useModal()` + `useRoutedModals()`

Programmatische Modals (`useModal`) und URL-Fragment-routed Modals
(`useRoutedModals` über `@cocoar/vue-fragment-parser`):

```
/admin/oauth/clients#new        → Modal "New Client"
/admin/oauth/clients/123#edit   → Modal "Edit Client"
```

Browser-Back schließt das Modal (Fragment weg).

## Auth-Store (Pinia)

`stores/auth.store.ts`:

```typescript
export const useAuthStore = defineStore('auth', () => {
  const currentUser = ref<UserMe | null>(null)
  const status = ref<'initial' | 'loading' | 'authenticated' | 'unauthenticated' | 'requires-2fa'>('initial')
  const permissions = ref<string[]>([])

  function hasPermission(needed: string): boolean {
    if (permissions.value.includes('app:admin')) return true
    const [resource] = needed.split(':')
    if (permissions.value.includes(`${resource}:admin`)) return true
    return permissions.value.includes(needed)
  }

  // ... loadCurrentUser, login, logout
})
```

Initialisierung in `main.ts` **vor** `app.mount()` — verhindert Flash
of Unauthenticated Content.

## Per-Resource-Sidebar-Gating

In `views/admin/AdminView.vue` deklariert jedes Sidebar-Item welche
Permissions es sichtbar machen:

```typescript
interface NavItem {
  section: 'authorization' | 'oauth' | 'identity' | 'system'
  label: string
  icon: string
  path: string
  requirePermissions: string[]   // mirrored 1:1 mit Backend-Strings
}

const allNavItems: NavItem[] = [
  { section: 'authorization', label: 'nav.users', icon: 'users',
    path: '/admin/users', requirePermissions: ['user:read'] },
  { section: 'oauth', label: 'admin.oauthClients.title', icon: 'app-window',
    path: '/admin/oauth/clients', requirePermissions: ['oauth-client:read'] },
  // ...
]

function canSee(item: NavItem): boolean {
  return item.requirePermissions.some((p) => authStore.hasPermission(p))
}
```

Sektionen werden ausgeblendet wenn alle Items gefiltert sind. Ein User
mit nur `user:read` sieht nur "Authorization > Users" — keine OAuth,
keine System.

Die vier Sektionen:

| Sektion | Items |
|---|---|
| **Autorisierung** | Users, Roles, Groups, Policy Simulator |
| **OAuth & Federation** | Clients, Scopes, APIs |
| **Identitätsquellen** | Login-Provider, Identity-Provider |
| **System** | Realms, Auth Log, Änderungsanfragen, Einstellungen |

## SignalR-Lifecycle (wichtig!)

Der SignalR-Client startet **erst NACH dem Login** und reißt beim
Logout sauber ab. Das passiert über einen Logout-`window.location`-Reload
statt einer Vue-Router-Navigation:

```typescript
// composables/useAuth.ts
async function logout() {
  await http.post('/api/account/logout').void()
  window.location.href = '/login'   // Hard reload!
}
```

Sonst hängt eine alte SignalR-Subscription am alten User und das
Backend versucht weiterhin Notifications dorthin zu schicken.

## URL-Routing

Vue-Router läuft mit `createWebHistory('/')`. Es gibt **keinen
Realm-Pfad-Prefix** mehr (cocoar.auth macht Realm-Routing über die
Domain, nicht den Pfad). Routes:

| Route | View |
|---|---|
| `/login` | LoginView |
| `/register` | RegisterView |
| `/setup` | SetupView (First-Time-Setup) |
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
| `/admin/realms` | RealmsListView (nur in Manager-Realms) |
| `/admin/auth-log` | AuthLogView |
| `/admin/change-requests` | ChangeRequestsView |
| `/admin/settings` | AppSettingsView |
| `/admin/simulator` | AuthorizationSimulatorView (Policy-Debug) |

## Vite-Dev-Setup

Der Vite-Dev-Server läuft auf `localhost:4300`, das Backend auf
`localhost:9099`. Vite proxyed alle Backend-Pfade:

```typescript
// vite.config.ts (vereinfacht)
proxy: {
  '/api':         { target: 'http://localhost:9099' },
  '/connect':     { target: 'http://localhost:9099' },
  '/.well-known': { target: 'http://localhost:9099' },
  '/signalr':     { target: 'http://localhost:9099', ws: true },
}
```

Production: Frontend wird in `dist/` gebaut und über
`app.UseSpaUI()` als statische Assets aus `wwwroot/` ausgeliefert.

## Komponenten-Konventionen

- **Grids:** `CoarGridBuilder` (über `@cocoar/vue-data-grid`) für
  alle Listen — declarative Column-Definition, AG-Grid darunter
- **Context-Menüs:** `useContextMenu()` + `CoarContextMenu` für
  Right-Click-Aktionen auf Grid-Rows
- **Doppelklick:** auf Grid-Row → Edit-View
- **Footer-Buttons:** Button1 = Save (primary), Button2 = Delete (danger)
- **Error-State:** `undefined` (nicht `''`) für "kein Error" auf
  Inputs

## Locale & Themes

- `useI18n()` aus `@cocoar/vue-localization` für Übersetzungen
- Dark-Mode via `.dark-mode`-Klasse auf `<html>`, persistiert in
  `localStorage` als `coar-theme`
- Sidebar-Token-Defaults vom Design-System — nicht überschreiben
