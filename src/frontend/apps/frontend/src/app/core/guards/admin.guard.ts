import { inject } from '@angular/core';
import { Router, type CanMatchFn } from '@angular/router';
import { map } from 'rxjs/operators';
import { AuthStateService } from '../services/auth-state.service';

/**
 * Guard that requires admin role.
 * Uses canMatch to prevent route matching for non-admin users.
 * Redirects to home page if not an admin.
 */
export const adminGuard: CanMatchFn = (route, segments) => {
  const authState = inject(AuthStateService);
  const router = inject(Router);

  // Reconstruct the URL from segments
  const url = '/' + segments.map((s) => s.path).join('/');

  const checkAdminAccess = () => {
    if (!authState.isAuthenticated()) {
      // Not authenticated, redirect to login
      return router.createUrlTree(['/login'], {
        queryParams: { returnUrl: url },
      });
    }

    if (authState.isAdmin()) {
      return true;
    }

    // Authenticated but not admin, redirect to home
    return router.createUrlTree(['/']);
  };

  // If already resolved, check immediately
  const status = authState.status();
  if (status !== 'initial' && status !== 'loading') {
    return checkAdminAccess();
  }

  // Wait for initialization to complete, then check admin access
  return authState.initialize().pipe(map(() => checkAdminAccess()));
};
