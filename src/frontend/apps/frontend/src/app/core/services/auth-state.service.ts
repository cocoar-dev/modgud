import { Injectable, inject, signal, computed } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, tap, filter, take, map } from 'rxjs/operators';
import { of, Observable, BehaviorSubject } from 'rxjs';
import { toObservable } from '@angular/core/rxjs-interop';
import { AuthApiService } from './auth-api.service';
import { CurrentUser, LoginRequest, LoginResult } from '../models/auth.models';

export type AuthStatus =
  | 'initial'
  | 'loading'
  | 'authenticated'
  | 'unauthenticated'
  | 'requires-2fa';

@Injectable({
  providedIn: 'root',
})
export class AuthStateService {
  private readonly authApi = inject(AuthApiService);
  private readonly router = inject(Router);

  // Signals for reactive state
  private readonly _currentUser = signal<CurrentUser | null>(null);
  private readonly _status = signal<AuthStatus>('initial');
  private readonly _error = signal<string | null>(null);

  // Public readonly computed signals
  readonly currentUser = this._currentUser.asReadonly();
  readonly status = this._status.asReadonly();
  readonly error = this._error.asReadonly();

  readonly isAuthenticated = computed(
    () => this._status() === 'authenticated' && this._currentUser() !== null
  );

  readonly isLoading = computed(() => this._status() === 'loading');

  readonly isAdmin = computed(() => {
    const user = this._currentUser();
    return user?.roles?.includes('Admin') ?? false;
  });

  readonly requiresTwoFactor = computed(
    () => this._status() === 'requires-2fa'
  );

  /**
   * Observable that emits true when auth state is resolved (not initial/loading).
   * Used by guards to wait for initialization to complete.
   */
  readonly whenReady$: Observable<boolean> = toObservable(this._status).pipe(
    filter((status) => status !== 'initial' && status !== 'loading'),
    take(1),
    map(() => true)
  );

  /**
   * Initialize auth state by checking if user is already logged in.
   * Should be called on app initialization.
   * Returns an Observable that completes when initialization is done.
   */
  initialize(): Observable<boolean> {
    if (this._status() !== 'initial') {
      // Already initialized or initializing
      return this.whenReady$;
    }

    this._status.set('loading');
    this._error.set(null);

    this.authApi
      .getCurrentUser()
      .pipe(
        tap((user) => {
          this._currentUser.set(user);
          this._status.set('authenticated');
        }),
        catchError(() => {
          this._currentUser.set(null);
          this._status.set('unauthenticated');
          return of(null);
        })
      )
      .subscribe();

    return this.whenReady$;
  }

  /**
   * Initialize auth state by checking if user is already logged in.
   * Should be called on app initialization.
   * @deprecated Use initialize() which now returns an Observable
   */
  initializeSync(): void {
    if (this._status() !== 'initial') {
      return;
    }

    this._status.set('loading');
    this._error.set(null);

    this.authApi
      .getCurrentUser()
      .pipe(
        tap((user) => {
          this._currentUser.set(user);
          this._status.set('authenticated');
        }),
        catchError(() => {
          this._currentUser.set(null);
          this._status.set('unauthenticated');
          return of(null);
        })
      )
      .subscribe();
  }

  /**
   * Attempt to login with username and password.
   */
  login(
    request: LoginRequest,
    options?: { redirectTo?: string }
  ): Promise<LoginResult> {
    this._status.set('loading');
    this._error.set(null);

    return new Promise((resolve) => {
      this.authApi
        .login(request)
        .pipe(
          tap((result) => {
            if (result.succeeded) {
              // Login successful, fetch user info
              this.fetchCurrentUser(options?.redirectTo ?? '/');
            } else if (result.requiresTwoFactor) {
              this._status.set('requires-2fa');
            } else {
              this._status.set('unauthenticated');
              this._error.set(
                result.errorMessage ?? 'Login failed. Please try again.'
              );
            }
            resolve(result);
          }),
          catchError((error) => {
            this._status.set('unauthenticated');
            this._error.set(
              error?.error?.message ?? 'Login failed. Please try again.'
            );
            resolve({
              succeeded: false,
              requiresTwoFactor: false,
              isLockedOut: false,
              isNotAllowed: false,
              errorMessage: this._error(),
            } as LoginResult);
            return of(null);
          })
        )
        .subscribe();
    });
  }

  /**
   * Complete 2FA login.
   */
  completeTwoFactorLogin(redirectTo?: string): void {
    this.fetchCurrentUser(redirectTo ?? '/');
  }

  /**
   * Logout the current user.
   */
  logout(redirectTo?: string): void {
    this._status.set('loading');

    this.authApi
      .logout()
      .pipe(
        tap(() => {
          this._currentUser.set(null);
          this._status.set('unauthenticated');
          this._error.set(null);
          this.router.navigate([redirectTo ?? '/login']);
        }),
        catchError(() => {
          // Even if logout fails on server, clear local state
          this._currentUser.set(null);
          this._status.set('unauthenticated');
          this._error.set(null);
          this.router.navigate([redirectTo ?? '/login']);
          return of(null);
        })
      )
      .subscribe();
  }

  /**
   * Refresh current user data from the server.
   */
  refreshUser(): void {
    if (this._status() !== 'authenticated') {
      return;
    }

    this.authApi
      .getCurrentUser()
      .pipe(
        tap((user) => {
          this._currentUser.set(user);
        }),
        catchError(() => {
          // If refresh fails, user session may have expired
          this._currentUser.set(null);
          this._status.set('unauthenticated');
          return of(null);
        })
      )
      .subscribe();
  }

  /**
   * Clear any error state.
   */
  clearError(): void {
    this._error.set(null);
  }

  /**
   * Set an error message to display to the user.
   */
  setError(message: string): void {
    this._error.set(message);
  }

  /**
   * Reset to unauthenticated state (e.g., after session expiry).
   */
  resetToUnauthenticated(): void {
    this._currentUser.set(null);
    this._status.set('unauthenticated');
    this._error.set(null);
  }

  private fetchCurrentUser(redirectTo: string): void {
    this.authApi
      .getCurrentUser()
      .pipe(
        tap((user) => {
          this._currentUser.set(user);
          this._status.set('authenticated');
          this.router.navigate([redirectTo]);
        }),
        catchError(() => {
          this._status.set('unauthenticated');
          this._error.set('Failed to fetch user information.');
          return of(null);
        })
      )
      .subscribe();
  }
}
