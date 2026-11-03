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

### Isolated HTTP client regression tests

With the project's Node 22 runtime, `pnpm test:unit` runs Node tests of the HTTP
client against synthetic responses and of the entity service using the installed
Vue reactivity with an isolated fake SignalR transport. It does not start Vite,
Playwright, Docker or a backend and makes no network requests. This is not a
substitute for browser or backend integration tests.

The login page handles expected 401 failures from the exact TOTP login,
email-MFA login and email-MFA challenge-request POSTs locally. They still
reject with `HttpClientError`; retry or an explicit return to credentials
retains the pending continuation. Protected requests and factor-management
endpoints keep their normal 401 navigation; server MFA and lockout are unchanged.

Optional background GETs can use `{ redirectOnStepUp: false }` to retain the
current page when the server requires area-level MFA. They still reject with
`HttpClientError`; 401 and fresh account-proof redirects remain unchanged.
Do not apply this to explicit user actions. Entity-service `loadAll` callers
can opt out through `redirectOnLoadStepUp: false` (for example, the header inbox);
all other callers keep the default navigation.

Entity-list reconnect reloads handle their background rejection locally. A failed
full-list read clears the cached list, resets `allLoaded` and sets `loadFailed`;
it is not a successful empty snapshot. Inbox and the user/service-account/position
lists show a generic warning, not raw HTTP bodies. A later full REST read or
authoritative FullSync clears the failure; incremental pushes alone do not prove
the full list is current. No retry loop or extra subscription is started.
Explicit `loadAll`/`initialize` and CRUD calls still reject to their callers.
Overlapping full reads use the latest-started read for store state; an authoritative
FullSync supersedes pending reads. Older completions still resolve/reject to their
explicit callers but cannot overwrite or invalidate a newer snapshot. Later deltas
or a mutation rollback may partially refill a failed list; the warning remains.

For the real SPA in a browser **without any backend or server**, first build to
an isolated directory, then run:

```bash
MODGUD_BROWSER_DIST=/absolute/path/to/isolated-build pnpm test:browser:isolated
```

This separate Playwright configuration has no global setup, web server or
container lifecycle. Tests serve the built files and simulated API replies via
browser interception, reject unknown requests, and block WebSockets/service
workers. They verify Vue/profile/header navigation, not real authentication,
database writes, SignalR reconnects or backend policy enforcement. They require
an already installed matching Chromium; they do not download one automatically.

### Isolated HTTP client regression tests

With the project's Node 22 runtime, `pnpm test:unit` runs Node tests of the HTTP
client against synthetic responses and of the entity service using the installed
Vue reactivity with an isolated fake SignalR transport. It does not start Vite,
Playwright, Docker or a backend and makes no network requests. This is not a
substitute for browser or backend integration tests.

Optional background GETs can use `{ redirectOnStepUp: false }` to retain the
current page when the server requires area-level MFA. They still reject with
`HttpClientError`; 401 and fresh account-proof redirects remain unchanged.
Do not apply this to explicit user actions. Entity-service `loadAll` callers
can opt out through `redirectOnLoadStepUp: false` (for example, the header inbox);
all other callers keep the default navigation.

Entity-list reconnect reloads handle their background rejection locally. A failed
full-list read clears the cached list, resets `allLoaded` and sets `loadFailed`;
it is not a successful empty snapshot. Inbox and the user/service-account/position
lists show a generic warning, not raw HTTP bodies. A later full REST read or
authoritative FullSync clears the failure; incremental pushes alone do not prove
the full list is current. No retry loop or extra subscription is started.
Explicit `loadAll`/`initialize` and CRUD calls still reject to their callers.
Overlapping full reads use the latest-started read for store state; an authoritative
FullSync supersedes pending reads. Older completions still resolve/reject to their
explicit callers but cannot overwrite or invalidate a newer snapshot. Later deltas
or a mutation rollback may partially refill a failed list; the warning remains.

For the real SPA in a browser **without any backend or server**, first build to
an isolated directory, then run:

```bash
MODGUD_BROWSER_DIST=/absolute/path/to/isolated-build pnpm test:browser:isolated
```

This separate Playwright configuration has no global setup, web server or
container lifecycle. Tests serve the built files and simulated API replies via
browser interception, reject unknown requests, and block WebSockets/service
workers. They verify Vue/profile/header navigation, not real authentication,
database writes, SignalR reconnects or backend policy enforcement. They require
an already installed matching Chromium; they do not download one automatically.

### Isolated HTTP client regression tests

With the project's Node 22 runtime, `pnpm test:unit` runs Node tests of the HTTP
client against synthetic responses. It does not start Vite,
Playwright, Docker or a backend and makes no network requests. This is not a
substitute for browser or backend integration tests.

Optional background GETs can use `{ redirectOnStepUp: false }` to retain the
current page when the server requires area-level MFA. They still reject with
`HttpClientError`; 401 and fresh account-proof redirects remain unchanged.
Do not apply this to explicit user actions. Entity-service `loadAll` callers
can opt out through `redirectOnLoadStepUp: false` (for example, the header inbox);
all other callers keep the default navigation.

For the real SPA in a browser **without any backend or server**, first build to
an isolated directory, then run:

```bash
MODGUD_BROWSER_DIST=/absolute/path/to/isolated-build pnpm test:browser:isolated
```

This separate Playwright configuration has no global setup, web server or
container lifecycle. Tests serve the built files and simulated API replies via
browser interception, reject unknown requests, and block WebSockets/service
workers. They verify Vue/profile/header navigation, not real authentication,
database writes, SignalR reconnects or backend policy enforcement. They require
an already installed matching Chromium; they do not download one automatically.

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
