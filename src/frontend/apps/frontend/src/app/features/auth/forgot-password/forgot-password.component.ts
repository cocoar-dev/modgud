import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterModule } from '@angular/router';
import {
  CoarButtonComponent,
  CoarTextInputComponent,
  CoarCardComponent,
  CoarNoteComponent,
} from '@cocoar/ui';
import { AuthApiService } from '../../../core/services/auth-api.service';
import { catchError, of, finalize } from 'rxjs';

@Component({
  selector: 'app-forgot-password',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    RouterModule,
    CoarButtonComponent,
    CoarTextInputComponent,
    CoarCardComponent,
    CoarNoteComponent,
  ],
  template: `
    <div class="forgot-container">
      <coar-card elevated padding="lg" class="forgot-card">
        <h1 class="forgot-title">Forgot Password</h1>
        <p class="forgot-subtitle">
          Enter your email address and we'll send you a link to reset your password.
        </p>

        @if (error()) {
          <coar-note color="error" padding="sm" class="error-note">
            {{ error() }}
          </coar-note>
        }

        @if (success()) {
          <coar-note color="success" padding="sm" class="success-note">
            {{ success() }}
          </coar-note>
          <p class="back-link">
            <a routerLink="/login">Back to Sign In</a>
          </p>
        } @else {
          <form [formGroup]="form" (ngSubmit)="onSubmit()">
            <div class="form-group">
              <coar-text-input
                label="Email"
                placeholder="your&#64;email.com"
                formControlName="email"
                autocomplete="email"
                [required]="true"
                [error]="getFieldError('email')" />
            </div>

            <coar-button
              type="submit"
              variant="primary"
              [fullWidth]="true"
              [loading]="isLoading()"
              [disabled]="form.invalid">
              Send Reset Link
            </coar-button>
          </form>

          <p class="back-link">
            Remember your password?
            <a routerLink="/login">Sign in</a>
          </p>
        }
      </coar-card>
    </div>
  `,
  styles: `
    .forgot-container {
      min-height: calc(100vh - 120px);
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 2rem;
    }

    .forgot-card {
      width: 100%;
      max-width: 420px;
    }

    .forgot-title {
      margin: 0 0 0.5rem;
      font-size: 1.5rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .forgot-subtitle {
      margin: 0 0 1.5rem;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .error-note,
    .success-note {
      margin-bottom: 1rem;
    }

    .form-group {
      margin-bottom: 1.5rem;
    }

    .back-link {
      margin: 1.5rem 0 0;
      text-align: center;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .back-link a {
      color: var(--color-primary);
      text-decoration: none;
      font-weight: 500;
    }

    .back-link a:hover {
      text-decoration: underline;
    }
  `,
})
export class ForgotPasswordComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApiService);

  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
  });

  onSubmit(): void {
    if (this.form.invalid) return;

    const { email } = this.form.getRawValue();

    this.isLoading.set(true);
    this.error.set(null);

    this.authApi
      .forgotPassword({ email })
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError(() => {
          // Always show success to prevent email enumeration
          return of(null);
        })
      )
      .subscribe(() => {
        this.success.set(
          'If an account exists with this email, you will receive a password reset link shortly.'
        );
      });
  }

  getFieldError(field: string): string {
    const control = this.form.get(field);
    if (!control?.touched || !control.errors) return '';

    if (control.errors['required']) {
      return 'Email is required';
    }

    if (control.errors['email']) {
      return 'Please enter a valid email address';
    }

    return '';
  }
}
