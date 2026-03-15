# Vue Architecture & Code Quality Audit

**Codebase:** `src/frontend-vue/apps/frontend/src/`
**Date:** 2026-03-06
**Stack:** Vue 3 (Composition API) + Pinia + Vue Router 4 + TypeScript

---

## Summary

The codebase is well-structured for a mid-size admin/auth application. It uses Vue 3 Composition API consistently, has a clean API layer with typed models, and the router is properly guarded. However, there are significant DRY violations across views, a missing global 401 interceptor, duplicated CSS across nearly every component, and the `ProfileView` is doing too much. The TypeScript usage is solid with no `any` types detected, but there are a few unsafe patterns. Overall, this is a good foundation that needs targeted refactoring to be production-grade.

**Stats:** 26 `.vue` files, 1 Pinia store, 2 API modules, 2 model files, 1 router file.

---

## Critical Issues (must fix)

### 1. No global 401/403 interceptor -- sessions can silently expire

**File:** `core/api/http.ts`

The `request()` function throws `ApiError` for all non-OK responses, but there is no global handling for 401 (session expired) or 403 (forbidden). If a user's session expires mid-use, API calls silently fail and each view handles errors individually with generic messages. The user stays on the page in a broken state instead of being redirected to login.

**Impact:** Users see "Failed to load users" instead of being redirected to `/login`. Auth state in the Pinia store becomes stale.

**Recommendation:** Add a response interceptor in `http.ts` that detects 401 and triggers `auth.logout('/login?expired=true')`, or use a global error handler. Example:

```ts
if (response.status === 401) {
  const { useAuthStore } = await import('@/stores/auth.store');
  const auth = useAuthStore();
  auth.logout('/login?sessionExpired=true');
  throw new ApiError(401, { message: 'Session expired' });
}
```

### 2. `useRouter()` called at store definition scope -- will break outside component context

**File:** `stores/auth.store.ts:11`

```ts
const router = useRouter(); // line 11, inside defineStore setup
```

`useRouter()` relies on the Vue app's injection context. It works here only because `useAuthStore()` is first called in `main.ts` after `app.use(router)`. However, if the store is ever used in a context without a component setup (e.g., a service, a test, or a Pinia plugin), this will throw. This is fragile.

**Recommendation:** Accept `router` as a parameter in `logout()` and `login()`, or import the router instance directly:

```ts
import { router } from '@/router';
```

### 3. `PrivacyView` uses `prompt()` for password input -- insecure and poor UX

**File:** `views/PrivacyView.vue:43`

```ts
const password = prompt('Enter your password to request account deletion:');
```

`window.prompt()` shows the password in plain text and is blocked by many browsers/extensions. This is a critical UX and security issue for an Identity Provider application.

**Recommendation:** Replace with an inline form or a modal dialog that uses a proper `<CoarPasswordInput>` component.

### 4. Optional chaining on non-optional API methods

**File:** `views/PrivacyView.vue:26,47,57`

```ts
await authApi.exportData?.();
await authApi.requestDeletion?.({ password });
await authApi.cancelDeletion?.();
```

These methods are always defined in `auth-api.ts`. The optional chaining (`?.`) suggests uncertainty about the API surface. If these methods truly can be absent, the types are wrong. If they're always present (they are), the `?.` silently swallows bugs -- a missing method would return `undefined` instead of throwing.

**Recommendation:** Remove the optional chaining. The methods exist and are typed.

---

## Improvements (should fix)

### 5. Massive CSS duplication across all views

Almost every view re-declares identical CSS classes:

| Class | Duplicated in |
|-------|--------------|
| `.auth-page` | LoginView, RegisterView, ForgotPasswordView, ResetPasswordView, ConfirmEmailView, TwoFactorLoginView, RecoveryLoginView, SetupView |
| `.form-group` | 14+ files |
| `.form-page`, `.page-header`, `.page-title` | All admin views |
| `.list-page` | UserListView, RoleListView, ClientListView, ScopeListView, ApiResourceListView |
| `.mb-3`, `.mb-4` | 15+ files |
| `.centered` | 7+ files |
| `.form-row-2` | 4+ files |
| `.form-actions` | 5+ files |

**Recommendation:** Extract shared layout classes to `styles.css` or create utility CSS classes. The auth views especially share an identical layout structure (centered card with title/subtitle/form) that should be a single set of global classes or a layout component.

### 6. Repeated form view boilerplate -- extract a composable

Every form view (UserFormView, RoleFormView, ClientFormView, ScopeFormView, ApiResourceFormView) follows the exact same pattern:

```ts
const id = computed(() => route.params.id as string | undefined);
const isEditMode = computed(() => !!id.value);
const isLoading = ref(false);
const isSaving = ref(false);
const isDeleting = ref(false);
const error = ref('');

// onMounted: load if editing
// onSubmit: create or update
// onDelete: confirm + delete + redirect
```

**Recommendation:** Extract a `useAdminForm<T>()` composable:

```ts
function useAdminForm<T>(options: {
  load: (id: string) => Promise<T>;
  create: (data: unknown) => Promise<unknown>;
  update: (id: string, data: unknown) => Promise<unknown>;
  delete: (id: string) => Promise<void>;
  listRoute: string;
}) {
  // Returns: id, isEditMode, isLoading, isSaving, isDeleting, error, onSubmit, onDelete
}
```

### 7. Repeated list view boilerplate -- extract a composable

All list views (UserListView, RoleListView, ClientListView, ScopeListView, ApiResourceListView) share this pattern:

```ts
const data = ref<T[] | null>(null);
const error = ref('');
const { builder } = useDataGrid<T>();
builder.columns([...]).rowDataRef(data).rowId(...).onRowClicked(...);
onMounted(async () => { try { data.value = (await api()).items; } catch { error.value = '...'; } });
```

**Recommendation:** Extract a `useAdminList<T>()` composable that handles loading, error state, and returns the builder.

### 8. `ProfileView` is a mega-component (344 lines) -- split it up

**File:** `views/ProfileView.vue` -- 173 lines of script + 171 lines of template

This single component manages three distinct concerns:
- Personal information editing (profile update)
- Password change
- Two-factor authentication (setup, enable, disable, recovery codes)

Each section has its own error/success refs, loading states, and API calls. The 2FA section alone has 6 refs and 5 async functions.

**Recommendation:** Extract into child components:
- `ProfileInfoSection.vue` -- first/last name, phone number
- `ChangePasswordSection.vue` -- password change form
- `TwoFactorSection.vue` -- all 2FA management

### 9. List views don't use server-side pagination

**File:** All admin list views (UserListView, RoleListView, etc.)

The `adminApi` supports `PaginationParams` (page, pageSize, search, sortBy, sortDescending), but list views call the API without any pagination params:

```ts
const result = await adminApi.getUsers(); // No pagination
users.value = result.items;
```

The `UserList` response includes `totalCount`, `page`, `pageSize` but they're ignored. This will cause performance issues with many records.

**Recommendation:** Implement pagination using the data grid's pagination events, or at minimum pass a reasonable `pageSize`.

### 10. Catch-all route silently redirects to `/` instead of showing 404

**File:** `router/index.ts:134`

```ts
{ path: '/:pathMatch(.*)*', redirect: '/' },
```

When a user navigates to a non-existent URL, they are silently redirected to the home page with no indication that the page wasn't found. This makes debugging URL issues difficult.

**Recommendation:** Create a `NotFoundView.vue` that displays a proper 404 message with a link to navigate home.

### 11. `NavMenuItem` active state logic has edge case

**File:** `components/NavMenuItem.vue:15-17`

```ts
const isActive = computed(() =>
  props.exact ? route.path === props.to : route.path.startsWith(props.to),
);
```

The `startsWith` check means `/admin/users` also marks `/admin/users/create` as active for the Users menu item, which is correct. But `to="/"` would match everything. This is mitigated by using `:exact="true"` on the Home link, but the pattern is fragile. If someone adds a `NavMenuItem` without `exact` for `/`, all items would appear active.

### 12. Data grid builder defined at module scope -- could cause issues with HMR

**File:** All list views (e.g., `UserListView.vue:13-39`)

The `useDataGrid<T>()` call and builder configuration happen at the top level of `<script setup>`, which means they execute once per component instance. This is correct for production, but during HMR the builder object is recreated while the grid component may not be. Verify this works correctly during development.

### 13. `LoginView` does not clear auth store error on mount

**File:** `views/auth/LoginView.vue`

If a user navigates away from login and returns, the previous error message from the auth store persists because the error is stored globally in the Pinia store (`auth.error`) and no cleanup happens on mount.

**Recommendation:** Call `auth.clearError()` in `onMounted` or use a local error ref instead of the store's error for the login form.

---

## Nice to Have

### 14. Auth views share identical layout -- extract `AuthLayout` component

All 7 auth views (Login, Register, ForgotPassword, ResetPassword, ConfirmEmail, TwoFactorLogin, RecoveryLogin) plus SetupView use the exact same layout:

```html
<div class="auth-page">
  <CoarCard elevated padding="l" class="auth-card">
    <h1 class="auth-title">...</h1>
    <p class="auth-subtitle">...</p>
    <!-- form content -->
  </CoarCard>
</div>
```

Extract an `AuthLayout.vue` component with slots:

```vue
<template>
  <div class="auth-page">
    <CoarCard elevated padding="l" class="auth-card">
      <h1 class="auth-title"><slot name="title" /></h1>
      <p v-if="$slots.subtitle" class="auth-subtitle"><slot name="subtitle" /></p>
      <slot />
    </CoarCard>
  </div>
</template>
```

### 15. Admin layout styles duplicated between `AdminLayout` and `MainLayout`

**Files:** `layouts/AdminLayout.vue`, `layouts/MainLayout.vue`

Both layouts have identical CSS for `.app-layout`, `.sidebar-header`, `.sidebar-logo`, `.sidebar-footer`, `.main-content`. Extract to a shared layout component or shared CSS.

### 16. `parseLines()` helper duplicated in 3 files

**Files:** `ClientFormView.vue:42`, `ScopeFormView.vue:40`, `ApiResourceFormView.vue:29`

```ts
function parseLines(val: string): string[] {
  return val.split('\n').map((s) => s.trim()).filter(Boolean);
}
```

Extract to a shared utility.

### 17. `formatDate()` in `SessionsView` could be a shared utility

**File:** `views/SessionsView.vue:51-53`

```ts
function formatDate(dateStr: string) {
  return new Date(dateStr).toLocaleString();
}
```

Date formatting will likely be needed in other views as the app grows.

### 18. No loading state shown in list views during initial data fetch

**Files:** All list views

The data grid is rendered immediately with `null` data. There's no spinner or loading indicator while the data is being fetched. The grid likely handles this internally, but a loading state before the grid renders would prevent a flash of empty content.

### 19. `confirm()` used for destructive actions

**Files:** `UserFormView.vue:111`, `RoleFormView.vue:54`, `ClientFormView.vue:103`, `ScopeFormView.vue:71`, `ApiResourceFormView.vue:87`, `SessionsView.vue:40`

Native `confirm()` dialogs break the design language. Consider using a `CoarDialog` or confirmation modal from the UI library for consistency.

### 20. Success messages in SessionsView never clear

**File:** `views/SessionsView.vue`

The `success` ref is set when a session is revoked but never cleared. It persists even after navigating to a different action.

### 21. Consider route-level code splitting names for debugging

**File:** `router/index.ts`

Route definitions lack `name` properties, which makes programmatic navigation and debugging harder:

```ts
// Current
{ path: 'users', component: () => import('@/views/admin/users/UserListView.vue') }

// Better
{ path: 'users', name: 'admin-users', component: () => import('@/views/admin/users/UserListView.vue') }
```

---

## Specific Recommendations

### File-by-file action items

| Priority | File | Action |
|----------|------|--------|
| Critical | `core/api/http.ts` | Add 401 interceptor that redirects to login |
| Critical | `stores/auth.store.ts:11` | Replace `useRouter()` with direct router import |
| Critical | `views/PrivacyView.vue:43` | Replace `prompt()` with proper password input UI |
| Critical | `views/PrivacyView.vue:26,47,57` | Remove unnecessary optional chaining on API calls |
| Should | `styles.css` | Extract shared classes (`.form-group`, `.form-actions`, `.centered`, `.mb-3`, etc.) |
| Should | New: `composables/useAdminForm.ts` | Extract shared form view logic |
| Should | New: `composables/useAdminList.ts` | Extract shared list view logic |
| Should | `views/ProfileView.vue` | Split into 3 section components |
| Should | All list views | Add pagination params to API calls |
| Should | `router/index.ts:134` | Replace catch-all redirect with a 404 view |
| Should | `views/auth/LoginView.vue` | Clear auth error on mount |
| Nice | New: `layouts/AuthLayout.vue` | Extract shared auth page layout |
| Nice | `layouts/AdminLayout.vue` + `MainLayout.vue` | Extract shared sidebar layout CSS |
| Nice | New: `utils/text.ts` | Extract `parseLines()` helper |
| Nice | `router/index.ts` | Add `name` to route definitions |
| Nice | All admin form/list views | Replace `confirm()` with UI dialog |

### Architecture Assessment

**What's done well:**
- Consistent use of Composition API with `<script setup lang="ts">`
- Clean API layer with proper separation (`http.ts` -> `auth-api.ts` / `admin-api.ts`)
- Complete TypeScript models with no `any` types
- Proper route guards with auth and admin role checks
- Auth store is well-designed with clear state machine (`initial` -> `loading` -> `authenticated`/`unauthenticated`)
- `returnUrl` handling for login redirects
- Lazy-loaded route components for code splitting
- `credentials: 'include'` for cookie-based auth

**What needs work:**
- DRY violations are the biggest issue -- 60%+ of CSS and 40%+ of script boilerplate is duplicated
- No global error boundary or 401 handling
- ProfileView complexity (should be 3 components)
- Missing pagination on list endpoints
- Security concern with `prompt()` for password input
