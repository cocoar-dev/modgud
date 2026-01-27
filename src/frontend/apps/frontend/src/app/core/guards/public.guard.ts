import { inject } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import { map } from 'rxjs/operators';
import { AuthStateService } from '../services/auth-state.service';

/**
 * Guard for public-only pages (login, register).
 * Redirects to home page if already authenticated.
 * Allows access if in requires-2fa state (for 2FA login flow).
 */
export const publicGuard: CanActivateFn = () => {
  const authState = inject(AuthStateService);
  const router = inject(Router);

  const checkPublicAccess = () => {
    // Allow access if not authenticated or requires 2FA
    if (!authState.isAuthenticated() || authState.requiresTwoFactor()) {
      return true;
    }

    // Already authenticated, redirect to home
    return router.createUrlTree(['/']);
  };

  // If already resolved, check immediately
  const status = authState.status();
  if (status !== 'initial' && status !== 'loading') {
    return checkPublicAccess();
  }

  // Wait for initialization to complete, then check access
  return authState.initialize().pipe(map(() => checkPublicAccess()));
};
