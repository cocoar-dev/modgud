import { createRouter, createWebHistory } from 'vue-router'
import type { NavigationGuard, RouteLocationGeneric } from 'vue-router'
import { useAuthStore } from '@/stores/auth.store'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { MODAL_MD, MODAL_LG, MODAL_FULL } from './modal-sizes'

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
    return { path: '/platform/customization/branding', replace: true }
  }
  return true
}

const authPageSlotGate: NavigationGuard = (to) => {
  const slug = typeof to.params.slug === 'string' ? to.params.slug : ''
  return ['login', 'logout', 'password-forgot'].includes(slug)
    ? true
    : { path: '/platform/customization/pages', replace: true }
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

// ── Per-modal assignments ───────────────────────────────────────────────────
// The named sizes themselves live in ./modal-sizes so modals opened from
// inside another modal (useModalOverlay) can reuse the same values.
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

// The 6-tab client builder (persistent identity column + two-column
// DualListboxes) genuinely earns the full frame. The app modal does not:
// even for user apps a settings form + a 3-column permission catalog fit a
// tall LG frame, and 112rem/90vh only left system apps (read-only, a few
// fields) marooned in empty space. LG keeps the grid its definite height.
const APP_MODAL_SIZE = MODAL_LG
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
      path: '/logged-out',
      component: () => import('@/views/auth/LoggedOutView.vue'),
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
          meta: {
            routedFragments: [
              {
                type: 'modal',
                path: 'change-password',
                component: () => import('@/views/profile/ChangePasswordModal.vue'),
                overlayOptions: { size: MODAL_MD },
              },
              {
                type: 'modal',
                path: 'mfa-setup',
                component: () => import('@/views/auth/MfaSetupModal.vue'),
                overlayOptions: { size: MODAL_MD },
              },
            ],
          },
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
                    // Literal first so it wins over the :id code-details fragment.
                    type: 'modal',
                    path: 'mint',
                    component: () => import('@/views/admin/inviteCodes/BulkMintModal.vue'),
                    overlayOptions: { size: MODAL_MD },
                  },
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/inviteCodes/InviteCodeDetailsModal.vue'),
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
                    // The :id slot is the App's Id (or "create"). One modal for the
                    // whole App: identity + permission catalog + ADR-0011 settings.
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/apps/AppDetails.vue'),
                    overlayOptions: { size: APP_MODAL_SIZE },
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
        // Platform — second top-level admin area for operator-facing config
        // (customization + operations). Own SubNavLayoutGrouped wrapper.
        {
          path: 'platform',
          component: () => import('@/views/platform/PlatformView.vue'),
          children: [
            { path: '', redirect: '/platform/customization/branding' },
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
              // :variantId is a variant GUID, or the literal "new" to author one.
              path: 'customization/pages/:slug/:variantId',
              component: () => import('@/views/admin/customization/PageEditorView.vue'),
              beforeEnter: [pageBuilderFeatureGate, authPageSlotGate],
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
        // Back-compat: /plattform was the old (German) segment for the route
        // above; redirect old bookmarks/links onto the equivalent /platform
        // path, preserving any trailing segments, query and hash.
        {
          path: 'plattform/:rest(.*)*',
          redirect: (to: RouteLocationGeneric) => {
            const rest = to.params.rest
            const suffix = Array.isArray(rest) && rest.length > 0 ? `/${rest.join('/')}` : ''
            return { path: `/platform${suffix}`, query: to.query, hash: to.hash }
          },
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
      'auth-log:read', 'audit-log:read', 'platform-audit:read', 'session:read', 'observability:read', 'asset:read',
      'app:read',
    ]
    if (!ADMIN_PERMS.some((p) => authStore.hasPermission(p))) {
      return '/dashboard'
    }
  }

  return true
})
