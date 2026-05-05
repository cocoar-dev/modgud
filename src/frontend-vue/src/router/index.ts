import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth.store'

let setupChecked = false

const routes = [
    {
      path: '/login',
      component: () => import('@/views/auth/LoginView.vue'),
      meta: { public: true },
    },
    {
      path: '/setup',
      component: () => import('@/views/auth/SetupView.vue'),
      meta: { public: true },
    },
    {
      path: '/forgot-password',
      component: () => import('@/views/auth/ForgotPasswordView.vue'),
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
                    overlayOptions: {
                      size: { minHeight: '80vh', maxHeight: '90vh' },
                    },
                  },
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/user/UserDetails.vue'),
                    overlayOptions: {
                      size: { minHeight: '80vh', maxHeight: '80vh' },
                    },
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
                    overlayOptions: {
                      size: { minHeight: '80vh', maxHeight: '80vh' },
                    },
                  },
                ],
              },
            },
            {
              path: 'auth-log',
              component: () => import('@/views/admin/AuthLogView.vue'),
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
                    overlayOptions: {
                      size: { minHeight: '80vh', maxHeight: '90vh' },
                    },
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
                    overlayOptions: {
                      size: { minHeight: '80vh', maxHeight: '90vh' },
                    },
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
                    overlayOptions: {
                      size: { minHeight: '80vh', maxHeight: '90vh' },
                    },
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
                  },
                ],
              },
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

  // Check setup status once (only if still not authenticated)
  if (!authStore.isAuthenticated && !setupChecked) {
    setupChecked = true
    try {
      const status = await authStore.fetchSetupStatus()
      if (status.NeedsSetup) {
        return '/setup'
      }
    } catch {
      // Setup endpoint not available — continue to login
    }
  }

  // Redirect to login if not authenticated (preserve intended destination)
  if (!authStore.isAuthenticated) {
    const redirect = to.fullPath !== '/' && to.fullPath !== '/dashboard' ? to.fullPath : undefined
    return redirect ? `/login?redirect=${encodeURIComponent(redirect)}` : '/login'
  }

  // Admin routes require *any* admin-resource read permission. The per-resource
  // sidebar gating in AdminView further hides individual menu items the user
  // cannot see; this guard just keeps users with zero admin permissions out of
  // the empty admin shell. `hasPermission` already short-circuits on
  // realm:admin / <app>:admin / <app>:<resource>:admin.
  if (to.path.startsWith('/admin')) {
    const ADMIN_PERMS = [
      'cocoar-auth:user:read', 'cocoar-auth:permission-role:read',
      'cocoar-auth:authorization-group:read',
      'cocoar-auth:oauth-client:read', 'cocoar-auth:oauth-scope:read',
      'cocoar-auth:oauth-api:read',
      'cocoar-auth:login-provider:read',
      'control-plane:realm:read',
      'cocoar-auth:auth-log:read', 'cocoar-auth:session:read',
      'cocoar-auth:app:read',
    ]
    if (!ADMIN_PERMS.some((p) => authStore.hasPermission(p))) {
      return '/dashboard'
    }
  }

  return true
})
