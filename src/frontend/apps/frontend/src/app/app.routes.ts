import { Route } from '@angular/router';
import { authGuard, twoFactorGuard, adminGuard } from './core';

/**
 * Edge Router configuration following the Edge Router pattern.
 *
 * Structure:
 * - Public routes are declared explicitly at the edge
 * - Authenticated routes are lazy-loaded behind canMatch guard
 * - Authentication is enforced BEFORE route matching
 *
 * @see .local/routing.md for the full specification
 */
export const appRoutes: Route[] = [
  // ==========================================================================
  // Public routes (no authentication required)
  // ==========================================================================

  // Setup route - only available when no admin exists
  {
    path: 'setup',
    loadComponent: () =>
      import('./features/setup/setup.component').then((m) => m.SetupComponent),
  },

  // Login flow
  {
    path: 'login',
    loadComponent: () =>
      import('./features/auth/login/login.component').then(
        (m) => m.LoginComponent,
      ),
  },

  {
    path: 'login/2fa',
    canActivate: [twoFactorGuard],
    loadComponent: () =>
      import(
        './features/auth/two-factor-login/two-factor-login.component'
      ).then((m) => m.TwoFactorLoginComponent),
  },

  {
    path: 'login/recovery',
    canActivate: [twoFactorGuard],
    loadComponent: () =>
      import('./features/auth/recovery-login/recovery-login.component').then(
        (m) => m.RecoveryLoginComponent,
      ),
  },

  // Registration
  {
    path: 'register',
    loadComponent: () =>
      import('./features/auth/register/register.component').then(
        (m) => m.RegisterComponent,
      ),
  },

  // Password recovery
  {
    path: 'forgot-password',
    loadComponent: () =>
      import('./features/auth/forgot-password/forgot-password.component').then(
        (m) => m.ForgotPasswordComponent,
      ),
  },

  // Password reset (with token from email)
  {
    path: 'reset-password',
    loadComponent: () =>
      import('./features/auth/reset-password/reset-password.component').then(
        (m) => m.ResetPasswordComponent,
      ),
  },

  // Email confirmation (with token from email)
  {
    path: 'confirm-email',
    loadComponent: () =>
      import('./features/auth/confirm-email/confirm-email.component').then(
        (m) => m.ConfirmEmailComponent,
      ),
  },

  // ==========================================================================
  // Admin routes (require admin role)
  // ==========================================================================
  {
    path: 'admin',
    canMatch: [adminGuard],
    loadChildren: () =>
      import('./admin/admin.routes').then((m) => m.adminRoutes),
  },

   // ==========================================================================
  // Authenticated routes (require authentication)
  // Main area is lazy-loaded only when the user is authenticated
  // ==========================================================================
  {
    path: '',
    canMatch: [authGuard],
    loadChildren: () => import('./main/main.routes').then((m) => m.mainRoutes),
  },

  // ==========================================================================
  // Fallback for unknown routes
  // Guard will redirect unauthenticated users to /login
  // ==========================================================================
  {
    path: '**',
    redirectTo: '',
  },
];
