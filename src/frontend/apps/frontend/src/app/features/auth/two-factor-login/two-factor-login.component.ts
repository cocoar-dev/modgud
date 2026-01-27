import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { Router, RouterModule, ActivatedRoute } from '@angular/router';
import {
  CoarButtonComponent,
  CoarTextInputComponent,
  CoarCheckboxComponent,
  CoarCardComponent,
  CoarNoteComponent,
} from '@cocoar/ui';
import { AuthApiService } from '../../../core/services/auth-api.service';
import { AuthStateService } from '../../../core/services/auth-state.service';
import { catchError, of, finalize } from 'rxjs';

@Component({
  selector: 'app-two-factor-login',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    RouterModule,
    CoarButtonComponent,
    CoarTextInputComponent,
    CoarCheckboxComponent,
    CoarCardComponent,
    CoarNoteComponent,
  ],
  template: `
    <div class="tfa-container">
      <coar-card elevated padding="lg" class="tfa-card">
        <h1 class="tfa-title">Two-Factor Authentication</h1>
        <p class="tfa-subtitle">
          Enter the 6-digit code from your authenticator app.
        </p>

        @if (error()) {
          <coar-note color="error" padding="sm" class="error-note">
            {{ error() }}
          </coar-note>
        }

        <form [formGroup]="form" (ngSubmit)="onSubmit()">
          <div class="form-group">
            <coar-text-input
              label="Authentication Code"
              placeholder="000000"
              formControlName="code"
              autocomplete="one-time-code"
              [required]="true"
              [maxlength]="6"
              [error]="getFieldError('code')" />
          </div>

          <div class="form-group">
            <coar-checkbox
              formControlName="rememberMachine"
              label="Remember this device for 30 days" />
          </div>

          <coar-button
            type="submit"
            variant="primary"
            [fullWidth]="true"
            [loading]="isLoading()"
            [disabled]="form.invalid">
            Verify
          </coar-button>
        </form>

        <div class="recovery-link">
          <p>Can't access your authenticator?</p>
          <a [routerLink]="['/login/recovery']" [queryParams]="{ returnUrl: returnUrl }">
            Use a recovery code
          </a>
        </div>
      </coar-card>
    </div>
  `,
  styles: `
    .tfa-container {
      min-height: calc(100vh - 120px);
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 2rem;
    }

    .tfa-card {
      width: 100%;
      max-width: 420px;
    }

    .tfa-title {
      margin: 0 0 0.5rem;
      font-size: 1.5rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .tfa-subtitle {
      margin: 0 0 1.5rem;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .error-note {
      margin-bottom: 1rem;
    }

    .form-group {
      margin-bottom: 1rem;
    }

    .recovery-link {
      margin-top: 1.5rem;
      text-align: center;
      font-size: 0.875rem;
    }

    .recovery-link p {
      margin: 0 0 0.25rem;
      color: var(--color-text-secondary);
    }

    .recovery-link a {
      color: var(--color-primary);
      text-decoration: none;
      font-weight: 500;
    }

    .recovery-link a:hover {
      text-decoration: underline;
    }
  `,
})
export class TwoFactorLoginComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApiService);
  private readonly authState = inject(AuthStateService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  readonly returnUrl =
    this.route.snapshot.queryParams['returnUrl'] || '/';

  readonly form = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^\d{6}$/)]],
    rememberMachine: [false],
  });

  onSubmit(): void {
    if (this.form.invalid) return;

    const { code, rememberMachine } = this.form.getRawValue();

    this.isLoading.set(true);
    this.error.set(null);

    this.authApi
      .twoFactorLogin({ code, rememberMachine })
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(
            err?.error?.message || 'Invalid code. Please try again.'
          );
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.authState.completeTwoFactorLogin(this.returnUrl);
        }
      });
  }

  getFieldError(field: string): string {
    const control = this.form.get(field);
    if (!control?.touched || !control.errors) return '';

    if (control.errors['required']) {
      return 'Authentication code is required';
    }

    if (control.errors['pattern']) {
      return 'Enter a valid 6-digit code';
    }

    return '';
  }
}
