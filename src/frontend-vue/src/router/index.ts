import { createRouter, createWebHistory } from 'vue-router'
import type { NavigationGuard } from 'vue-router'
import { useAuthStore } from '@/stores/auth.store'
import { useAppConfigStore } from '@/stores/appconfig.store'

/**
 * Per-route gate for routes that depend on the page-builder feature
 * flag. AppConfig has already loaded by the time the user navigates
 * into /admin/* (the global beforeEach in this file ensures
 * authStore.fetchMe runs first, and the layout root awaits the
 * appConfig load). When the flag is off we redirect to the visible
 * Branding sibling so deep-links don't dead-end on a blank screen.
 */
const pageBuilderFeatureGate: NavigationGuard = () => {
  const appConfig = useAppConfigStore()
  if (!appConfig.config.Features.PageBuilder) {
    return { path: '/plattform/customization/branding', replace: true }
  }
  return true
}

/**
 * Per-modal sizes for routedFragments. The Admin UI is desktop-only —
 * each modal gets a size tailored to its own content density (Scope
 * form has 5 fields; ClientDetails has 8 tabs with 20 inputs).
 *
 * <para>Two principles enforced by the constraint quartet
 * (<c>width</c> + <c>height</c> + the four <c>min/max</c> twins):</para>
 *
 * <list type="number">
 *   <item><description><b>Panel size is content-immune.</b> Tab switches
 *   inside a modal must never resize the modal. The lower bounds
 *   prevent that completely.</description></item>
 *   <item><description><b>Panel size is viewport-respectful.</b> For
 *   large modals we use <c>vw/vh</c> with a <c>maxWidth</c>/
 *   <c>maxHeight</c> cap so the modal grows on big monitors but never
 *   exceeds a sane upper bound. For compact forms we pin <c>width</c>=
 *   <c>minWidth</c>=<c>maxWidth</c> on the same rem value — the panel
 *   is literally that fixed.</description></item>
 * </list>
 *
 * <para>Set on the route rather than inside the Vue component so the
 * sizing contract lives next to the route, and every consumer of a
 * <c>routedFragments</c> list reads it in one place. The component
 * itself is expected to be <c>width:100%; height:100%; display:flex;
 * flex-direction:column; min-height:0</c> at its root — the size
 * decision is the route's, the inner component just fills.</para>
 */

// ── Named modal sizes (UI/UX wave 3) ────────────────────────────────────────
//
// A single named-size contract replaces the per-modal one-offs. Two height
// strategies, chosen by content:
//
//  • cap-to-content (height:auto + minHeight:auto + maxHeight) — the panel
//    sizes to its content and scrolls past the cap. No dead lower half. Use
//    for single-form modals. Proven by the old SERVICE_ACCOUNT size.
//  • stable frame (height==minHeight==maxHeight in vh) — a definite ancestor
//    height for tabbed / grid / editor modals whose flex:1 children
//    (CoarDualListbox, AG-Grid, Monaco, read-only JSON panes) collapse to 0
//    without one. Sized for the heaviest tab.
//
// Big (vw) sizes carry NO minWidth rem floor: a floor wins over the vw
// computation once the viewport is narrower than the floor, overflowing the
// viewport horizontally (tested 2026-05-15 — an 84rem floor cut off the close
// button at 1280px). vw + a maxWidth cap scales to any viewport. SM/MD keep a
// rem min==max because 32/42rem are always below a real admin viewport.

// Cap-to-content single forms. (A 32rem MODAL_SM can be added when a modal of
// ≤4 short fields needs it — none do today.)
const MODAL_MD = {
  width: '42rem', minWidth: '42rem', maxWidth: '42rem',
  height: 'auto', minHeight: 'auto', maxHeight: '85vh',
} as const

// Stable tall frames for tabbed / grid / editor modals.
const MODAL_LG = {
  width: '78vw', maxWidth: '80rem',
  height: '82vh', minHeight: '82vh', maxHeight: '82vh',
} as const

const MODAL_FULL = {
  width: '92vw', maxWidth: '112rem',
  height: '90vh', minHeight: '90vh', maxHeight: '90vh',
} as const

// ── Per-modal assignments ───────────────────────────────────────────────────
// Single forms → cap-to-content (MD); drive the family toward ScopeDetails.
const SCOPE_MODAL_SIZE = MODAL_MD
const REALM_MODAL_SIZE = MODAL_MD
// Role is tabbed (Allgemein / Berechtigungen). FIXED frame so the size never
// changes on tab switch — sized to the taller Permissions tab (its catalog
// checklist scrolls inside). The short Allgemein tab fills the same frame.
const ROLE_MODAL_SIZE = {
  width: '42rem', minWidth: '42rem', maxWidth: '42rem',
  height: '33rem', minHeight: '33rem', maxHeight: '85vh',
} as const
const SERVICE_ACCOUNT_MODAL_SIZE = MODAL_MD

// API modal is now Create=wizard / Edit=tabs. A FIXED frame (like ROLE) so the
// size never jumps between wizard steps or edit tabs — sized to the tallest
// content (the linkage permission checklist + the review list); shorter
// steps/tabs fill the same frame, taller ones scroll internally.
const API_MODAL_SIZE = {
  width: '46rem', minWidth: '46rem', maxWidth: '46rem',
  height: '38rem', minHeight: '38rem', maxHeight: '85vh',
} as const

// Tabbed editors / read-only multi-pane → stable tall frame.
const IDP_CLAIMS_MODAL_SIZE = MODAL_LG
const LOGIN_PROVIDER_MODAL_SIZE = MODAL_LG
const SCHEDULED_JOB_MODAL_SIZE = MODAL_LG
const CONSISTENCY_CHECK_MODAL_SIZE = MODAL_LG
// User: cap-to-content at a moderate fluid width. The CREATE form has no tabs, so
// it stays compact (no dead lower half — the owner's #1 complaint). EDIT is tabbed
// (General/Groups/Effektiv/Security); to keep the modal from resizing on tab switch,
// the component pins a fixed body height in edit mode (.user-edit-frame in
// UserDetails) so every tab fills the same height. vw width + maxWidth cap, no
// minWidth rem floor (viewport-overflow gotcha).
const USER_MODAL_SIZE = {
  width: '64vw', maxWidth: '58rem',
  height: 'auto', minHeight: 'auto', maxHeight: '85vh',
} as const
// Group is a tabbed editor (General form + Members/Roles dual-listboxes + Monaco
// script + effective lists). FIXED frame so the size never changes on tab switch —
// a modal keeps the size it opened at. The editor tabs (.flex-section flex:1) fill
// the frame; the tall General form fills it too (52rem keeps it narrow enough for
// the form yet fine for the two-column dual-listboxes).
const GROUP_MODAL_SIZE = {
  width: '60vw', maxWidth: '52rem',
  height: '80vh', minHeight: '80vh', maxHeight: '80vh',
} as const

// Heaviest builders (wide AG-Grid catalog / 6-tab client builder) → full.
const APP_MODAL_SIZE = MODAL_FULL
const APP_SETTINGS_MODAL_SIZE = MODAL_MD
const CLIENT_MODAL_SIZE = MODAL_FULL

const routes = [
    {
      path: '/login',
      component: () => import('@/views/auth/LoginView.vue'),
      meta: { public: true },
    },
    // The /setup wizard surface was removed in C15d. First-admin onboarding
    // now goes through a CP-issued bootstrap-invite (SPA: /bootstrap?token=…)
    // or the recovery-CLI `bootstrap-admin` command — both grounded in
    // explicit trust (CP-admin or filesystem) instead of a race-window
    // anonymous endpoint.
    {
      path: '/forgot-password',
      component: () => import('@/views/auth/ForgotPasswordView.vue'),
      meta: { public: true },
    },
    {
      path: '/register',
      component: () => import('@/views/auth/RegisterView.vue'),
      meta: { public: true },
    },
    {
      // OAuth consent prompt — server-side ticket flow.
      // /connect/authorize creates a ConsentTicket, redirects here
      // with ?ticket=<id>. The view itself is public (no auth-gate),
      // but the underlying /connect/consent API call demands the
      // session cookie. 401 from that endpoint bounces back to /login.
      path: '/consent',
      component: () => import('@/views/auth/ConsentView.vue'),
      meta: { public: true },
    },
    {
      // OAuth 2.0 Device Authorization Grant (RFC 8628) verification page.
      // A device shows the user "go to <host>/device" + a user code; the user
      // lands here either directly (then types the code) or via
      // /connect/verify's redirect (?ticket=<id>, code captured server-side).
      // Public route; the /connect/device-verification API demands the cookie.
      path: '/device',
      component: () => import('@/views/auth/DeviceVerifyView.vue'),
      meta: { public: true },
    },
    {
      path: '/reset-password',
      component: () => import('@/views/auth/ResetPasswordView.vue'),
      meta: { public: true },
    },
    {
      // C15b — first-admin bootstrap form. Recipient lands here from the
      // magic-link in the bootstrap email; sets a password; auto-login.
      path: '/bootstrap',
      component: () => import('@/views/auth/BootstrapView.vue'),
      meta: { public: true },
    },
    {
      path: '/magic-login',
      component: () => import('@/views/auth/MagicLoginView.vue'),
      meta: { public: true },
    },
    {
      path: '/verify-email',
      component: () => import('@/views/profile/VerifyEmailView.vue'),
      meta: { public: true },
    },
    {
      path: '/',
      component: () => import('@/layouts/MainLayout.vue'),
      children: [
        { path: '', redirect: '/dashboard' },
        {
          path: 'dashboard',
          component: () => import('@/views/dashboard/DashboardView.vue'),
        },
        {
          path: 'profile',
          component: () => import('@/views/profile/ProfileView.vue'),
        },
        {
          // Self-service grace interstitial: shown right after a self-pending
          // user logs in (LoginView routes here before the app redirect). The
          // ?redirect= query carries where to continue once they decide.
          path: 'deletion-pending',
          component: () => import('@/views/profile/DeletionPendingView.vue'),
        },
        // Admin routes (permission check in route guard)
        {
          path: 'admin',
          component: () => import('@/views/admin/AdminView.vue'),
          children: [
            { path: '', redirect: '/admin/users' },
            {
              path: 'users',
              component: () => import('@/views/admin/user/UserList.vue'),
              meta: {
                routedFragments: [
                  {
                    type: 'modal',
                    path: 'claims/:id',
                    component: () => import('@/views/admin/user/IdpClaimsModal.vue'),
                    overlayOptions: { size: IDP_CLAIMS_MODAL_SIZE },
                  },
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/user/UserDetails.vue'),
                    overlayOptions: { size: USER_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              path: 'service-accounts',
              component: () => import('@/views/admin/serviceAccount/ServiceAccountsView.vue'),
              meta: {
                routedFragments: [
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/serviceAccount/ServiceAccountDetails.vue'),
                    overlayOptions: { size: SERVICE_ACCOUNT_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              path: 'roles',
              component: () => import('@/views/admin/role/RoleList.vue'),
              meta: {
                routedFragments: [
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/role/RoleDetails.vue'),
                    overlayOptions: { size: ROLE_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              path: 'groups',
              component: () => import('@/views/admin/group/GroupList.vue'),
              meta: {
                routedFragments: [
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/group/GroupDetails.vue'),
                    overlayOptions: { size: GROUP_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              // Combined logs home — Audit + Security as tabs.
              path: 'logs',
              component: () => import('@/views/admin/AdminLogsView.vue'),
            },
            // Back-compat: the two surfaces used to be separate routes. Keep the
            // links working by redirecting onto the matching tab.
            {
              path: 'auth-log',
              redirect: { path: '/admin/logs', query: { tab: 'security' } },
            },
            {
              path: 'audit',
              redirect: { path: '/admin/logs', query: { tab: 'audit' } },
            },
            {
              path: 'change-requests',
              component: () => import('@/views/admin/ChangeRequestsView.vue'),
            },
            {
              path: 'oauth/clients',
              component: () => import('@/views/admin/oauth/ClientList.vue'),
              meta: {
                routedFragments: [
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/oauth/ClientDetails.vue'),
                    overlayOptions: { size: CLIENT_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              path: 'oauth/scopes',
              component: () => import('@/views/admin/oauth/ScopeList.vue'),
              meta: {
                routedFragments: [
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/oauth/ScopeDetails.vue'),
                    overlayOptions: { size: SCOPE_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              path: 'oauth/apis',
              component: () => import('@/views/admin/oauth/ApiList.vue'),
              meta: {
                routedFragments: [
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/oauth/ApiDetails.vue'),
                    overlayOptions: { size: API_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              // ADR-0012 — app-scoped registration invite codes (the InviteCode posture).
              path: 'invite-codes',
              component: () => import('@/views/admin/inviteCodes/InviteCodeList.vue'),
              meta: {
                routedFragments: [
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/inviteCodes/BulkMintModal.vue'),
                    overlayOptions: { size: MODAL_MD },
                  },
                ],
              },
            },
            {
              path: 'login-providers',
              component: () => import('@/views/admin/login-providers/LoginProviderList.vue'),
              meta: {
                routedFragments: [
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/login-providers/LoginProviderDetails.vue'),
                    overlayOptions: { size: LOGIN_PROVIDER_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              path: 'realms',
              component: () => import('@/views/admin/realms/RealmList.vue'),
              meta: {
                routedFragments: [
                  {
                    // The :id slot is reused as `slug` inside RealmDetails — realms are
                    // addressed by slug throughout the API.
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/realms/RealmDetails.vue'),
                    overlayOptions: { size: REALM_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              path: 'apps',
              component: () => import('@/views/admin/apps/AppList.vue'),
              meta: {
                routedFragments: [
                  {
                    // The :id slot is the App's Id (or "create"). Slug is
                    // immutable post-creation but stored on the dto.
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/apps/AppDetails.vue'),
                    overlayOptions: { size: APP_MODAL_SIZE },
                  },
                  {
                    // ADR-0011 — per-App settings overrides. Two-segment path so
                    // it never collides with the single-segment `:id` App modal.
                    type: 'modal',
                    path: 'settings/:id',
                    component: () => import('@/views/admin/apps/ApplicationSettingsModal.vue'),
                    overlayOptions: { size: APP_SETTINGS_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              path: 'realm-settings',
              component: () => import('@/views/admin/RealmSettingsView.vue'),
            },
            {
              path: 'scheduled-jobs',
              component: () => import('@/views/admin/scheduledJobs/ScheduledJobList.vue'),
              meta: {
                routedFragments: [
                  {
                    // :id slot carries the job Key (e.g. "dcr-gc").
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/scheduledJobs/ScheduledJobDetails.vue'),
                    overlayOptions: { size: SCHEDULED_JOB_MODAL_SIZE },
                  },
                ],
              },
            },
          ],
        },
        // Plattform — second top-level admin area for operator-facing config
        // (Anpassung + Betrieb). Own SubNavLayoutGrouped wrapper.
        {
          path: 'plattform',
          component: () => import('@/views/platform/PlatformView.vue'),
          children: [
            { path: '', redirect: '/plattform/customization/branding' },
            {
              path: 'customization/branding',
              component: () => import('@/views/admin/customization/BrandingView.vue'),
            },
            {
              path: 'customization/pages',
              component: () => import('@/views/admin/customization/PagesView.vue'),
              beforeEnter: pageBuilderFeatureGate,
            },
            {
              path: 'customization/pages/:slug',
              component: () => import('@/views/admin/customization/PageEditorView.vue'),
              beforeEnter: pageBuilderFeatureGate,
            },
            {
              path: 'customization/assets',
              component: () => import('@/views/admin/assets/AssetsView.vue'),
            },
            {
              path: 'observability',
              component: () => import('@/views/admin/AdminObservabilityView.vue'),
            },
            {
              path: 'inbox-settings',
              component: () => import('@/views/admin/InboxSettingsView.vue'),
            },
            {
              path: 'settings',
              component: () => import('@/views/admin/AppSettingsView.vue'),
              meta: {
                routedFragments: [
                  {
                    type: 'modal',
                    path: 'consistency-check',
                    component: () => import('@/views/admin/ConsistencyCheckModal.vue'),
                    overlayOptions: { size: CONSISTENCY_CHECK_MODAL_SIZE },
                  },
                ],
              },
            },
          ],
        },
      ],
    },
    { path: '/:pathMatch(.*)*', redirect: '/dashboard' },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
})

router.beforeEach(async (to) => {
  // Public routes don't need auth
  if (to.meta.public) return true

  const authStore = useAuthStore()

  // Try to fetch current user if not authenticated
  if (!authStore.isAuthenticated) {
    await authStore.fetchMe()
  }

  // Setup-status probe + /setup redirect were removed in C15d. The
  // first-admin onboarding now goes through CP-issued bootstrap-invites
  // (which land at /bootstrap?token=…); a fresh deployment with no admin
  // simply shows the login screen — the operator runs
  // `dotnet Modgud.Api.dll recover bootstrap-admin --email …` once
  // and onboards via the printed magic-link.

  // Redirect to login if not authenticated (preserve intended destination)
  if (!authStore.isAuthenticated) {
    const redirect = to.fullPath !== '/' && to.fullPath !== '/dashboard' ? to.fullPath : undefined
    return redirect ? `/login?redirect=${encodeURIComponent(redirect)}` : '/login'
  }

  // Admin routes require *any* admin-resource read permission. The per-resource
  // sidebar gating in AdminView further hides individual menu items the user
  // cannot see; this guard just keeps users with zero admin permissions out of
  // the empty admin shell. `hasPermission` short-circuits on realm:admin and
  // <resource>:admin. Strings are bare 2-segment form (modgud context is
  // implicit; realm:read is control-plane).
  if (to.path.startsWith('/admin')) {
    const ADMIN_PERMS = [
      'user:read', 'permission-role:read',
      'authorization-group:read',
      'oauth-client:read', 'oauth-scope:read',
      'oauth-api:read',
      'login-provider:read',
      'realm:read', 'realm-settings:read',
      'auth-log:read', 'audit-log:read', 'session:read', 'observability:read', 'asset:read',
      'app:read',
    ]
    if (!ADMIN_PERMS.some((p) => authStore.hasPermission(p))) {
      return '/dashboard'
    }
  }

  return true
})
