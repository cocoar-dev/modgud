# UX & Interaction Flow Audit

**Auditor:** UX Interactions Specialist
**Date:** 2026-03-06
**Scope:** All Vue views, layouts, router, auth store, and API layer

---

## Summary

The Cocoar Auth Vue frontend is a well-structured application with solid fundamentals: clean auth flows, consistent layout patterns, and a logical information architecture. The auth pages follow modern conventions (centered card, clear titles, loading states, error messages). The admin section has a complete CRUD pattern with data grids.

However, there are several UX gaps that range from critical data-loss risks to missing enterprise-grade polish. The biggest issues are: no unsaved-changes protection on any form, missing confirmation dialogs for single-item destructive actions (revoke session), success messages that never auto-dismiss, no loading/empty states on admin list pages, and an account deletion flow that uses `prompt()` instead of a proper modal. The 2FA setup flow is missing QR code rendering, making authenticator setup unnecessarily difficult.

**Overall UX Grade: B-** -- Functional and clean, but missing guardrails and polish expected in an enterprise identity provider.

---

## Critical Issues (must fix)

### 1. No Unsaved Changes Guard on Any Form
**Impact:** Users can lose work by accidentally navigating away.
**Affected files:**
- `views/ProfileView.vue` -- profile edits, password changes, 2FA setup mid-flow
- `views/admin/users/UserFormView.vue`
- `views/admin/roles/RoleFormView.vue`
- `views/admin/oauth/ClientFormView.vue`
- `views/admin/oauth/ScopeFormView.vue`
- `views/admin/oauth/ApiResourceFormView.vue`

**Problem:** None of these forms track dirty state or use `onBeforeRouteLeave` to warn users. A user editing an OAuth client with multiple URIs configured could lose all work by clicking a sidebar nav item.

**Fix:** Add a `beforeRouteLeave` guard (or `onBeforeRouteLeave` composable) that checks for dirty state and shows a confirmation dialog. Consider creating a shared `useDirtyGuard()` composable.

### 2. Account Deletion Uses `prompt()` -- Poor UX and Potential Security Issue
**File:** `views/PrivacyView.vue:43`
**Problem:** The account deletion flow uses the browser's native `prompt()` to collect the user's password:
```js
const password = prompt('Enter your password to request account deletion:');
```
This is a critical UX issue for several reasons:
- `prompt()` is ugly, unbranded, and inconsistent across browsers
- The password is visible in plain text (not masked)
- Some browsers/extensions block `prompt()` entirely
- It breaks the visual design language of the rest of the app

**Fix:** Replace with a proper modal dialog that includes a `CoarPasswordInput` field, a clear warning message about the consequences, and explicit confirm/cancel buttons.

### 3. 2FA Setup Missing QR Code
**File:** `views/ProfileView.vue:257-272`
**Problem:** The 2FA setup flow shows only the manual key. The API returns `authenticatorUri` (line 76 of `auth.models.ts`) but the view never renders a QR code from it. Most users expect to scan a QR code -- requiring manual key entry is error-prone and dramatically reduces 2FA adoption.

**Fix:** Add a QR code renderer (e.g., `qrcode` npm package or a Vue component) that displays the `setupData.authenticatorUri` as a scannable QR code. Keep the manual key as a fallback.

### 4. No Loading Indicator on Admin Data Grids
**Files:**
- `views/admin/users/UserListView.vue`
- `views/admin/roles/RoleListView.vue`
- `views/admin/oauth/ClientListView.vue`
- `views/admin/oauth/ScopeListView.vue`
- `views/admin/oauth/ApiResourceListView.vue`

**Problem:** Data is loaded in `onMounted` and assigned to a `ref<T[] | null>(null)`. The grid is rendered immediately with `null` data. There is no loading spinner shown while data is being fetched -- the user sees an empty grid until data arrives, which looks broken.

**Fix:** Add `isLoading` state and show a `CoarSpinner` while data is null, similar to how `SessionsView.vue` and `ProfileView.vue` handle loading.

### 5. Session Revoke (Single) Has No Confirmation Dialog
**File:** `views/SessionsView.vue:28-37`
**Problem:** Revoking an individual session happens immediately on button click with no confirmation. While `revokeAll()` correctly uses `confirm()`, the single `revokeSession()` does not. A misclick could terminate an active session on another device.

**Fix:** Add a confirmation dialog before revoking a single session.

---

## Improvements (should fix)

### 6. Success/Error Messages Never Auto-Dismiss
**Affected files:** All views that use `CoarNote` for success/error feedback.
**Problem:** Success messages like "Profile updated successfully" persist indefinitely until the user navigates away. They stack up if the user performs multiple actions (e.g., saves profile, then changes password -- both success messages visible simultaneously in `ProfileView.vue`).

**Fix:**
- Auto-dismiss success messages after 5-8 seconds using `setTimeout`
- Clear previous success messages when a new action begins
- Error messages should remain persistent (user needs time to read/act)

### 7. Login Error Does Not Distinguish Lockout State
**File:** `views/auth/LoginView.vue` + `stores/auth.store.ts`
**Problem:** The `LoginResult` model includes `isLockedOut` and `isNotAllowed` fields, but the login view treats all non-2FA failures identically with a generic error. A locked-out user gets the same "Login failed" message as someone with a wrong password, providing no actionable guidance.

**Fix:** Show specific messages:
- `isLockedOut`: "Your account has been locked due to too many failed attempts. Please try again later or contact an administrator."
- `isNotAllowed`: "Your account is not allowed to sign in. This may be because your email has not been confirmed."

### 8. Register Form Lacks Client-Side Validation
**File:** `views/auth/RegisterView.vue`
**Problem:** Unlike the login form which has `touched` tracking and computed error messages, the register form has no client-side validation at all. It silently does nothing if required fields are empty (`if (!userName.value || !email.value || !password.value) return;`). No visual feedback indicates what's wrong.

**Fix:** Add the same `touched` + computed error pattern used in `LoginView.vue`. Add email format validation. Consider adding password strength indicator.

### 9. Reset Password Page Exposes Email Field Unnecessarily
**File:** `views/auth/ResetPasswordView.vue:11`
**Problem:** The email is pre-populated from the query string but shown as an editable field. The user should not need to (or be able to) change the email on a password reset -- the token is bound to a specific email. Showing it editable is confusing and could lead to errors if modified.

**Fix:** Either make the email field read-only/disabled, or hide it entirely and just use the value from the query string.

### 10. No 404/Not Found Handling
**File:** `router/index.ts:134`
**Problem:** The catch-all route `{ path: '/:pathMatch(.*)*', redirect: '/' }` silently redirects unknown URLs to the home page. Users who mistype a URL or follow a stale link get no feedback about why they ended up at the home page.

**Fix:** Create a `NotFoundView.vue` with a clear "Page not found" message and a link back to home, instead of silently redirecting.

### 11. Admin User Form Missing Available Admin Actions
**File:** `views/admin/users/UserFormView.vue`
**Problem:** The admin API supports several user management actions that have no UI:
- `unlockUser()` -- no button to unlock a locked user
- `resetUserPassword()` -- no way to admin-reset a password
- `softDeleteUser()` / `restoreUser()` -- no soft-delete/restore workflow
- `permanentlyEraseUser()` -- no GDPR erasure workflow
- `getUserSessions()` / `revokeUserSessions()` -- no session management per user

These are all defined in `admin-api.ts` (lines 56-66) but have no corresponding UI elements. An admin managing users cannot unlock a locked account or force a password reset without using API calls directly.

**Fix:** Add an "Actions" section to the user edit form with buttons for unlock, reset password, soft-delete/restore, view sessions, and force logout.

### 12. NavMenuItem Active State Bug for Admin Routes
**File:** `components/NavMenuItem.vue:15-17`
**Problem:** The `isActive` computed uses `startsWith` matching. The "Home" nav item at `/` with `exact: true` works correctly. But the admin "Back to App" link (`to="/"`) in `AdminLayout.vue:30` will show as active for ALL routes since every path starts with `/`. It needs `:exact="true"` but doesn't have it.

Additionally, when on `/admin/roles/create`, both "Users" and "Roles" nav items would need careful prefix matching. The `/admin/users` prefix won't incorrectly match `/admin/roles`, but this pattern is fragile.

**Fix:** Add `:exact="true"` to the "Back to App" NavMenuItem in `AdminLayout.vue`.

### 13. No Pagination UI on Admin Lists
**Files:** All admin list views.
**Problem:** The API supports pagination (`PaginationParams` in models, `buildQuery()` in `admin-api.ts`), and `UserList` returns `totalCount`, `page`, `pageSize`. However, no list view passes pagination params or renders pagination controls. With many users/clients, all data loads at once.

**Fix:** Add pagination controls to list views. Pass `PaginationParams` to the API calls. The `CoarDataGrid` may support server-side pagination natively.

### 14. No Search/Filter on Admin Lists
**Problem:** Same as above -- the API supports `search` parameter but no list view provides a search input. Finding a specific user in a large deployment requires scrolling through the grid.

**Fix:** Add a search input above the data grid that filters via the API's `search` parameter.

### 15. 2FA Login Flow Loses returnUrl When Switching Methods
**File:** `views/auth/TwoFactorLoginView.vue:54`
**Problem:** The "Use a recovery code instead" link is a static `RouterLink to="/login/recovery"`. It does not forward the `returnUrl` query parameter, so after recovery login, the user will be redirected to `/` instead of their original destination.

Similarly, `RecoveryLoginView.vue:50` links back to `/login/2fa` without preserving `returnUrl`.

**Fix:** Make both links preserve the current query parameters:
```html
<RouterLink :to="{ path: '/login/recovery', query: route.query }">
```

### 16. Authenticated Users Cannot Access 2FA Login Pages
**File:** `router/index.ts:20-28`
**Problem:** The 2FA login routes have `meta: { public: true }`. The guard at line 155-157 redirects authenticated users away from public pages. If a user somehow ends up authenticated but needs to complete 2FA (edge case with browser back button), they'd be redirected to `/` without completing the flow.

This is a minor edge case but worth considering. The `requires-2fa` state in the auth store should be checked.

---

## Nice to Have

### 17. Keyboard Shortcut for Form Submission
**Problem:** Admin forms use `@click` on the save button rather than wrapping in a `<form @submit.prevent>`. The `UserFormView.vue`, `RoleFormView.vue`, `ScopeFormView.vue`, and `ApiResourceFormView.vue` don't use `<form>` elements at all -- they're just `<CoarCard>` with inputs and buttons. Pressing Enter in a text field does not submit the form.

**Fix:** Wrap form content in `<form @submit.prevent="onSubmit">` and make the save button `type="submit"`.

### 18. No Breadcrumbs in Admin Section
**Problem:** Admin pages have a "Back" button in the header, but no breadcrumbs. When editing a deeply nested resource (e.g., an API resource), the user has no trail showing where they are: Admin > OAuth > API Resources > Edit "my-api".

**Fix:** Add breadcrumbs to admin form pages. A simple text trail above the page title would suffice.

### 19. No Bulk Actions on Admin Lists
**Problem:** Admin lists don't support row selection or bulk operations. Common enterprise needs like "delete multiple users" or "assign role to multiple users" require opening each one individually.

**Fix:** Add checkbox selection to data grids and a bulk action toolbar (delete, assign role, etc.).

### 20. Data Grid Rows Not Visually Clickable
**Problem:** Admin data grids have `onRowClicked` handlers but rows have no visual indication they're clickable (no hover cursor, no hover highlight visible in the code). Users may not discover that clicking a row navigates to the edit form.

**Fix:** Add `cursor: pointer` and hover background to grid rows. Consider adding a visual "edit" icon in the last column.

### 21. No Toast/Notification System
**Problem:** All feedback is inline `CoarNote` components. After saving an admin entity, the user is immediately redirected to the list page with no success feedback. The redirect happens at `router.push('/admin/users')` -- the list page has no mechanism to show "User created successfully."

**Fix:** Implement a global toast/notification system so success messages persist across navigation.

### 22. Recovery Codes Not Copyable in One Click
**File:** `views/ProfileView.vue:290-293`
**Problem:** Recovery codes are displayed in a grid of individual `<code>` elements. There's no "Copy all" button. Users need to manually select and copy each code.

**Fix:** Add a "Copy all to clipboard" button that copies all codes as a formatted list.

### 23. OAuth Client Form -- Multi-line Input UX
**File:** `views/admin/oauth/ClientFormView.vue:152-158`
**Problem:** Redirect URIs, post-logout URIs, and scopes use `CoarTextInput` with `:rows="3"`. It's unclear from the label alone that these are newline-delimited. The format instruction "(one per line)" is in the label, but a helper text below the input would be clearer.

**Fix:** Use dedicated helper text / description prop below the inputs, or use a tag-input component where users can add/remove URIs individually.

### 24. No Session Timeout Handling
**File:** `core/api/http.ts`
**Problem:** The HTTP client throws `ApiError` on non-200 responses but doesn't specifically handle 401 (session expired). If a user's session expires while they're on an admin page, API calls will fail with generic errors rather than redirecting to login.

**Fix:** Add a global 401 interceptor that clears auth state and redirects to `/login?sessionExpired=true` with a message.

### 25. Setup Page Not Protected by Router Guard
**File:** `router/index.ts:8-11`
**Problem:** The `/setup` route has no `meta` flags. It's not public and not auth-required. The guard logic will try to initialize auth (which may fail if no admin exists yet), then redirect to login. The `SetupView.vue` handles this internally by checking `getSetupStatus()`, but the router-level behavior could be cleaner.

---

## Specific File Changes

| File | Line(s) | Change |
|------|---------|--------|
| `views/PrivacyView.vue` | 43 | Replace `prompt()` with a proper modal dialog for password input |
| `views/ProfileView.vue` | 257-272 | Add QR code rendering for `setupData.authenticatorUri` |
| `views/ProfileView.vue` | all | Add `onBeforeRouteLeave` dirty state guard |
| `views/ProfileView.vue` | 65-66, 84-85 | Auto-dismiss success messages after timeout |
| `views/auth/LoginView.vue` | 29-43 | Handle `isLockedOut` and `isNotAllowed` with specific messages |
| `views/auth/RegisterView.vue` | 20-21 | Add client-side validation with `touched` tracking |
| `views/auth/ResetPasswordView.vue` | 48 | Make email field read-only or hidden |
| `views/auth/TwoFactorLoginView.vue` | 54 | Preserve `returnUrl` in recovery code link |
| `views/auth/RecoveryLoginView.vue` | 50 | Preserve `returnUrl` in 2FA link |
| `views/SessionsView.vue` | 28 | Add confirmation before single session revoke |
| `views/admin/users/UserListView.vue` | all | Add loading spinner; add search input; add pagination |
| `views/admin/users/UserFormView.vue` | all | Add unlock, reset password, soft-delete, sessions actions; add `<form>` wrapper; add dirty guard |
| `views/admin/roles/RoleListView.vue` | all | Add loading spinner |
| `views/admin/roles/RoleFormView.vue` | all | Add `<form>` wrapper; add dirty guard |
| `views/admin/oauth/ClientListView.vue` | all | Add loading spinner |
| `views/admin/oauth/ClientFormView.vue` | all | Add `<form>` wrapper; add dirty guard |
| `views/admin/oauth/ScopeListView.vue` | all | Add loading spinner |
| `views/admin/oauth/ScopeFormView.vue` | all | Add `<form>` wrapper; add dirty guard |
| `views/admin/oauth/ApiResourceListView.vue` | all | Add loading spinner |
| `views/admin/oauth/ApiResourceFormView.vue` | all | Add `<form>` wrapper; add dirty guard |
| `layouts/AdminLayout.vue` | 30 | Add `:exact="true"` to "Back to App" NavMenuItem |
| `router/index.ts` | 134 | Replace catch-all redirect with a NotFoundView |
| `core/api/http.ts` | 33 | Add 401 handler to redirect to login on session expiry |
| `components/NavMenuItem.vue` | -- | Consider adding `aria-current="page"` for accessibility |

---

## Accessibility Notes (inferred from code)

1. **Form labels present** -- All inputs use the `label` prop, which likely renders `<label>` elements. Good.
2. **No ARIA roles on nav** -- `CoarSidebar`/`CoarMenu` are component library elements; accessibility depends on their implementation. Cannot verify from this code.
3. **No skip navigation link** -- No mechanism to skip to main content for keyboard users.
4. **No focus management on route change** -- After navigation, focus is not programmatically moved to the new page's heading. Screen reader users may not know the page changed.
5. **`confirm()` and `prompt()` are accessible** but provide poor UX. Custom modals should use `role="dialog"`, `aria-modal="true"`, and trap focus.
6. **Data grid accessibility** -- Depends entirely on `CoarDataGrid` implementation. Row click handlers should ideally also be keyboard-accessible (Enter/Space).
7. **Loading states** -- `CoarSpinner` should have an `aria-label` or be wrapped in a `role="status"` region.
