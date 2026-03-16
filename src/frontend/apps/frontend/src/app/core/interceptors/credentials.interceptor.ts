import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AuthStateService } from '../services/auth-state.service';
import { RealmContextService } from '../services/realm-context.service';

/**
 * HTTP interceptor that adds credentials to all API requests
 * and handles 401 Unauthorized responses.
 */
export const credentialsInterceptor: HttpInterceptorFn = (req, next) => {
  const authState = inject(AuthStateService);
  const realm = inject(RealmContextService);

  // Only add credentials for requests to our API
  if (req.url.startsWith(realm.apiUrl)) {
    const clonedRequest = req.clone({
      withCredentials: true,
    });

    return next(clonedRequest).pipe(
      catchError((error: HttpErrorResponse) => {
        // Handle 401 Unauthorized - session expired
        if (error.status === 401) {
          authState.resetToUnauthenticated();
        }
        return throwError(() => error);
      })
    );
  }

  return next(req);
};
