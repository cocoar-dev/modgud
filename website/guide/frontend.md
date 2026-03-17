# Vue Frontend

The admin frontend is a Vue 3 SPA at `src/frontend-vue/apps/frontend/`.

## Tech Stack

| Technology | Purpose |
|-----------|---------|
| Vue 3 | UI framework (Composition API, `<script setup>`) |
| Vue Router 4 | Client-side routing with realm-aware base href |
| Pinia | State management (auth store) |
| @cocoar/vue-ui | Component library (CoarSidebar, CoarMenu, CoarButton, CoarCard, etc.) |
| @cocoar/vue-data-grid | Data grid component (AG Grid wrapper) |
| Vite | Dev server and bundler |

## Project Structure

```
src/
├── core/
│   ├── api/
│   │   ├── http.ts          # Base HTTP client (realm-aware base URL)
│   │   ├── auth-api.ts      # Auth endpoints
│   │   └── admin-api.ts     # Admin CRUD endpoints
│   └── models/
│       ├── auth.models.ts   # Auth, User, Role, Realm types
│       └── oauth.models.ts  # OAuth client, scope, API resource types
├── stores/
│   └── auth.store.ts        # Pinia auth store
├── composables/
│   ├── useRealmContext.ts   # Realm detection from URL
│   ├── useUI.ts             # UI state (header, footer, content)
│   └── useDirtyGuard.ts    # Form dirty state guard
├── layouts/
│   └── MainLayout.vue       # Single layout for account + admin
├── views/
│   ├── admin/               # Admin views (users, roles, realms, oauth)
│   └── auth/                # Login, register, 2FA, password reset
└── router/
    └── index.ts             # Routes with auth/admin guards
```

## Single Layout

Account and admin views share a single `MainLayout.vue` with a unified sidebar:

- **Account** section: Home, Profile, Sessions, Privacy (always visible)
- **System** section: Realms (only for system realm admins)
- **Administration** section: Users, Roles, Clients, Scopes, APIs, Login Providers (admin role required)

## API Layer

All API calls go through `http.ts` which:
- Uses `realmContext.apiUrl` as base URL (realm-aware)
- Sends cookies with `credentials: 'include'`
- Handles 401 errors by resetting auth state and redirecting to login (only for mid-session expiry, not during initialization)

## Auth Store

The Pinia auth store manages:
- `currentUser` — the logged-in user's info (from `/api/auth/me`)
- `status` — `initial` | `loading` | `authenticated` | `unauthenticated` | `requires-2fa`
- `isAdmin` — computed from `currentUser.roles.includes('Admin')`

Initialization happens before app mount in `main.ts` to avoid flash of unauthenticated content.
