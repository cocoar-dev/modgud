import { inject } from '@angular/core';
import { Router, type CanMatchFn } from '@angular/router';
import { map } from 'rxjs/operators';
import { AuthStateService } from '../services/auth-state.service';

/**
 * Guard that requires authentication.
 * Uses canMatch to prevent route matching before authentication is verified.
 * Redirects to login page if not authenticated.
 */
export const authGuard: CanMatchFn = (route, segments) => {
  const authState = inject(AuthStateService);
  const router = inject(Router);

  // Reconstruct the URL from segments
  const url = '/' + segments.map((s) => s.path).join('/');

  const checkAuth = () => {
    if (authState.isAuthenticated()) {
      return true;
    }
    return router.createUrlTree(['/login'], {
      queryParams: { returnUrl: url },
    });
  };

  // If already resolved, check immediately
  const status = authState.status();
  if (status !== 'initial' && status !== 'loading') {
    return checkAuth();
  }

  // Wait for initialization to complete, then check auth status
  return authState.initialize().pipe(map(() => checkAuth()));
};
