import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { Router, RouterModule, ActivatedRoute } from '@angular/router';
import {
  CoarButtonComponent,
  CoarTextInputComponent,
  CoarPasswordInputComponent,
  CoarCheckboxComponent,
  CoarCardComponent,
  CoarNoteComponent,
} from '@cocoar/ui';
import { AuthStateService } from '../../../core/services/auth-state.service';
import { AuthApiService } from '../../../core/services/auth-api.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    RouterModule,
    CoarButtonComponent,
    CoarTextInputComponent,
    CoarPasswordInputComponent,
    CoarCheckboxComponent,
    CoarCardComponent,
    CoarNoteComponent,
  ],
  template: `
    <div class="login-container">
      <coar-card elevated padding="lg" class="login-card">
        <h1 class="login-title">Sign In</h1>
        <p class="login-subtitle">Welcome back! Please enter your credentials.</p>

        @if (error()) {
          <coar-note color="error" padding="sm" class="error-note">
            {{ error() }}
          </coar-note>
        }

        @if (webAuthnSupported()) {
          <button
            type="button"
            class="passkey-button"
            [disabled]="isPasskeyLoading()"
            (click)="onPasskeyLogin()">
            @if (isPasskeyLoading()) {
              <span class="spinner"></span>
            } @else {
              <span class="passkey-icon">🔑</span>
            }
            <span>Sign in with Passkey</span>
          </button>

          <div class="divider">
            <span>or</span>
          </div>
        }

        <form [formGroup]="form" (ngSubmit)="onSubmit()">
          <div class="form-group">
            <coar-text-input
              label="Username"
              placeholder="Enter your username"
              formControlName="userName"
              autocomplete="username"
              [required]="true"
              [error]="getFieldError('userName')" />
          </div>

          <div class="form-group">
            <coar-password-input
              label="Password"
              placeholder="Enter your password"
              formControlName="password"
              autocomplete="current-password"
              [required]="true"
              [error]="getFieldError('password')" />
          </div>

          <div class="form-row">
            <coar-checkbox
              formControlName="rememberMe"
              label="Remember me" />

            <a routerLink="/forgot-password" class="forgot-link">
              Forgot password?
            </a>
          </div>

          <coar-button
            type="submit"
            variant="primary"
            [fullWidth]="true"
            [loading]="isLoading()"
            [disabled]="form.invalid">
            Sign In
          </coar-button>
        </form>

        <p class="register-link">
          Don't have an account?
          <a routerLink="/register">Create one</a>
        </p>
      </coar-card>
    </div>
  `,
  styles: `
    .login-container {
      min-height: calc(100vh - 120px);
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 2rem;
    }

    .login-card {
      width: 100%;
      max-width: 420px;
    }

    .login-title {
      margin: 0 0 0.5rem;
      font-size: 1.5rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .login-subtitle {
      margin: 0 0 1.5rem;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .error-note {
      margin-bottom: 1rem;
    }

    .passkey-button {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 0.75rem;
      width: 100%;
      padding: 0.875rem 1rem;
      border: 1px solid var(--color-border-primary);
      border-radius: var(--radius-md);
      background: var(--color-surface-primary);
      font-size: 0.9375rem;
      font-weight: 500;
      color: var(--color-text-primary);
      cursor: pointer;
      transition: all 0.15s ease;
    }

    .passkey-button:hover:not(:disabled) {
      border-color: var(--color-primary);
      background: var(--color-surface-secondary);
    }

    .passkey-button:disabled {
      opacity: 0.7;
      cursor: not-allowed;
    }

    .passkey-icon {
      font-size: 1.25rem;
    }

    .spinner {
      width: 20px;
      height: 20px;
      border: 2px solid var(--color-border-primary);
      border-top-color: var(--color-primary);
      border-radius: 50%;
      animation: spin 1s linear infinite;
    }

    @keyframes spin {
      to {
        transform: rotate(360deg);
      }
    }

    .divider {
      display: flex;
      align-items: center;
      margin: 1.5rem 0;
      color: var(--color-text-secondary);
      font-size: 0.875rem;
    }

    .divider::before,
    .divider::after {
      content: '';
      flex: 1;
      height: 1px;
      background: var(--color-border-primary);
    }

    .divider span {
      padding: 0 1rem;
    }

    .form-group {
      margin-bottom: 1rem;
    }

    .form-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 1.5rem;
    }

    .forgot-link {
      font-size: 0.875rem;
      color: var(--color-primary);
      text-decoration: none;
    }

    .forgot-link:hover {
      text-decoration: underline;
    }

    .register-link {
      margin: 1.5rem 0 0;
      text-align: center;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .register-link a {
      color: var(--color-primary);
      text-decoration: none;
      font-weight: 500;
    }

    .register-link a:hover {
      text-decoration: underline;
    }
  `,
})
export class LoginComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authState = inject(AuthStateService);
  private readonly authApi = inject(AuthApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly isLoading = this.authState.isLoading;
  readonly error = this.authState.error;
  readonly isPasskeyLoading = signal(false);
  readonly webAuthnSupported = signal(false);

  readonly form = this.fb.nonNullable.group({
    userName: ['', [Validators.required]],
    password: ['', [Validators.required]],
    rememberMe: [false],
  });

  constructor() {
    // Check if WebAuthn is supported
    this.webAuthnSupported.set(
      typeof window !== 'undefined' &&
      !!window.PublicKeyCredential &&
      typeof window.PublicKeyCredential.isConditionalMediationAvailable === 'function'
    );
  }

  async onSubmit(): Promise<void> {
    if (this.form.invalid) return;

    const { userName, password, rememberMe } = this.form.getRawValue();
    const returnUrl =
      this.route.snapshot.queryParams['returnUrl'] || '/';

    const result = await this.authState.login(
      { userName, password, rememberMe },
      { redirectTo: returnUrl }
    );

    if (result.requiresTwoFactor) {
      const queryParams: Record<string, string> = { returnUrl };
      if (result.availableTwoFactorMethods?.length) {
        queryParams['methods'] = result.availableTwoFactorMethods.join(',');
      }
      this.router.navigate(['/login/2fa'], { queryParams });
    }
  }

  async onPasskeyLogin(): Promise<void> {
    this.isPasskeyLoading.set(true);
    this.authState.clearError();

    try {
      // Get login options from server (passwordless - no prior auth required)
      const optionsResponse = await this.authApi
        .getWebAuthnLoginOptions()
        .toPromise();

      if (!optionsResponse) {
        throw new Error('Failed to get authentication options');
      }

      // Prepare options for navigator.credentials.get
      const options = optionsResponse.options as PublicKeyCredentialRequestOptions;

      const publicKeyOptions: PublicKeyCredentialRequestOptions = {
        ...options,
        challenge: this.base64UrlToBuffer(options.challenge as unknown as string),
        allowCredentials: options.allowCredentials?.map((cred) => ({
          ...cred,
          id: this.base64UrlToBuffer(cred.id as unknown as string),
        })),
      };

      // Request credential from authenticator
      const credential = (await navigator.credentials.get({
        publicKey: publicKeyOptions,
      })) as PublicKeyCredential;

      if (!credential) {
        throw new Error('No credential returned');
      }

      const response = credential.response as AuthenticatorAssertionResponse;

      // Send assertion to server
      const assertionResponse = {
        id: credential.id,
        rawId: this.bufferToBase64Url(credential.rawId),
        type: credential.type,
        response: {
          authenticatorData: this.bufferToBase64Url(response.authenticatorData),
          clientDataJSON: this.bufferToBase64Url(response.clientDataJSON),
          signature: this.bufferToBase64Url(response.signature),
          userHandle: response.userHandle
            ? this.bufferToBase64Url(response.userHandle)
            : null,
        },
      };

      const result = await this.authApi
        .completeWebAuthnLogin({
          assertionResponse,
          rememberMachine: true,
        })
        .toPromise();

      if (!result?.succeeded) {
        throw new Error(result?.errorMessage || 'Login failed');
      }

      // Success - redirect
      const returnUrl = this.route.snapshot.queryParams['returnUrl'] || '/';
      this.authState.completeTwoFactorLogin(returnUrl);
    } catch (err: unknown) {
      console.error('Passkey login failed:', err);
      const error = err as Error;
      if (error.name === 'NotAllowedError') {
        // User cancelled or no credentials available - don't show error
      } else {
        this.authState.setError(error.message || 'Passkey login failed. Please try again.');
      }
    } finally {
      this.isPasskeyLoading.set(false);
    }
  }

  getFieldError(field: string): string {
    const control = this.form.get(field);
    if (!control?.touched || !control.errors) return '';

    if (control.errors['required']) {
      return `${field === 'userName' ? 'Username' : 'Password'} is required`;
    }

    return '';
  }

  private base64UrlToBuffer(base64url: string): ArrayBuffer {
    const base64 = base64url.replace(/-/g, '+').replace(/_/g, '/');
    const padLen = (4 - (base64.length % 4)) % 4;
    const padded = base64 + '='.repeat(padLen);
    const binary = atob(padded);
    const buffer = new ArrayBuffer(binary.length);
    const view = new Uint8Array(buffer);
    for (let i = 0; i < binary.length; i++) {
      view[i] = binary.charCodeAt(i);
    }
    return buffer;
  }

  private bufferToBase64Url(buffer: ArrayBuffer): string {
    const bytes = new Uint8Array(buffer);
    let binary = '';
    for (let i = 0; i < bytes.length; i++) {
      binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
  }
}
