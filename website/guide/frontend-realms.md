# Realm-Aware SPA

The Vue frontend is realm-agnostic. It detects the realm from its URL at startup and adapts all behavior accordingly.

## How It Works

### 1. Realm Detection (`useRealmContext.ts`)

At module load time (before any component renders):

```typescript
const match = window.location.pathname.match(/^\/realms\/([a-z][a-z0-9-]+)(\/|$)/);

export const realmContext = match
  ? { slug: match[1], apiUrl: `/realms/${match[1]}/api`, baseHref: `/realms/${match[1]}/`, isSystem: false }
  : { slug: 'system', apiUrl: '/api', baseHref: '/', isSystem: true };
```

Values are **immutable** — computed once, never change during SPA lifetime.

### 2. API Routing (`http.ts`)

```typescript
const BASE_URL = realmContext.apiUrl;
// System:  /api/auth/me
// Acme:    /realms/acme/api/auth/me
```

### 3. Router Base (`router/index.ts`)

```typescript
export const router = createRouter({
  history: createWebHistory(realmContext.baseHref),
  routes: [...]
});
```

This ensures all client-side navigations stay within the realm prefix:
- System: `/login`, `/admin/users`
- Acme: `/realms/acme/login`, `/realms/acme/admin/users`

### 4. Conditional Menu (`MainLayout.vue`)

```vue
<template v-if="auth.isAdmin && realmContext.isSystem">
  <CoarMenuHeading>System</CoarMenuHeading>
  <CoarMenuItem label="Realms" icon="globe" ... />
</template>
```

### 5. Realm Indicator

The sidebar header shows the current realm slug so the user always knows which realm they're operating in.

## Vite Proxy Configuration

The dev server proxies API requests while serving the SPA for navigation:

```typescript
proxy: {
  '/realms': {
    target: 'http://localhost',
    bypass(req) {
      // Only proxy API/connect requests, serve SPA for navigation
      if (req.url && !/\/realms\/[^/]+\/(api|connect|\.well-known)/.test(req.url)) {
        return '/index.html';
      }
    },
  },
  '/api': { target: 'http://localhost' },
  '/connect': { target: 'http://localhost' },
}
```

## Deployment

For production, the reverse proxy (nginx) must serve `index.html` for realm paths:

```nginx
location /realms/ {
  try_files $uri /index.html;
}
```

The `<base href="/">` in `index.html` keeps asset loading from root. The Vue Router's `createWebHistory(baseHref)` handles the realm prefix.
