import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth.store'

/**
 * Fixed size for every admin-modal opened via routedFragments.
 *
 * <para>The admin surface is desktop-only — using a single size for
 * every modal keeps the dialog from resizing when the user switches
 * between tabs (some tabs have one CoarTextInput, others have a full
 * DualListbox). Without a fixed size that flickering is jarring; with
 * one the layout feels stable.</para>
 *
 * <para>Set on the route (not the ModalLayout) so the size is the
 * routing layer's contract: every entry of every grid opens at the
 * same dimensions, regardless of which view component renders inside.
 * Per-view <c>width</c> overrides on <c>ModalLayout</c> are
 * intentionally ignored.</para>
 */
const ADMIN_MODAL_SIZE = { width: '80rem', height: '80vh' } as const

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
          path: 'confirm-deletion',
          component: () => import('@/views/profile/ConfirmDeletionView.vue'),
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
                    overlayOptions: { size: ADMIN_MODAL_SIZE },
                  },
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/user/UserDetails.vue'),
                    overlayOptions: { size: ADMIN_MODAL_SIZE },
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
                    overlayOptions: { size: ADMIN_MODAL_SIZE },
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
                    overlayOptions: { size: ADMIN_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              path: 'auth-log',
              component: () => import('@/views/admin/AuthLogView.vue'),
            },
            {
              path: 'observability',
              component: () => import('@/views/admin/AdminObservabilityView.vue'),
            },
            {
              path: 'customization/assets',
              component: () => import('@/views/admin/assets/AssetsView.vue'),
            },
            {
              path: 'customization/branding',
              component: () => import('@/views/admin/customization/BrandingView.vue'),
            },
            {
              path: 'customization/pages',
              component: () => import('@/views/admin/customization/PagesView.vue'),
            },
            {
              path: 'customization/pages/:slug',
              component: () => import('@/views/admin/customization/PageEditorView.vue'),
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
                    overlayOptions: { size: ADMIN_MODAL_SIZE },
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
                    overlayOptions: { size: ADMIN_MODAL_SIZE },
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
                    overlayOptions: { size: ADMIN_MODAL_SIZE },
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
                    overlayOptions: { size: ADMIN_MODAL_SIZE },
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
                    overlayOptions: { size: ADMIN_MODAL_SIZE },
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
                    overlayOptions: { size: ADMIN_MODAL_SIZE },
                  },
                ],
              },
            },
            {
              path: 'realm-settings',
              component: () => import('@/views/admin/RealmSettingsView.vue'),
            },
            {
              path: 'settings',
              component: () => import('@/views/admin/AppSettingsView.vue'),
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
  // `dotnet Cocoar.Auth.Api.dll recover bootstrap-admin --email …` once
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
  // <resource>:admin. Strings are bare 2-segment form (cocoar-auth context is
  // implicit; realm:read is control-plane).
  if (to.path.startsWith('/admin')) {
    const ADMIN_PERMS = [
      'user:read', 'permission-role:read',
      'authorization-group:read',
      'oauth-client:read', 'oauth-scope:read',
      'oauth-api:read',
      'login-provider:read',
      'realm:read', 'realm-settings:read',
      'auth-log:read', 'session:read', 'observability:read', 'asset:read',
      'app:read',
    ]
    if (!ADMIN_PERMS.some((p) => authStore.hasPermission(p))) {
      return '/dashboard'
    }
  }

  return true
})
