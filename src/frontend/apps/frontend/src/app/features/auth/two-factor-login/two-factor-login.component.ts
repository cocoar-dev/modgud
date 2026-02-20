import { Component, inject, signal, OnInit } from '@angular/core';
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

type TwoFactorMethod = 'totp' | 'email' | 'webauthn';

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
      <coar-card elevated padding="l" class="tfa-card">
        <h1 class="tfa-title">Two-Factor Authentication</h1>

        @if (error()) {
          <coar-note variant="error" padding="s" class="error-note">
            {{ error() }}
          </coar-note>
        }

        @if (success()) {
          <coar-note variant="success" padding="s" class="success-note">
            {{ success() }}
          </coar-note>
        }

        <!-- Method Selection -->
        @if (showMethodSelection()) {
          <p class="tfa-subtitle">Choose a verification method:</p>

          <div class="method-options">
            @if (hasMethod('totp')) {
              <button
                type="button"
                class="method-option"
                (click)="selectMethod('totp')">
                <span class="method-icon">🔐</span>
                <span class="method-info">
                  <span class="method-name">Authenticator App</span>
                  <span class="method-desc">Use your authenticator app code</span>
                </span>
              </button>
            }

            @if (hasMethod('email')) {
              <button
                type="button"
                class="method-option"
                (click)="selectMethod('email')">
                <span class="method-icon">📧</span>
                <span class="method-info">
                  <span class="method-name">Email Code</span>
                  <span class="method-desc">Receive a code via email</span>
                </span>
              </button>
            }

            @if (hasMethod('webauthn')) {
              <button
                type="button"
                class="method-option"
                (click)="selectMethod('webauthn')">
                <span class="method-icon">🔑</span>
                <span class="method-info">
                  <span class="method-name">Security Key / Passkey</span>
                  <span class="method-desc">Use your security key or passkey</span>
                </span>
              </button>
            }
          </div>
        }

        <!-- TOTP Form -->
        @if (selectedMethod() === 'totp') {
          <p class="tfa-subtitle">
            Enter the 6-digit code from your authenticator app.
          </p>

          <form [formGroup]="totpForm" (ngSubmit)="onSubmitTotp()">
            <div class="form-group">
              <coar-text-input
                label="Authentication Code"
                placeholder="000000"
                formControlName="code"
                autocomplete="one-time-code"
                [required]="true"
                [maxlength]="6"
                [error]="getFieldError(totpForm, 'code')" />
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
              [disabled]="totpForm.invalid">
              Verify
            </coar-button>
          </form>

          @if (availableMethods().length > 1) {
            <button
              type="button"
              class="back-link"
              (click)="backToMethodSelection()">
              ← Choose a different method
            </button>
          }

          <div class="recovery-link">
            <p>Can't access your authenticator?</p>
            <a [routerLink]="['/login/recovery']" [queryParams]="{ returnUrl: returnUrl }">
              Use a recovery code
            </a>
          </div>
        }

        <!-- Email OTP Form -->
        @if (selectedMethod() === 'email') {
          @if (!emailOtpSent()) {
            <p class="tfa-subtitle">
              We'll send a verification code to your email address.
            </p>

            <coar-button
              variant="primary"
              [fullWidth]="true"
              [loading]="isLoading()"
              (clicked)="onRequestEmailOtp()">
              Send Code
            </coar-button>
          } @else {
            <p class="tfa-subtitle">
              Enter the 6-digit code sent to your email.
            </p>

            <form [formGroup]="emailOtpForm" (ngSubmit)="onSubmitEmailOtp()">
              <div class="form-group">
                <coar-text-input
                  label="Email Code"
                  placeholder="000000"
                  formControlName="code"
                  autocomplete="one-time-code"
                  [required]="true"
                  [maxlength]="6"
                  [error]="getFieldError(emailOtpForm, 'code')" />
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
                [disabled]="emailOtpForm.invalid">
                Verify
              </coar-button>
            </form>

            <button
              type="button"
              class="resend-link"
              [disabled]="isLoading()"
              (click)="onRequestEmailOtp()">
              Resend code
            </button>
          }

          @if (availableMethods().length > 1) {
            <button
              type="button"
              class="back-link"
              (click)="backToMethodSelection()">
              ← Choose a different method
            </button>
          }
        }

        <!-- WebAuthn -->
        @if (selectedMethod() === 'webauthn') {
          <p class="tfa-subtitle">
            Use your security key or passkey to verify.
          </p>

          <div class="webauthn-prompt">
            @if (isLoading()) {
              <div class="webauthn-loading">
                <span class="spinner"></span>
                <p>Waiting for your security key...</p>
              </div>
            } @else {
              <coar-button
                variant="primary"
                [fullWidth]="true"
                (clicked)="onWebAuthnAuthenticate()">
                Use Security Key
              </coar-button>
            }
          </div>

          @if (availableMethods().length > 1) {
            <button
              type="button"
              class="back-link"
              (click)="backToMethodSelection()">
              ← Choose a different method
            </button>
          }
        }
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

    .error-note,
    .success-note {
      margin-bottom: 1rem;
    }

    .method-options {
      display: flex;
      flex-direction: column;
      gap: 0.75rem;
    }

    .method-option {
      display: flex;
      align-items: center;
      gap: 1rem;
      padding: 1rem;
      border: 1px solid var(--color-border-primary);
      border-radius: var(--radius-md);
      background: var(--color-surface-primary);
      cursor: pointer;
      transition: all 0.15s ease;
      text-align: left;
    }

    .method-option:hover {
      border-color: var(--color-primary);
      background: var(--color-surface-secondary);
    }

    .method-icon {
      font-size: 1.5rem;
    }

    .method-info {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
    }

    .method-name {
      font-weight: 500;
      color: var(--color-text-primary);
    }

    .method-desc {
      font-size: 0.75rem;
      color: var(--color-text-secondary);
    }

    .form-group {
      margin-bottom: 1rem;
    }

    .back-link {
      display: block;
      margin-top: 1rem;
      padding: 0;
      border: none;
      background: none;
      font-size: 0.875rem;
      color: var(--color-primary);
      cursor: pointer;
      text-align: center;
      width: 100%;
    }

    .back-link:hover {
      text-decoration: underline;
    }

    .resend-link {
      display: block;
      margin-top: 0.75rem;
      padding: 0;
      border: none;
      background: none;
      font-size: 0.875rem;
      color: var(--color-primary);
      cursor: pointer;
      text-align: center;
      width: 100%;
    }

    .resend-link:hover:not(:disabled) {
      text-decoration: underline;
    }

    .resend-link:disabled {
      opacity: 0.5;
      cursor: not-allowed;
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

    .webauthn-prompt {
      text-align: center;
      padding: 1rem 0;
    }

    .webauthn-loading {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 1rem;
    }

    .spinner {
      width: 40px;
      height: 40px;
      border: 3px solid var(--color-border-primary);
      border-top-color: var(--color-primary);
      border-radius: 50%;
      animation: spin 1s linear infinite;
    }

    @keyframes spin {
      to {
        transform: rotate(360deg);
      }
    }
  `,
})
export class TwoFactorLoginComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApiService);
  private readonly authState = inject(AuthStateService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly availableMethods = signal<TwoFactorMethod[]>([]);
  readonly selectedMethod = signal<TwoFactorMethod | null>(null);
  readonly emailOtpSent = signal(false);

  readonly returnUrl = this.route.snapshot.queryParams['returnUrl'] || '/';

  readonly totpForm = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^\d{6}$/)]],
    rememberMachine: [false],
  });

  readonly emailOtpForm = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^\d{6}$/)]],
    rememberMachine: [false],
  });

  readonly showMethodSelection = () => {
    return this.availableMethods().length > 1 && this.selectedMethod() === null;
  };

  ngOnInit(): void {
    // Get available methods from query params or route state
    const methods = this.route.snapshot.queryParams['methods'];
    if (methods) {
      this.availableMethods.set(methods.split(',') as TwoFactorMethod[]);
    } else {
      // Default to TOTP only if no methods specified (legacy behavior)
      this.availableMethods.set(['totp']);
    }

    // If only one method, auto-select it
    if (this.availableMethods().length === 1) {
      this.selectMethod(this.availableMethods()[0]);
    }
  }

  hasMethod(method: TwoFactorMethod): boolean {
    return this.availableMethods().includes(method);
  }

  selectMethod(method: TwoFactorMethod): void {
    this.selectedMethod.set(method);
    this.error.set(null);
    this.success.set(null);

    // For WebAuthn, automatically start the authentication
    if (method === 'webauthn') {
      this.onWebAuthnAuthenticate();
    }
  }

  backToMethodSelection(): void {
    this.selectedMethod.set(null);
    this.emailOtpSent.set(false);
    this.error.set(null);
    this.success.set(null);
    this.totpForm.reset();
    this.emailOtpForm.reset();
  }

  onSubmitTotp(): void {
    if (this.totpForm.invalid) return;

    const { code, rememberMachine } = this.totpForm.getRawValue();

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

  onRequestEmailOtp(): void {
    this.isLoading.set(true);
    this.error.set(null);

    this.authApi
      .requestEmailOtp()
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(
            err?.error?.message || 'Failed to send code. Please try again.'
          );
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.emailOtpSent.set(true);
          this.success.set('A verification code has been sent to your email.');
        }
      });
  }

  onSubmitEmailOtp(): void {
    if (this.emailOtpForm.invalid) return;

    const { code, rememberMachine } = this.emailOtpForm.getRawValue();

    this.isLoading.set(true);
    this.error.set(null);

    this.authApi
      .emailOtpLogin({ code, rememberMachine })
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

  async onWebAuthnAuthenticate(): Promise<void> {
    this.isLoading.set(true);
    this.error.set(null);

    try {
      // Check if WebAuthn is supported
      if (!window.PublicKeyCredential) {
        this.error.set('WebAuthn is not supported in this browser.');
        this.isLoading.set(false);
        return;
      }

      // Get authentication options from server
      const optionsResponse = await this.authApi
        .getWebAuthnAuthenticationOptions()
        .toPromise();

      if (!optionsResponse) {
        throw new Error('Failed to get authentication options');
      }

      // Prepare options for navigator.credentials.get
      const options = optionsResponse.options as PublicKeyCredentialRequestOptions;

      // Convert base64url strings to ArrayBuffers
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

      await this.authApi
        .completeWebAuthnAuthentication({
          assertionResponse,
          rememberMachine: true,
        })
        .toPromise();

      this.authState.completeTwoFactorLogin(this.returnUrl);
    } catch (err: unknown) {
      console.error('WebAuthn authentication failed:', err);
      const error = err as Error;
      if (error.name === 'NotAllowedError') {
        this.error.set('Authentication was cancelled or timed out.');
      } else {
        this.error.set(
          error.message || 'WebAuthn authentication failed. Please try again.'
        );
      }
    } finally {
      this.isLoading.set(false);
    }
  }

  getFieldError(form: typeof this.totpForm, field: string): string {
    const control = form.get(field);
    if (!control?.touched || !control.errors) return '';

    if (control.errors['required']) {
      return 'Code is required';
    }

    if (control.errors['pattern']) {
      return 'Enter a valid 6-digit code';
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
