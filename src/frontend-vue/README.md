# Modgud — Frontend

Vue 3 SPA for the Modgud Identity Provider. Covers the IdP-specific
admin views (Users, Roles, Groups, OAuth Clients/Scopes/APIs,
Service Accounts, Login Providers, IdP Config, Realms, Customization,
Observability, Inbox, Jobs) and end-user self-service (Profile,
Sessions, Privacy/GDPR).

## Layout

Flat — no `apps/` wrapper.

```
src/frontend-vue/
├── src/
│   ├── views/
│   │   ├── admin/{user, role, group, oauth, login-providers, realms,
│   │   │           idp-config, ...}/
│   │   ├── auth/                    # login, register, reset, confirm,
│   │   │                              setup, magic link, passkey
│   │   ├── profile/                 # profile, sessions, privacy
│   │   └── dashboard/
│   ├── stores/                      # Pinia (one per entity)
│   ├── models/                      # TS types matching backend DTOs
│   ├── composables/                 # useUI, useEntityService,
│   │                                  useHttpClient, useModal, useSignalR
│   ├── layouts/                     # MainLayout, ModalLayout
│   └── router/
├── public/i18n/{de,en}.json
└── e2e/
```

## Build & run

```bash
cd src/frontend-vue
pnpm install
pnpm dev               # vite dev server (default port 4300)
pnpm build             # production build
pnpm exec vue-tsc --noEmit   # type check
```

The dev server proxies `/api`, `/signalr`, `/.well-known/*`,
`/connect/*`, `/signin-oidc`, `/signout-callback-oidc` to
`http://localhost:9099` (the backend).

## Patterns

- `useUI()` — page header/footer/content context
- `useEntityService()` — generic CRUD + Pinia integration; SignalR
  auto-resubscribe on entities that publish change streams
- `useHttpClient()` — immutable fluent HTTP builder
- `useModal()` + `useRoutedModals()` — programmatic + URL-fragment modals
- `CoarGridBuilder` — fluent AG-Grid wrapper from `@cocoar/vue-data-grid`
- Per-resource sidebar gating in `views/admin/AdminView.vue` —
  permissions mapped 1:1 to backend `RequiresPermission` strings

## History

This codebase replaced an earlier `apps/`-based monorepo frontend. The
pre-cutover legacy is preserved at the `legacy-final` git tag.
