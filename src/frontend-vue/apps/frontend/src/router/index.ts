import { createRouter, createWebHistory } from 'vue-router';
import { useAuthStore } from '@/stores/auth.store';
import { authApi } from '@/core/api/auth-api';
import { realmContext } from '@/composables/useRealmContext';

export const router = createRouter({
  history: createWebHistory(realmContext.baseHref),
  routes: [
    // ── Setup ──────────────────────────────────────────────────
    {
      path: '/setup',
      component: () => import('@/views/SetupView.vue'),
      meta: { public: true },
    },

    // ── Public auth ────────────────────────────────────────────
    {
      path: '/login',
      component: () => import('@/views/auth/LoginView.vue'),
      meta: { public: true },
    },
    {
      path: '/login/2fa',
      component: () => import('@/views/auth/TwoFactorLoginView.vue'),
      meta: { public: true },
    },
    {
      path: '/login/recovery',
      component: () => import('@/views/auth/RecoveryLoginView.vue'),
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
      path: '/consent',
      component: () => import('@/views/auth/ConsentView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/consent/denied',
      component: () => import('@/views/auth/ConsentDeniedView.vue'),
      meta: { public: true },
    },

    // ── Authenticated area (single layout) ─────────────────────
    {
      path: '/',
      component: () => import('@/layouts/MainLayout.vue'),
      meta: { requiresAuth: true },
      children: [
        // Account
        { path: '', component: () => import('@/views/HomeView.vue') },
        { path: 'profile', component: () => import('@/views/ProfileView.vue') },
        { path: 'sessions', component: () => import('@/views/SessionsView.vue') },
        { path: 'privacy', component: () => import('@/views/PrivacyView.vue') },

        // Admin — Realms (System Realm Only)
        { path: 'admin/realms', component: () => import('@/views/admin/realms/RealmListView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/realms/create', component: () => import('@/views/admin/realms/RealmFormView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/realms/:slug', component: () => import('@/views/admin/realms/RealmFormView.vue'), meta: { requiresAdmin: true } },

        // Admin
        { path: 'admin', redirect: '/admin/users' },
        { path: 'admin/users', component: () => import('@/views/admin/users/UserListView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/users/create', component: () => import('@/views/admin/users/UserFormView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/users/:id', component: () => import('@/views/admin/users/UserFormView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/roles', component: () => import('@/views/admin/roles/RoleListView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/roles/create', component: () => import('@/views/admin/roles/RoleFormView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/roles/:id', component: () => import('@/views/admin/roles/RoleFormView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/login-providers', component: () => import('@/views/admin/login-providers/LoginProviderListView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/login-providers/create', component: () => import('@/views/admin/login-providers/LoginProviderFormView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/login-providers/:id', component: () => import('@/views/admin/login-providers/LoginProviderFormView.vue'), meta: { requiresAdmin: true } },
        // OAuth Admin
        { path: 'admin/oauth/clients', component: () => import('@/views/admin/oauth/ClientListView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/oauth/clients/create', component: () => import('@/views/admin/oauth/ClientFormView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/oauth/clients/:id', component: () => import('@/views/admin/oauth/ClientFormView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/oauth/scopes', component: () => import('@/views/admin/oauth/ScopeListView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/oauth/scopes/create', component: () => import('@/views/admin/oauth/ScopeFormView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/oauth/scopes/:id', component: () => import('@/views/admin/oauth/ScopeFormView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/oauth/api-resources', component: () => import('@/views/admin/oauth/ApiResourceListView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/oauth/api-resources/create', component: () => import('@/views/admin/oauth/ApiResourceFormView.vue'), meta: { requiresAdmin: true } },
        { path: 'admin/oauth/api-resources/:id', component: () => import('@/views/admin/oauth/ApiResourceFormView.vue'), meta: { requiresAdmin: true } },
      ],
    },

    // ── Catch-all ──────────────────────────────────────────────
    { path: '/:pathMatch(.*)*', component: () => import('@/views/NotFoundView.vue') },
  ],
});

// Cache setup status to avoid repeated API calls
let setupChecked = false;
let needsSetup = false;

// Navigation guards
router.beforeEach(async (to) => {
  const auth = useAuthStore();

  // Wait for auth to finish initializing (handles concurrent calls)
  await auth.initialize();

  // Check setup status once when not authenticated
  if (!auth.isAuthenticated && !setupChecked && to.path !== '/setup') {
    try {
      const status = await authApi.getSetupStatus();
      needsSetup = status.needsSetup;
    } catch {
      needsSetup = false;
    }
    setupChecked = true;

    if (needsSetup) {
      return '/setup';
    }
  }

  if (to.meta.requiresAdmin && !auth.isAdmin) {
    return auth.isAuthenticated ? '/' : `/login?returnUrl=${encodeURIComponent(to.fullPath)}`;
  }

  if (to.meta.requiresAuth && !auth.isAuthenticated) {
    return `/login?returnUrl=${encodeURIComponent(to.fullPath)}`;
  }

  if (to.meta.public && auth.isAuthenticated) {
    // Don't redirect away from setup if just completed (avoids loop)
    if (to.path === '/setup') {
      return '/';
    }
    return '/';
  }
});
