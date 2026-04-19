import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { useAuthStore } from '@/stores/auth.store'

let setupChecked = false

const routes: RouteRecordRaw[] = [
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
    path: '/register',
    component: () => import('@/views/auth/RegisterView.vue'),
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
    path: '/confirm-email',
    component: () => import('@/views/auth/ConfirmEmailView.vue'),
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
        path: 'admin',
        component: () => import('@/views/admin/AdminView.vue'),
        children: [
          { path: '', redirect: '/admin/users' },
          {
            path: 'users',
            component: () => import('@/views/admin/users/UserList.vue'),
          },
          {
            path: 'roles',
            component: () => import('@/views/admin/roles/RoleList.vue'),
          },
          {
            path: 'oauth/clients',
            component: () => import('@/views/admin/oauth/ClientList.vue'),
          },
          {
            path: 'oauth/scopes',
            component: () => import('@/views/admin/oauth/ScopeList.vue'),
          },
          {
            path: 'oauth/apis',
            component: () => import('@/views/admin/oauth/ApiList.vue'),
          },
          {
            path: 'login-providers',
            component: () =>
              import('@/views/admin/login-providers/LoginProviderList.vue'),
          },
          {
            path: 'realms',
            component: () => import('@/views/admin/realms/RealmList.vue'),
          },
          {
            path: 'authorization-groups',
            component: () =>
              import('@/views/admin/authorization-groups/GroupList.vue'),
          },
          {
            path: 'permission-roles',
            component: () =>
              import('@/views/admin/permission-roles/RoleList.vue'),
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
    const redirect =
      to.fullPath !== '/' && to.fullPath !== '/dashboard' ? to.fullPath : undefined
    return redirect ? `/login?redirect=${encodeURIComponent(redirect)}` : '/login'
  }

  return true
})
