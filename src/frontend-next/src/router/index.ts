import { RoutedOverlayFragment } from 'node_modules/@cocoar/vue-fragment-parser/dist/composables/useRoutedModals'
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
          meta: {
            routedFragments: [
              {
                type: 'modal',
                path: ':todoId',
                component: () => import('@/views/todo/TodoDetails.vue'),
                overlayOptions: { size: { minHeight: '80vh', maxHeight: '80vh' } },
              },
            ] satisfies RoutedOverlayFragment[],
          },
        },
        {
          path: 'profile',
          component: () => import('@/views/profile/ProfileView.vue'),
        },
        {
          path: 'todos',
          component: () => import('@/views/todo/TodoTableView.vue'),
          meta: {
            routedFragments: [
              {
                type: 'modal',
                path: ':todoId',
                component: () => import('@/views/todo/TodoDetails.vue'),
                overlayOptions: {size: {minHeight: '90vh', maxHeight: '90vh'}}
              },
            ] satisfies RoutedOverlayFragment[],
          },
        },
        // Customers (top-level, not admin-only)
        {
          path: 'customers',
          component: () => import('@/views/customer/CustomerList.vue'),
          meta: {
            routedFragments: [
              {
                type: 'modal',
                path: ':id',
                component: () => import('@/views/customer/CustomerDetails.vue'),
              },
            ],
          },
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
              path: 'simulator',
              component: () => import('@/views/admin/AuthorizationSimulatorView.vue'),
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
              path: 'idp-config',
              component: () => import('@/views/admin/idp-config/IdpConfigList.vue'),
              meta: {
                routedFragments: [
                  {
                    type: 'modal',
                    path: ':id',
                    component: () => import('@/views/admin/idp-config/IdpConfigDetails.vue'),
                    overlayOptions: {
                      size: { minHeight: '80vh', maxHeight: '90vh' },
                    },
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

  // Admin routes require app:admin permission
  if (to.path.startsWith('/admin') && !authStore.hasPermission('app:admin')) {
    return '/todos'
  }

  return true
})
