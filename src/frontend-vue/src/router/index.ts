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

// Compact forms — fixed rem widths, no flex.
const SCOPE_MODAL_SIZE = {
  width: '44rem', minWidth: '44rem', maxWidth: '44rem',
  height: '70vh', minHeight: '70vh', maxHeight: '70vh',
} as const

const REALM_MODAL_SIZE = {
  width: '50rem', minWidth: '50rem', maxWidth: '50rem',
  height: '72vh', minHeight: '72vh', maxHeight: '72vh',
} as const

const ROLE_MODAL_SIZE = {
  width: '56rem', minWidth: '56rem', maxWidth: '56rem',
  height: '72vh', minHeight: '72vh', maxHeight: '72vh',
} as const

const IDP_CLAIMS_MODAL_SIZE = {
  width: '60rem', minWidth: '60rem', maxWidth: '60rem',
  height: '72vh', minHeight: '72vh', maxHeight: '72vh',
} as const

const SERVICE_ACCOUNT_MODAL_SIZE = {
  width: '52rem', minWidth: '52rem', maxWidth: '52rem',
  height: 'auto', minHeight: 'auto', maxHeight: '80vh',
} as const

const API_MODAL_SIZE = {
  width: '64rem', minWidth: '64rem', maxWidth: '64rem',
  height: '78vh', minHeight: '78vh', maxHeight: '78vh',
} as const

// Big modals — pure vw/vh with a maxWidth cap, NO minWidth floor.
//
// A minWidth floor wins over the vw computation as soon as the viewport
// is smaller than the floor — the modal then overflows the viewport
// horizontally. Tested 2026-05-15 with ClientDetails: an 84rem floor
// (1344px) broke at 1280px viewport (the right edge cut off, including
// the close button). The vw-only approach scales naturally to any
// viewport size: 92vw on 1920px = 1766px; 92vw on 1280px = 1178px;
// 92vw on 4K (3840px) = 3533px — and the maxWidth cap stops it from
// becoming silly on ultrawides.
//
// vw isn't capped against viewport overflow by definition, so on
// every viewport the modal sits comfortably with a backdrop margin on
// each side. No need for a floor; if content is genuinely too wide
// for a tiny viewport, the content scrolls inside its tab panel — the
// modal frame stays correctly sized.
const LOGIN_PROVIDER_MODAL_SIZE = {
  width: '80vw', maxWidth: '90rem',
  height: '82vh', minHeight: '82vh', maxHeight: '82vh',
} as const

const SCHEDULED_JOB_MODAL_SIZE = {
  width: '70vw', maxWidth: '80rem',
  height: '80vh', minHeight: '80vh', maxHeight: '80vh',
} as const

const APP_MODAL_SIZE = {
  width: '85vw', maxWidth: '100rem',
  height: '88vh', minHeight: '88vh', maxHeight: '88vh',
} as const

const USER_MODAL_SIZE = {
  width: '90vw', maxWidth: '110rem',
  height: '90vh', minHeight: '90vh', maxHeight: '90vh',
} as const

const GROUP_MODAL_SIZE = {
  width: '90vw', maxWidth: '110rem',
  height: '90vh', minHeight: '90vh', maxHeight: '90vh',
} as const

// Read-only diagnostic modal — a bit smaller than the editor modals
// because there are no nested tabs, no script editor, no big grids.
// Tall enough to show the per-check accordion without forcing the
// outer scrollbar in typical (1080p) viewports.
const CONSISTENCY_CHECK_MODAL_SIZE = {
  width: '60rem', minWidth: '60rem', maxWidth: '60rem',
  height: '80vh', minHeight: '80vh', maxHeight: '80vh',
} as const

const CLIENT_MODAL_SIZE = {
  width: '92vw', maxWidth: '120rem',
  height: '92vh', minHeight: '92vh', maxHeight: '92vh',
} as const

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
              path: 'auth-log',
              component: () => import('@/views/admin/AuthLogView.vue'),
            },
            {
              path: 'audit',
              component: () => import('@/views/admin/AuditLogView.vue'),
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
