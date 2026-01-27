import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AuthStateService } from '../services/auth-state.service';
import { environment } from '../../../environments/environment';

/**
 * HTTP interceptor that adds credentials to all API requests
 * and handles 401 Unauthorized responses.
 */
export const credentialsInterceptor: HttpInterceptorFn = (req, next) => {
  const authState = inject(AuthStateService);

  // Only add credentials for requests to our API
  if (req.url.startsWith(environment.apiUrl)) {
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
