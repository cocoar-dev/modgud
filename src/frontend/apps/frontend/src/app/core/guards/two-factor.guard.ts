import { inject } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import { AuthStateService } from '../services/auth-state.service';

/**
 * Guard for 2FA pages.
 * Only allows access when user needs to complete 2FA.
 */
export const twoFactorGuard: CanActivateFn = () => {
  const authState = inject(AuthStateService);
  const router = inject(Router);

  if (authState.requiresTwoFactor()) {
    return true;
  }

  // If already authenticated, go to home
  if (authState.isAuthenticated()) {
    return router.createUrlTree(['/']);
  }

  // Otherwise, go to login
  return router.createUrlTree(['/login']);
};
