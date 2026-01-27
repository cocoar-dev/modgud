import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import {
  FormBuilder,
  FormGroup,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';
import {
  CoarButtonComponent,
  CoarTextInputComponent,
  CoarPasswordInputComponent,
  CoarCardComponent,
  CoarNoteComponent,
} from '@cocoar/ui';
import { AuthApiService } from '../../core/services/auth-api.service';

@Component({
  selector: 'app-setup',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    CoarButtonComponent,
    CoarTextInputComponent,
    CoarPasswordInputComponent,
    CoarCardComponent,
    CoarNoteComponent,
  ],
  template: `
    <div class="setup-container">
      <div class="setup-content">
        @if (loading()) {
          <div class="loading">
            <p>Checking setup status...</p>
          </div>
        } @else if (setupComplete()) {
          <coar-card>
            <div class="setup-complete">
              <h1 class="coar-title">Setup Already Complete</h1>
              <p class="coar-body coar-text-secondary">
                An administrator account has already been created.
              </p>
              <coar-button variant="primary" (click)="goToLogin()">
                Go to Login
              </coar-button>
            </div>
          </coar-card>
        } @else {
          <coar-card>
            <div class="setup-form">
              <div class="setup-header">
                <h1 class="coar-title">Welcome to Cocoar Auth</h1>
                <p class="coar-body coar-text-secondary">
                  Create your administrator account to get started.
                </p>
              </div>

              @if (error()) {
                <coar-note variant="error">
                  {{ error() }}
                </coar-note>
              }

              @if (success()) {
                <coar-note variant="success">
                  {{ success() }}
                </coar-note>
              }

              <form [formGroup]="form" (ngSubmit)="onSubmit()">
                <div class="form-fields">
                  <coar-text-input
                    label="Username"
                    placeholder="admin"
                    [required]="true"
                    formControlName="userName"
                    [error]="getFieldError('userName')"
                  />

                  <coar-text-input
                    label="Email"
                    type="email"
                    placeholder="admin@example.com"
                    formControlName="email"
                    [error]="getFieldError('email')"
                  />

                  <coar-password-input
                    label="Password"
                    [required]="true"
                    formControlName="password"
                    [error]="getFieldError('password')"
                  />

                  <coar-password-input
                    label="Confirm Password"
                    [required]="true"
                    formControlName="confirmPassword"
                    [error]="getFieldError('confirmPassword')"
                  />

                  <div class="name-row">
                    <coar-text-input
                      label="First Name"
                      placeholder="John"
                      formControlName="firstName"
                    />

                    <coar-text-input
                      label="Last Name"
                      placeholder="Doe"
                      formControlName="lastName"
                    />
                  </div>
                </div>

                <div class="form-actions">
                  <coar-button
                    variant="primary"
                    type="submit"
                    [loading]="submitting()"
                    [disabled]="form.invalid || submitting()"
                  >
                    Create Admin Account
                  </coar-button>
                </div>
              </form>
            </div>
          </coar-card>
        }
      </div>
    </div>
  `,
  styles: [`
    .setup-container {
      min-height: 100vh;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: var(--coar-spacing-lg, 1.5rem);
      background: var(--coar-background-neutral-secondary);
    }

    .setup-content {
      width: 100%;
      max-width: 480px;
    }

    .loading {
      text-align: center;
      padding: var(--coar-spacing-xl, 2rem);
    }

    .setup-complete {
      text-align: center;
      padding: var(--coar-spacing-lg, 1.5rem);
    }

    .setup-complete h1 {
      margin-bottom: var(--coar-spacing-md, 1rem);
    }

    .setup-complete p {
      margin-bottom: var(--coar-spacing-lg, 1.5rem);
    }

    .setup-form {
      padding: var(--coar-spacing-md, 1rem);
    }

    .setup-header {
      text-align: center;
      margin-bottom: var(--coar-spacing-lg, 1.5rem);
    }

    .setup-header h1 {
      margin-bottom: var(--coar-spacing-sm, 0.5rem);
    }

    .form-fields {
      display: flex;
      flex-direction: column;
      gap: var(--coar-spacing-md, 1rem);
      margin-bottom: var(--coar-spacing-lg, 1.5rem);
    }

    .name-row {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: var(--coar-spacing-md, 1rem);
    }

    .form-actions {
      display: flex;
      justify-content: center;
    }

    coar-note {
      margin-bottom: var(--coar-spacing-md, 1rem);
    }
  `],
})
export class SetupComponent implements OnInit {
  private readonly authApi = inject(AuthApiService);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);

  loading = signal(true);
  setupComplete = signal(false);
  submitting = signal(false);
  error = signal<string | null>(null);
  success = signal<string | null>(null);

  form: FormGroup = this.fb.group({
    userName: ['', [Validators.required, Validators.minLength(3)]],
    email: ['', [Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
    confirmPassword: ['', [Validators.required]],
    firstName: [''],
    lastName: [''],
  });

  ngOnInit(): void {
    this.checkSetupStatus();
  }

  private checkSetupStatus(): void {
    this.authApi.getSetupStatus().subscribe({
      next: (status) => {
        this.loading.set(false);
        if (!status.needsSetup) {
          this.setupComplete.set(true);
        }
      },
      error: () => {
        this.loading.set(false);
        this.error.set('Failed to check setup status. Please try again.');
      },
    });
  }

  onSubmit(): void {
    if (this.form.invalid) {
      return;
    }

    const { password, confirmPassword } = this.form.value;
    if (password !== confirmPassword) {
      this.error.set('Passwords do not match.');
      return;
    }

    this.error.set(null);
    this.success.set(null);
    this.submitting.set(true);

    const request = {
      userName: this.form.value.userName,
      password: this.form.value.password,
      email: this.form.value.email || undefined,
      firstName: this.form.value.firstName || undefined,
      lastName: this.form.value.lastName || undefined,
    };

    this.authApi.createAdmin(request).subscribe({
      next: (result) => {
        this.submitting.set(false);
        if (result.success) {
          this.success.set(result.message);
          setTimeout(() => {
            this.router.navigate(['/login']);
          }, 2000);
        } else {
          this.error.set(result.message);
        }
      },
      error: (err) => {
        this.submitting.set(false);
        if (err.status === 404) {
          this.error.set('Setup has already been completed.');
          this.setupComplete.set(true);
        } else if (err.error?.message) {
          this.error.set(err.error.message);
        } else if (err.error?.errors) {
          this.error.set(err.error.errors.join(', '));
        } else {
          this.error.set('Failed to create admin account. Please try again.');
        }
      },
    });
  }

  getFieldError(fieldName: string): string {
    const control = this.form.get(fieldName);
    if (control?.touched && control?.errors) {
      if (control.errors['required']) {
        return `${fieldName.charAt(0).toUpperCase() + fieldName.slice(1)} is required`;
      }
      if (control.errors['minlength']) {
        return `Minimum ${control.errors['minlength'].requiredLength} characters required`;
      }
      if (control.errors['email']) {
        return 'Please enter a valid email address';
      }
    }
    return '';
  }

  goToLogin(): void {
    this.router.navigate(['/login']);
  }
}
