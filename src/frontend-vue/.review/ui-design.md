# UI Design Audit Report - Cocoar Auth Vue Frontend

**Date:** 2026-03-06
**Auditor:** UI Design Agent
**Scope:** All .vue files in views/, layouts, NavMenuItem component, styles.css

---

## Summary

The application demonstrates a solid design foundation with consistent use of the `@cocoar/vue-ui` component library and semantic design tokens. The auth pages (login, register, etc.) are well-polished with a centered card layout pattern. The admin section follows a clean list/form paradigm with CoarDataGrid for tables.

**Strengths:**
- Design tokens (`--coar-*`) are used almost universally -- very few hardcoded colors
- Auth pages share a cohesive centered-card aesthetic
- CoarNote is used consistently for error/success feedback
- Loading states with CoarSpinner are present on all data-fetching pages
- The component library is leveraged well (CoarCard, CoarButton, CoarTag, CoarDataGrid)

**Key concerns:**
- Inconsistent max-width values across pages (600px, 700px, 800px, 900px) without a clear rationale
- Typography hierarchy differs between Account pages and Admin pages (different h1 sizes, inconsistent subtitle patterns)
- Duplicated utility CSS classes across nearly every component (`.mb-3`, `.centered`, `.form-group`, etc.)
- Layout styles duplicated between MainLayout and AdminLayout

---

## Critical Issues (must fix)

### 1. Inconsistent page title sizes between Account and Admin sections

Account pages use `font-size: 1.5rem` for h1, while Admin pages use `font-size: 1.25rem`. The Home page uses `font-size: 1.75rem`. This creates a jarring visual difference when navigating between sections.

| Page | h1 font-size | font-weight |
|------|-------------|-------------|
| HomeView | 1.75rem | 700 |
| ProfileView | 1.5rem | 700 |
| SessionsView | 1.5rem | 700 |
| PrivacyView | 1.5rem | 700 |
| Admin list pages | 1.25rem | 700 |
| Admin form pages | 1.25rem | 700 |
| Auth pages | 1.5rem | 600 |

**Recommendation:** Standardize on two tiers: `1.5rem/700` for all main page titles (Account + Admin), and `1.5rem/600` for auth card titles. The Home page "Welcome" heading at `1.75rem` is acceptable as a dashboard hero.

### 2. Inconsistent max-width values across pages

Different pages constrain their content width differently with no apparent system:

| Page | max-width |
|------|-----------|
| HomeView | 900px |
| ProfileView | 700px |
| SessionsView | 800px |
| PrivacyView | 700px |
| UserFormView | 800px |
| RoleFormView | 600px |
| ScopeFormView | 600px |
| ClientFormView | 800px |
| ApiResourceFormView | 700px |
| Admin list pages | none (full height flex) |
| Auth pages (login, etc.) | 420px / 480px (card width) |

**Recommendation:** Establish a system:
- Account content pages: `max-width: 800px` uniformly
- Admin form pages: `max-width: 720px` uniformly (or `800px` for forms with 2-column rows)
- Admin list pages: full width (current behavior is correct)
- Auth cards: keep 420px for simple forms, 480px for forms with name rows

### 3. Page header pattern inconsistency

Some pages use h1 + subtitle, some use just h1, and the pattern differs:
- HomeView: h1 + p subtitle in a `.page-header` div
- ProfileView: bare h1 with no subtitle, no wrapping div
- SessionsView: h1 + action button in `.page-header` flex row
- PrivacyView: bare h1 with no subtitle
- Admin list pages: h1 + subtitle in `.page-header` flex row with action button
- Admin form pages: mixed -- some have subtitle, some do not; RoleFormView has no subtitle

**Recommendation:** Standardize page headers:
- Account pages: always use `.page-header` wrapper with h1 + optional subtitle
- Admin list pages: `.page-header` with h1 + subtitle + action button (already consistent)
- Admin form pages: `.page-header` with h1 + subtitle + Back button (standardize subtitle presence)

---

## Improvements (should fix)

### 4. Duplicated CSS utility classes across components

The following classes are copy-pasted into nearly every component's `<style scoped>`:

- `.mb-3 { margin-bottom: 0.75rem; }` -- appears in 12+ files
- `.mb-4 { margin-bottom: 1rem; }` -- appears in 6+ files
- `.centered { display: flex; justify-content: center; padding: 3rem; }` -- appears in 8+ files
- `.form-group { margin-bottom: 1rem; }` -- appears in 10+ files
- `.form-row-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 0.75rem; margin-bottom: 1rem; }` -- appears in 5+ files
- `.form-actions { display: flex; gap: 0.75rem; }` -- appears in 5+ files
- `.page-title`, `.page-subtitle`, `.page-header` -- duplicated with slight variations

**Recommendation:** Extract common utility classes to `styles.css` or a dedicated `_utilities.css` file. This eliminates duplication and ensures consistency. Keep component-specific styles scoped.

### 5. Layout duplication between MainLayout and AdminLayout

Both layouts have identical CSS for `.app-layout`, `.sidebar-header`, `.sidebar-logo`, `.sidebar-footer`, and `.main-content`. Only the template content differs.

**Recommendation:** Extract shared layout styles to a common CSS file or create a base `AppShell` component that both layouts can use.

### 6. Auth page title inconsistency -- "Reset Password" vs "Set New Password"

The ForgotPasswordView has h1 "Reset Password" and the ResetPasswordView has h1 "Set New Password". This is fine semantically but the ResetPasswordView is missing the `.auth-subtitle` paragraph that all other auth pages have. This breaks visual rhythm.

**Recommendation:** Add a subtitle to ResetPasswordView: "Enter your new password below."

### 7. Session card layout needs refinement

The SessionsView uses CoarCard for each session, which is appropriate. However, the `.session-browser` text and `.session-meta` lack visual hierarchy. The browser name and version are on the same line without clear separation.

**Recommendation:** Consider making the browser name bolder or slightly larger, and adding a subtle icon for the OS/browser type to improve scannability.

### 8. Admin form pages -- Back button inconsistency

Some admin form pages use `icon-start="back"` on the Back button (UserFormView) while others have no icon (RoleFormView, ScopeFormView, ClientFormView, ApiResourceFormView).

**Recommendation:** All admin form Back buttons should consistently use `icon-start="back"` for visual affordance.

### 9. Checkbox grouping in UserFormView

The checkboxes (Active, Lockout Enabled, Email Confirmed, 2FA Enabled) use `display: flex; flex-wrap: wrap; gap: 1rem;` which renders them in a horizontal row. This can feel cramped on narrow viewports and makes the checkboxes feel like they have equal weight.

**Recommendation:** Consider a vertical stack for these toggle-style settings, possibly with a section title like "Account Settings" above them to group them semantically.

### 10. CoarNote for feedback -- missing auto-dismiss

Success messages (e.g., "Profile updated successfully", "Session revoked") persist until the page is navigated away from. They never auto-dismiss.

**Recommendation:** Consider auto-dismissing success notes after 5-8 seconds. Error messages should persist until dismissed or corrected (current behavior is correct).

---

## Nice to Have

### 11. No background color on auth pages

The auth pages (`min-height: 100vh; display: flex; align-items: center; justify-content: center;`) rely on the browser default white background. A subtle background (`--coar-background-neutral-secondary`) would give the centered card more visual lift and separation.

### 12. Sidebar active state relies on global CSS

The active state for sidebar menu items is defined in `styles.css` using `.coar-menu-item.router-link-active` and `.coar-menu-item.active`. This works but couples the global stylesheet to the component library's internal class names. If the library changes its class naming, this breaks silently.

### 13. No transition/animation on page changes

Page transitions between views are instant (bare `<RouterView />`). A subtle fade transition (150-200ms) would feel more polished, especially in the admin section when navigating between list and form views.

### 14. PrivacyView uses `prompt()` for password input

The "Request Account Deletion" flow uses `window.prompt()` to collect the user's password. This is a native browser dialog that cannot be styled, breaks the design language entirely, and is insecure (password is visible in plaintext). This is also flagged in the UX audit but has design implications.

### 15. Empty states in DataGrid views

The admin list pages (UserListView, RoleListView, etc.) delegate empty state rendering to CoarDataGrid. If the grid component does not render a meaningful empty state, the user sees a blank table. There is no explicit empty-state handling in any list view.

**Recommendation:** Verify CoarDataGrid renders an appropriate empty state. If not, add a fallback empty-state message with a CTA (e.g., "No users yet. Create your first user.").

### 16. The `.form-card` class is referenced but never defined

Several admin form views apply `class="form-card"` to CoarCard elements (RoleFormView, ScopeFormView, ClientFormView, etc.) but never define a `.form-card` CSS rule. This class has no effect.

**Recommendation:** Either define `.form-card` with appropriate styles (e.g., margin-bottom) or remove the class from templates.

### 17. Hardcoded fallback colors

Two instances of hardcoded color values exist:
- `styles.css:6` -- `#e6f0ff` and `#0066cc` as fallbacks in `var()` for menu active state
- `HomeView.vue:70-72` -- `#f59e0b` for admin card warning border/icon

These are acceptable as CSS custom property fallbacks, but if the design token system is reliable, the fallbacks could be removed to enforce token usage.

---

## Specific File Changes

### `styles.css`
- Add shared utility classes (`.mb-3`, `.mb-4`, `.centered`, `.form-group`, `.form-row-2`, `.form-actions`, `.page-title`, `.page-subtitle`, `.page-header`) to eliminate cross-component duplication
- Consider adding `.auth-page`, `.auth-card`, `.auth-title`, `.auth-subtitle`, `.auth-footer` base styles here since they are identical across 7 auth views

### `layouts/MainLayout.vue`
- Extract shared layout styles (`.app-layout`, `.sidebar-header`, `.sidebar-logo`, `.sidebar-footer`, `.main-content`) to a common file or base component

### `layouts/AdminLayout.vue`
- Same as MainLayout -- extract shared styles

### `views/HomeView.vue`
- Change `.page-title` font-size from `1.75rem` to `1.5rem` for consistency (or keep if intentional dashboard emphasis)

### `views/ProfileView.vue`
- Wrap h1 in a `.page-header` div for consistency with other pages
- Consider adding a subtitle: "Manage your account details and security settings"

### `views/PrivacyView.vue`
- Wrap h1 in a `.page-header` div for consistency

### `views/auth/ResetPasswordView.vue`
- Add `.auth-subtitle` paragraph below h1: "Enter your new password below."
- Adjust `.auth-title` margin to `0 0 0.5rem` to match other auth pages (currently `0 0 1.5rem` because subtitle is missing)

### `views/admin/roles/RoleFormView.vue`
- Add subtitle to page header: "Update role details" / "Create a new role"
- Add `icon-start="back"` to Back button

### `views/admin/oauth/ScopeFormView.vue`
- Add `icon-start="back"` to Back button

### `views/admin/oauth/ClientFormView.vue`
- Add `icon-start="back"` to Back button

### `views/admin/oauth/ApiResourceFormView.vue`
- Add `icon-start="back"` to Back button

### `views/admin/users/UserFormView.vue`
- Consider stacking checkboxes vertically instead of horizontal flex row

### Admin form pages (all)
- Standardize max-width to `720px` across RoleFormView (600px), ScopeFormView (600px), ApiResourceFormView (700px), UserFormView/ClientFormView (800px)

### Account content pages (ProfileView, SessionsView, PrivacyView)
- Standardize max-width to `800px` (ProfileView and PrivacyView currently use 700px)

---

## Design Token Usage Audit

All files correctly use `--coar-*` design tokens for:
- Text colors: `--coar-text-neutral-primary`, `--coar-text-neutral-secondary`, `--coar-text-accent-primary`
- Borders: `--coar-border-neutral-tertiary`, `--coar-border-accent-primary`
- Backgrounds: `--coar-background-neutral-primary`, `--coar-background-neutral-secondary`
- Radii: `--coar-radius-md`, `--coar-radius-sm`
- Icons: `--coar-icon-accent-primary`

No instances of raw hex colors except the two fallback cases mentioned in item 17. This is excellent token discipline.
