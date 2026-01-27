import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute } from '@angular/router';
import {
  CoarButtonComponent,
  CoarCardComponent,
  CoarNoteComponent,
  CoarIconComponent,
} from '@cocoar/ui';
import { AuthApiService } from '../../../core/services/auth-api.service';
import { catchError, of, finalize } from 'rxjs';

@Component({
  selector: 'app-confirm-email',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    CoarButtonComponent,
    CoarCardComponent,
    CoarNoteComponent,
    CoarIconComponent,
  ],
  template: `
    <div class="confirm-container">
      <coar-card elevated padding="lg" class="confirm-card">
        @if (isLoading()) {
          <div class="loading-state">
            <div class="spinner"></div>
            <p>Confirming your email...</p>
          </div>
        } @else if (error()) {
          <div class="error-state">
            <coar-icon name="x-circle" size="xl" class="error-icon" />
            <h1 class="confirm-title">Confirmation Failed</h1>
            <coar-note color="error" padding="sm">
              {{ error() }}
            </coar-note>
            <p class="help-text">
              The confirmation link may have expired or is invalid.
              Please request a new confirmation email.
            </p>
            <coar-button
              variant="primary"
              routerLink="/login">
              Back to Sign In
            </coar-button>
          </div>
        } @else {
          <div class="success-state">
            <coar-icon name="check-circle" size="xl" class="success-icon" />
            <h1 class="confirm-title">Email Confirmed!</h1>
            <p class="success-text">
              Your email address has been verified successfully.
              You can now sign in to your account.
            </p>
            <coar-button
              variant="primary"
              routerLink="/login">
              Sign In
            </coar-button>
          </div>
        }
      </coar-card>
    </div>
  `,
  styles: `
    .confirm-container {
      min-height: calc(100vh - 120px);
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 2rem;
    }

    .confirm-card {
      width: 100%;
      max-width: 420px;
      text-align: center;
    }

    .confirm-title {
      margin: 1rem 0 0.5rem;
      font-size: 1.5rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .loading-state {
      padding: 2rem 0;
    }

    .spinner {
      width: 48px;
      height: 48px;
      border: 4px solid var(--color-border-primary);
      border-top-color: var(--color-primary);
      border-radius: 50%;
      animation: spin 1s linear infinite;
      margin: 0 auto 1rem;
    }

    @keyframes spin {
      to {
        transform: rotate(360deg);
      }
    }

    .error-state,
    .success-state {
      padding: 1rem 0;
    }

    .error-icon {
      color: var(--color-error);
    }

    .success-icon {
      color: var(--color-success);
    }

    .help-text,
    .success-text {
      margin: 1rem 0 1.5rem;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }
  `,
})
export class ConfirmEmailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly authApi = inject(AuthApiService);

  readonly isLoading = signal(true);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    const userId = this.route.snapshot.queryParams['userId'];
    const token = this.route.snapshot.queryParams['token'];

    if (!userId || !token) {
      this.error.set('Invalid confirmation link. Missing required parameters.');
      this.isLoading.set(false);
      return;
    }

    this.authApi
      .confirmEmail(userId, token)
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(
            err?.error?.message ||
              'Failed to confirm email. The link may have expired.'
          );
          return of(null);
        })
      )
      .subscribe();
  }
}
