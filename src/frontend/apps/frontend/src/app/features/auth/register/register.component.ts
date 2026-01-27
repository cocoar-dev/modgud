import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { Router, RouterModule } from '@angular/router';
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
  selector: 'app-register',
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
    <div class="register-container">
      <coar-card elevated padding="lg" class="register-card">
        <h1 class="register-title">Create Account</h1>
        <p class="register-subtitle">Join us today. Fill in your details below.</p>

        @if (error()) {
          <coar-note color="error" padding="sm" class="error-note">
            {{ error() }}
          </coar-note>
        }

        @if (success()) {
          <coar-note color="success" padding="sm" class="success-note">
            {{ success() }}
          </coar-note>
        } @else {
          <form [formGroup]="form" (ngSubmit)="onSubmit()">
            <div class="form-group">
              <coar-text-input
                label="Username"
                placeholder="Choose a username"
                formControlName="userName"
                autocomplete="username"
                [required]="true"
                [error]="getFieldError('userName')" />
            </div>

            <div class="form-group">
              <coar-text-input
                label="Email"
                placeholder="your&#64;email.com"
                formControlName="email"
                autocomplete="email"
                [required]="true"
                [error]="getFieldError('email')" />
            </div>

            <div class="form-row-two">
              <div class="form-group">
                <coar-text-input
                  label="First Name"
                  placeholder="John"
                  formControlName="firstName"
                  autocomplete="given-name" />
              </div>

              <div class="form-group">
                <coar-text-input
                  label="Last Name"
                  placeholder="Doe"
                  formControlName="lastName"
                  autocomplete="family-name" />
              </div>
            </div>

            <div class="form-group">
              <coar-password-input
                label="Password"
                placeholder="Create a strong password"
                formControlName="password"
                autocomplete="new-password"
                [required]="true"
                [error]="getFieldError('password')" />
            </div>

            <div class="form-group">
              <coar-password-input
                label="Confirm Password"
                placeholder="Repeat your password"
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
              Create Account
            </coar-button>
          </form>

          <p class="login-link">
            Already have an account?
            <a routerLink="/login">Sign in</a>
          </p>
        }
      </coar-card>
    </div>
  `,
  styles: `
    .register-container {
      min-height: calc(100vh - 120px);
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 2rem;
    }

    .register-card {
      width: 100%;
      max-width: 480px;
    }

    .register-title {
      margin: 0 0 0.5rem;
      font-size: 1.5rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .register-subtitle {
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

    .form-row-two {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 1rem;
    }

    .login-link {
      margin: 1.5rem 0 0;
      text-align: center;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .login-link a {
      color: var(--color-primary);
      text-decoration: none;
      font-weight: 500;
    }

    .login-link a:hover {
      text-decoration: underline;
    }
  `,
})
export class RegisterComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApiService);
  private readonly router = inject(Router);

  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    userName: ['', [Validators.required, Validators.minLength(3)]],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
    confirmPassword: ['', [Validators.required]],
    firstName: [''],
    lastName: [''],
  });

  onSubmit(): void {
    if (this.form.invalid) return;

    const { userName, email, password, confirmPassword, firstName, lastName } =
      this.form.getRawValue();

    if (password !== confirmPassword) {
      this.error.set('Passwords do not match');
      return;
    }

    this.isLoading.set(true);
    this.error.set(null);

    this.authApi
      .register({
        userName,
        email,
        password,
        firstName: firstName || undefined,
        lastName: lastName || undefined,
      })
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(
            err?.error?.message || 'Registration failed. Please try again.'
          );
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result) {
          if (result.succeeded) {
            if (result.requiresEmailConfirmation) {
              this.success.set(
                'Account created successfully! Please check your email to confirm your account.'
              );
            } else {
              this.success.set(
                'Account created successfully! You can now sign in.'
              );
              setTimeout(() => this.router.navigate(['/login']), 2000);
            }
          } else {
            this.error.set(
              result.errors?.join(', ') ||
                'Registration failed. Please try again.'
            );
          }
        }
      });
  }

  getFieldError(field: string): string {
    const control = this.form.get(field);
    if (!control?.touched || !control.errors) return '';

    if (control.errors['required']) {
      const labels: Record<string, string> = {
        userName: 'Username',
        email: 'Email',
        password: 'Password',
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
