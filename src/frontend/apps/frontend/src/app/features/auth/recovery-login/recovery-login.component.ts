import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { Router, RouterModule, ActivatedRoute } from '@angular/router';
import {
  CoarButtonComponent,
  CoarTextInputComponent,
  CoarCardComponent,
  CoarNoteComponent,
} from '@cocoar/ui';
import { AuthApiService } from '../../../core/services/auth-api.service';
import { AuthStateService } from '../../../core/services/auth-state.service';
import { catchError, of, finalize } from 'rxjs';

@Component({
  selector: 'app-recovery-login',
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
    <div class="recovery-container">
      <coar-card elevated padding="l" class="recovery-card">
        <h1 class="recovery-title">Recovery Code</h1>
        <p class="recovery-subtitle">
          Enter one of your recovery codes to sign in.
        </p>

        @if (error()) {
          <coar-note variant="error" padding="s" class="error-note">
            {{ error() }}
          </coar-note>
        }

        <coar-note variant="warning" padding="s" class="warning-note">
          Each recovery code can only be used once. After signing in,
          consider generating new recovery codes.
        </coar-note>

        <form [formGroup]="form" (ngSubmit)="onSubmit()">
          <div class="form-group">
            <coar-text-input
              label="Recovery Code"
              placeholder="XXXXX-XXXXX"
              formControlName="code"
              autocomplete="off"
              [required]="true"
              [error]="getFieldError('code')" />
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

        <div class="back-link">
          <a [routerLink]="['/login/2fa']" [queryParams]="{ returnUrl: returnUrl }">
            Use authenticator code instead
          </a>
        </div>
      </coar-card>
    </div>
  `,
  styles: `
    .recovery-container {
      min-height: calc(100vh - 120px);
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 2rem;
    }

    .recovery-card {
      width: 100%;
      max-width: 420px;
    }

    .recovery-title {
      margin: 0 0 0.5rem;
      font-size: 1.5rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .recovery-subtitle {
      margin: 0 0 1rem;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .error-note,
    .warning-note {
      margin-bottom: 1rem;
    }

    .form-group {
      margin-bottom: 1.5rem;
    }

    .back-link {
      margin-top: 1.5rem;
      text-align: center;
      font-size: 0.875rem;
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
export class RecoveryLoginComponent {
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
    code: ['', [Validators.required]],
  });

  onSubmit(): void {
    if (this.form.invalid) return;

    const { code } = this.form.getRawValue();

    this.isLoading.set(true);
    this.error.set(null);

    this.authApi
      .recoveryCodeLogin({ code: code.trim() })
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(
            err?.error?.message || 'Invalid recovery code. Please try again.'
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
      return 'Recovery code is required';
    }

    return '';
  }
}
