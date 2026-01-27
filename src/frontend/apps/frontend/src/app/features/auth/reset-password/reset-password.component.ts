import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { Router, RouterModule, ActivatedRoute } from '@angular/router';
import {
  CoarButtonComponent,
  CoarTextInputComponent,
  CoarPasswordInputComponent,
  CoarCardComponent,
  CoarNoteComponent,
} from '@cocoar/ui';
import { AuthApiService } from '../../../core/services/auth-api.service';
import { catchError, of, finalize } from 'rxjs';

@Component({
  selector: 'app-reset-password',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    RouterModule,
    CoarButtonComponent,
    CoarTextInputComponent,
    CoarPasswordInputComponent,
    CoarCardComponent,
    CoarNoteComponent,
  ],
  template: `
    <div class="reset-container">
      <coar-card elevated padding="lg" class="reset-card">
        <h1 class="reset-title">Reset Password</h1>
        <p class="reset-subtitle">Enter your new password below.</p>

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
            <a routerLink="/login">Sign in with your new password</a>
          </p>
        } @else if (!hasToken()) {
          <coar-note color="error" padding="sm">
            Invalid or missing reset token. Please request a new password reset link.
          </coar-note>
          <p class="back-link">
            <a routerLink="/forgot-password">Request new reset link</a>
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

            <div class="form-group">
              <coar-password-input
                label="New Password"
                placeholder="Enter your new password"
                formControlName="newPassword"
                autocomplete="new-password"
                [required]="true"
                [error]="getFieldError('newPassword')" />
            </div>

            <div class="form-group">
              <coar-password-input
                label="Confirm New Password"
                placeholder="Repeat your new password"
                formControlName="confirmPassword"
                autocomplete="new-password"
                [required]="true"
                [error]="getFieldError('confirmPassword')" />
            </div>

            <coar-button
              type="submit"
              variant="primary"
              [fullWidth]="true"
              [loading]="isLoading()"
              [disabled]="form.invalid">
              Reset Password
            </coar-button>
          </form>
        }
      </coar-card>
    </div>
  `,
  styles: `
    .reset-container {
      min-height: calc(100vh - 120px);
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 2rem;
    }

    .reset-card {
      width: 100%;
      max-width: 420px;
    }

    .reset-title {
      margin: 0 0 0.5rem;
      font-size: 1.5rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .reset-subtitle {
      margin: 0 0 1.5rem;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .error-note,
    .success-note {
      margin-bottom: 1rem;
    }

    .form-group {
      margin-bottom: 1rem;
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
export class ResetPasswordComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);
  readonly hasToken = signal(false);

  private token = '';

  readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    newPassword: ['', [Validators.required, Validators.minLength(8)]],
    confirmPassword: ['', [Validators.required]],
  });

  ngOnInit(): void {
    this.token = this.route.snapshot.queryParams['token'] || '';
    this.hasToken.set(!!this.token);
  }

  onSubmit(): void {
    if (this.form.invalid) return;

    const { email, newPassword, confirmPassword } = this.form.getRawValue();

    if (newPassword !== confirmPassword) {
      this.error.set('Passwords do not match');
      return;
    }

    this.isLoading.set(true);
    this.error.set(null);

    this.authApi
      .resetPassword({
        email,
        token: this.token,
        newPassword,
      })
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(
            err?.error?.message ||
              'Failed to reset password. The link may have expired.'
          );
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.success.set(
            'Your password has been reset successfully. You can now sign in.'
          );
        }
      });
  }

  getFieldError(field: string): string {
    const control = this.form.get(field);
    if (!control?.touched || !control.errors) return '';

    if (control.errors['required']) {
      const labels: Record<string, string> = {
        email: 'Email',
        newPassword: 'New Password',
        confirmPassword: 'Confirm Password',
      };
      return `${labels[field] || field} is required`;
    }

    if (control.errors['minlength']) {
      const minLength = control.errors['minlength'].requiredLength;
      return `Must be at least ${minLength} characters`;
    }

    if (control.errors['email']) {
      return 'Please enter a valid email address';
    }

    return '';
  }
}
