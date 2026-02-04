import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import {
  CoarButtonComponent,
  CoarTextInputComponent,
  CoarPasswordInputComponent,
  CoarCardComponent,
  CoarNoteComponent,
} from '@cocoar/ui';
import { AuthApiService } from '../../core/services/auth-api.service';
import { AuthStateService } from '../../core/services/auth-state.service';
import {
  Profile,
  TwoFactorStatus,
  TwoFactorSetup,
  WebAuthnCredential,
} from '../../core/models/auth.models';
import { catchError, of, finalize, forkJoin } from 'rxjs';

type TabId = 'personal' | 'password' | 'tfa';

@Component({
  selector: 'app-profile',
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
    <div class="profile">
      <h1 class="page-title">Profile Settings</h1>

      <!-- Custom Tabs -->
      <div class="tabs">
        <button
          type="button"
          class="tab"
          [class.active]="activeTab() === 'personal'"
          (click)="activeTab.set('personal')">
          Personal Info
        </button>
        <button
          type="button"
          class="tab"
          [class.active]="activeTab() === 'password'"
          (click)="activeTab.set('password')">
          Change Password
        </button>
        <button
          type="button"
          class="tab"
          [class.active]="activeTab() === 'tfa'"
          (click)="activeTab.set('tfa')">
          Two-Factor Auth
        </button>
      </div>

      <!-- Personal Info Tab -->
      @if (activeTab() === 'personal') {
        <coar-card padding="lg" class="section-card">
          @if (profileError()) {
            <coar-note color="error" padding="sm" class="message">
              {{ profileError() }}
            </coar-note>
          }

          @if (profileSuccess()) {
            <coar-note color="success" padding="sm" class="message">
              {{ profileSuccess() }}
            </coar-note>
          }

          <form [formGroup]="profileForm" (ngSubmit)="onUpdateProfile()">
            <div class="form-row">
              <div class="form-group">
                <coar-text-input
                  label="Username"
                  [value]="profile()?.userName || ''"
                  [disabled]="true"
                  hint="Username cannot be changed" />
              </div>

              <div class="form-group">
                <coar-text-input
                  label="Email"
                  [value]="profile()?.email || ''"
                  [disabled]="true"
                  [hint]="
                    profile()?.emailConfirmed ? 'Verified' : 'Not verified'
                  " />
              </div>
            </div>

            <div class="form-row">
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
              <coar-text-input
                label="Phone Number"
                placeholder="+1 (555) 123-4567"
                formControlName="phoneNumber"
                autocomplete="tel" />
            </div>

            <coar-button
              type="submit"
              variant="primary"
              [loading]="isUpdatingProfile()"
              [disabled]="profileForm.pristine">
              Save Changes
            </coar-button>
          </form>
        </coar-card>
      }

      <!-- Change Password Tab -->
      @if (activeTab() === 'password') {
        <coar-card padding="lg" class="section-card">
          @if (passwordError()) {
            <coar-note color="error" padding="sm" class="message">
              {{ passwordError() }}
            </coar-note>
          }

          @if (passwordSuccess()) {
            <coar-note color="success" padding="sm" class="message">
              {{ passwordSuccess() }}
            </coar-note>
          }

          <form [formGroup]="passwordForm" (ngSubmit)="onChangePassword()">
            <div class="form-group">
              <coar-password-input
                label="Current Password"
                placeholder="Enter your current password"
                formControlName="currentPassword"
                autocomplete="current-password"
                [required]="true"
                [error]="getPasswordFieldError('currentPassword')" />
            </div>

            <div class="form-group">
              <coar-password-input
                label="New Password"
                placeholder="Enter your new password"
                formControlName="newPassword"
                autocomplete="new-password"
                [required]="true"
                [error]="getPasswordFieldError('newPassword')" />
            </div>

            <div class="form-group">
              <coar-password-input
                label="Confirm New Password"
                placeholder="Repeat your new password"
                formControlName="confirmPassword"
                autocomplete="new-password"
                [required]="true"
                [error]="getPasswordFieldError('confirmPassword')" />
            </div>

            <coar-button
              type="submit"
              variant="primary"
              [loading]="isChangingPassword()"
              [disabled]="passwordForm.invalid">
              Change Password
            </coar-button>
          </form>
        </coar-card>
      }

      <!-- Two-Factor Auth Tab -->
      @if (activeTab() === 'tfa') {
        <coar-card padding="lg" class="section-card">
          @if (tfaError()) {
            <coar-note color="error" padding="sm" class="message">
              {{ tfaError() }}
            </coar-note>
          }

          @if (tfaSuccess()) {
            <coar-note color="success" padding="sm" class="message">
              {{ tfaSuccess() }}
            </coar-note>
          }

          <!-- Authenticator App Section -->
          <div class="tfa-section">
            <h3 class="section-title">Authenticator App</h3>

            @if (tfaStatus()?.hasAuthenticator) {
              <div class="tfa-enabled">
                <coar-note color="success" padding="md">
                  <strong>Authenticator app is configured</strong>
                  <p>
                    Recovery codes remaining:
                    {{ tfaStatus()?.recoveryCodesRemaining }}
                  </p>
                </coar-note>

                <div class="tfa-actions">
                  <coar-button
                    variant="secondary"
                    [loading]="isGeneratingCodes()"
                    (clicked)="onGenerateRecoveryCodes()">
                    Generate New Recovery Codes
                  </coar-button>

                  <coar-button
                    variant="danger"
                    (clicked)="showDisableForm.set(true)">
                    Disable Authenticator
                  </coar-button>
                </div>

                @if (recoveryCodes().length > 0) {
                  <div class="recovery-codes">
                    <h4>Recovery Codes</h4>
                    <coar-note color="warning" padding="sm">
                      Save these codes in a secure place. Each code can only be
                      used once.
                    </coar-note>
                    <div class="codes-list">
                      @for (code of recoveryCodes(); track code) {
                        <code>{{ code }}</code>
                      }
                    </div>
                  </div>
                }

                @if (showDisableForm()) {
                  <div class="disable-form">
                    <h4>Disable Authenticator App</h4>
                    <p>Enter your authenticator code to disable.</p>
                    <form [formGroup]="disableForm" (ngSubmit)="onDisable2FA()">
                      <div class="form-group">
                        <coar-text-input
                          label="Authentication Code"
                          placeholder="000000"
                          formControlName="code"
                          [required]="true"
                          [maxlength]="6" />
                      </div>
                      <div class="form-actions">
                        <coar-button
                          variant="ghost"
                          (clicked)="showDisableForm.set(false)">
                          Cancel
                        </coar-button>
                        <coar-button
                          type="submit"
                          variant="danger"
                          [loading]="isDisabling2FA()"
                          [disabled]="disableForm.invalid">
                          Disable
                        </coar-button>
                      </div>
                    </form>
                  </div>
                }
              </div>
            } @else {
              <div class="tfa-setup">
                @if (!tfaSetup()) {
                  <p>Add an authenticator app for additional security.</p>
                  <coar-button
                    variant="primary"
                    [loading]="isSettingUp2FA()"
                    (clicked)="onSetup2FA()">
                    Set Up Authenticator
                  </coar-button>
                } @else {
                  <h4>Scan QR Code</h4>
                  <p>
                    Scan this QR code with your authenticator app (Google
                    Authenticator, Authy, etc.)
                  </p>

                  <div class="qr-code">
                    <img [src]="qrCodeUrl()" alt="QR Code" />
                  </div>

                  <p class="manual-key">
                    Or enter this key manually:
                    <code>{{ tfaSetup()?.sharedKey }}</code>
                  </p>

                  <form [formGroup]="enableForm" (ngSubmit)="onEnable2FA()">
                    <div class="form-group">
                      <coar-text-input
                        label="Verification Code"
                        placeholder="000000"
                        formControlName="code"
                        hint="Enter the 6-digit code from your authenticator app"
                        [required]="true"
                        [maxlength]="6" />
                    </div>

                    <coar-button
                      type="submit"
                      variant="primary"
                      [loading]="isEnabling2FA()"
                      [disabled]="enableForm.invalid">
                      Enable Authenticator
                    </coar-button>
                  </form>
                }
              </div>
            }
          </div>

          <!-- Passkeys / Security Keys Section -->
          <div class="tfa-section">
            <h3 class="section-title">Passkeys & Security Keys</h3>

            @if (webAuthnCredentials().length > 0) {
              <div class="credentials-list">
                @for (cred of webAuthnCredentials(); track cred.id) {
                  <div class="credential-item">
                    <div class="credential-info">
                      <span class="credential-icon">🔑</span>
                      <div class="credential-details">
                        @if (editingCredentialId() === cred.id) {
                          <form
                            class="rename-form"
                            (ngSubmit)="onSaveCredentialName(cred.id)">
                            <coar-text-input
                              [value]="editingCredentialName()"
                              (input)="editingCredentialName.set($any($event.target).value)"
                              [maxlength]="50" />
                            <coar-button
                              type="submit"
                              variant="primary"
                              size="sm"
                              [loading]="isRenamingCredential()">
                              Save
                            </coar-button>
                            <coar-button
                              variant="ghost"
                              size="sm"
                              (clicked)="cancelEditCredential()">
                              Cancel
                            </coar-button>
                          </form>
                        } @else {
                          <span class="credential-name">{{ cred.deviceName }}</span>
                          <span class="credential-meta">
                            Added {{ formatDate(cred.createdAt) }}
                            @if (cred.lastUsedAt) {
                              · Last used {{ formatDate(cred.lastUsedAt) }}
                            }
                          </span>
                        }
                      </div>
                    </div>
                    @if (editingCredentialId() !== cred.id) {
                      <div class="credential-actions">
                        <button
                          type="button"
                          class="action-btn"
                          title="Rename"
                          (click)="onEditCredential(cred)">
                          ✏️
                        </button>
                        <button
                          type="button"
                          class="action-btn danger"
                          title="Delete"
                          (click)="onDeleteCredential(cred.id)">
                          🗑️
                        </button>
                      </div>
                    }
                  </div>
                }
              </div>
            } @else {
              <p class="no-credentials">No passkeys or security keys registered.</p>
            }

            <coar-button
              variant="secondary"
              [loading]="isRegisteringWebAuthn()"
              (clicked)="onRegisterWebAuthn()">
              Add Passkey / Security Key
            </coar-button>

            @if (showWebAuthnNameInput()) {
              <div class="webauthn-name-input">
                <div class="form-group">
                  <coar-text-input
                    label="Name this device"
                    placeholder="e.g., MacBook Touch ID, YubiKey"
                    [formControl]="webAuthnDeviceName"
                    [required]="true" />
                </div>
                <div class="form-actions">
                  <coar-button
                    variant="ghost"
                    (clicked)="cancelWebAuthnRegistration()">
                    Cancel
                  </coar-button>
                  <coar-button
                    variant="primary"
                    [loading]="isRegisteringWebAuthn()"
                    [disabled]="!webAuthnDeviceName.value"
                    (clicked)="onConfirmWebAuthnName()">
                    Continue
                  </coar-button>
                </div>
              </div>
            }
          </div>

          <!-- Email OTP Info -->
          <div class="tfa-section">
            <h3 class="section-title">Email Verification</h3>
            <coar-note color="info" padding="md">
              <strong>Always available</strong>
              <p>
                You can always request a one-time code via email during login.
                No setup required.
              </p>
            </coar-note>
          </div>
        </coar-card>
      }
    </div>
  `,
  styles: `
    .profile {
      max-width: 800px;
      margin: 0 auto;
    }

    .page-title {
      margin: 0 0 1.5rem;
      font-size: 1.5rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .tabs {
      display: flex;
      gap: 0.25rem;
      margin-bottom: 1rem;
      border-bottom: 1px solid var(--color-border-primary);
    }

    .tab {
      padding: 0.75rem 1.25rem;
      border: none;
      background: none;
      font-size: 0.875rem;
      font-weight: 500;
      color: var(--color-text-secondary);
      cursor: pointer;
      border-bottom: 2px solid transparent;
      margin-bottom: -1px;
      transition: all 0.15s ease;
    }

    .tab:hover {
      color: var(--color-text-primary);
    }

    .tab.active {
      color: var(--color-primary);
      border-bottom-color: var(--color-primary);
    }

    .section-card {
      margin-top: 1rem;
    }

    .message {
      margin-bottom: 1rem;
    }

    .form-row {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 1rem;
    }

    .form-group {
      margin-bottom: 1rem;
    }

    .tfa-section {
      padding: 1.5rem 0;
      border-bottom: 1px solid var(--color-border-primary);
    }

    .tfa-section:last-child {
      border-bottom: none;
      padding-bottom: 0;
    }

    .tfa-section:first-child {
      padding-top: 0;
    }

    .section-title {
      margin: 0 0 1rem;
      font-size: 1rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .tfa-enabled {
      display: flex;
      flex-direction: column;
      gap: 1rem;
    }

    .tfa-actions {
      display: flex;
      gap: 0.75rem;
    }

    .recovery-codes {
      margin-top: 1rem;
      padding: 1rem;
      background: var(--color-surface-secondary);
      border-radius: var(--radius-md);
    }

    .recovery-codes h4 {
      margin: 0 0 0.75rem;
      font-size: 0.875rem;
      font-weight: 600;
    }

    .codes-list {
      display: grid;
      grid-template-columns: repeat(2, 1fr);
      gap: 0.5rem;
      margin-top: 0.75rem;
    }

    .codes-list code {
      padding: 0.5rem;
      background: var(--color-surface-primary);
      border-radius: var(--radius-sm);
      font-family: monospace;
      font-size: 0.875rem;
    }

    .disable-form {
      margin-top: 1rem;
      padding: 1rem;
      background: var(--color-surface-secondary);
      border-radius: var(--radius-md);
    }

    .disable-form h4 {
      margin: 0 0 0.5rem;
      font-size: 0.875rem;
      font-weight: 600;
    }

    .disable-form p {
      margin: 0 0 1rem;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .form-actions {
      display: flex;
      gap: 0.75rem;
      justify-content: flex-end;
    }

    .tfa-setup {
      text-align: center;
    }

    .tfa-setup h4 {
      margin: 0 0 0.5rem;
      font-size: 1rem;
      font-weight: 600;
    }

    .tfa-setup p {
      margin: 0 0 1rem;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .qr-code {
      margin: 1.5rem auto;
      width: 200px;
      height: 200px;
      background: white;
      padding: 1rem;
      border-radius: var(--radius-md);
    }

    .qr-code img {
      width: 100%;
      height: 100%;
    }

    .manual-key {
      margin-bottom: 1.5rem;
    }

    .manual-key code {
      display: inline-block;
      padding: 0.25rem 0.5rem;
      background: var(--color-surface-secondary);
      border-radius: var(--radius-sm);
      font-family: monospace;
      word-break: break-all;
    }

    .credentials-list {
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      margin-bottom: 1rem;
    }

    .credential-item {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0.75rem 1rem;
      background: var(--color-surface-secondary);
      border-radius: var(--radius-md);
    }

    .credential-info {
      display: flex;
      align-items: center;
      gap: 0.75rem;
      flex: 1;
    }

    .credential-icon {
      font-size: 1.25rem;
    }

    .credential-details {
      display: flex;
      flex-direction: column;
      gap: 0.125rem;
    }

    .credential-name {
      font-weight: 500;
      color: var(--color-text-primary);
    }

    .credential-meta {
      font-size: 0.75rem;
      color: var(--color-text-secondary);
    }

    .credential-actions {
      display: flex;
      gap: 0.5rem;
    }

    .action-btn {
      padding: 0.25rem 0.5rem;
      border: none;
      background: none;
      cursor: pointer;
      opacity: 0.7;
      transition: opacity 0.15s;
    }

    .action-btn:hover {
      opacity: 1;
    }

    .action-btn.danger:hover {
      color: var(--color-error);
    }

    .rename-form {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }

    .no-credentials {
      margin: 0 0 1rem;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .webauthn-name-input {
      margin-top: 1rem;
      padding: 1rem;
      background: var(--color-surface-secondary);
      border-radius: var(--radius-md);
    }
  `,
})
export class ProfileComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApiService);
  private readonly authState = inject(AuthStateService);

  readonly activeTab = signal<TabId>('personal');

  readonly profile = signal<Profile | null>(null);
  readonly tfaStatus = signal<TwoFactorStatus | null>(null);
  readonly tfaSetup = signal<TwoFactorSetup | null>(null);
  readonly recoveryCodes = signal<string[]>([]);
  readonly webAuthnCredentials = signal<WebAuthnCredential[]>([]);

  readonly profileError = signal<string | null>(null);
  readonly profileSuccess = signal<string | null>(null);
  readonly passwordError = signal<string | null>(null);
  readonly passwordSuccess = signal<string | null>(null);
  readonly tfaError = signal<string | null>(null);
  readonly tfaSuccess = signal<string | null>(null);

  readonly isUpdatingProfile = signal(false);
  readonly isChangingPassword = signal(false);
  readonly isSettingUp2FA = signal(false);
  readonly isEnabling2FA = signal(false);
  readonly isDisabling2FA = signal(false);
  readonly isGeneratingCodes = signal(false);
  readonly isRegisteringWebAuthn = signal(false);
  readonly isRenamingCredential = signal(false);

  readonly showDisableForm = signal(false);
  readonly showWebAuthnNameInput = signal(false);
  readonly editingCredentialId = signal<string | null>(null);
  readonly editingCredentialName = signal('');

  readonly profileForm = this.fb.nonNullable.group({
    firstName: [''],
    lastName: [''],
    phoneNumber: [''],
  });

  readonly passwordForm = this.fb.nonNullable.group({
    currentPassword: ['', [Validators.required]],
    newPassword: ['', [Validators.required, Validators.minLength(8)]],
    confirmPassword: ['', [Validators.required]],
  });

  readonly enableForm = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^\d{6}$/)]],
  });

  readonly disableForm = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^\d{6}$/)]],
  });

  readonly webAuthnDeviceName = this.fb.control('', [Validators.required]);

  private pendingWebAuthnOptions: unknown = null;
  private pendingWebAuthnCredential: PublicKeyCredential | null = null;

  readonly qrCodeUrl = () => {
    const setup = this.tfaSetup();
    if (!setup) return '';
    return `https://api.qrserver.com/v1/create-qr-code/?size=200x200&data=${encodeURIComponent(setup.authenticatorUri)}`;
  };

  ngOnInit(): void {
    this.loadData();
  }

  private loadData(): void {
    forkJoin([
      this.authApi.getProfile(),
      this.authApi.getTwoFactorStatus(),
      this.authApi.getWebAuthnCredentials(),
    ]).subscribe({
      next: ([profile, tfaStatus, webAuthnCreds]) => {
        this.profile.set(profile);
        this.tfaStatus.set(tfaStatus);
        this.webAuthnCredentials.set(webAuthnCreds.credentials);
        this.profileForm.patchValue({
          firstName: profile.firstName || '',
          lastName: profile.lastName || '',
          phoneNumber: profile.phoneNumber || '',
        });
        this.profileForm.markAsPristine();
      },
      error: () => {
        this.profileError.set('Failed to load profile data.');
      },
    });
  }

  onUpdateProfile(): void {
    if (this.profileForm.pristine) return;

    const { firstName, lastName, phoneNumber } = this.profileForm.getRawValue();

    this.isUpdatingProfile.set(true);
    this.profileError.set(null);
    this.profileSuccess.set(null);

    this.authApi
      .updateProfile({
        firstName: firstName || undefined,
        lastName: lastName || undefined,
        phoneNumber: phoneNumber || undefined,
      })
      .pipe(
        finalize(() => this.isUpdatingProfile.set(false)),
        catchError((err) => {
          this.profileError.set(
            err?.error?.message || 'Failed to update profile.'
          );
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result) {
          this.profile.set(result);
          this.profileForm.markAsPristine();
          this.profileSuccess.set('Profile updated successfully.');
          this.authState.refreshUser();
        }
      });
  }

  onChangePassword(): void {
    const { currentPassword, newPassword, confirmPassword } =
      this.passwordForm.getRawValue();

    if (newPassword !== confirmPassword) {
      this.passwordError.set('Passwords do not match.');
      return;
    }

    this.isChangingPassword.set(true);
    this.passwordError.set(null);
    this.passwordSuccess.set(null);

    this.authApi
      .changePassword({ currentPassword, newPassword })
      .pipe(
        finalize(() => this.isChangingPassword.set(false)),
        catchError((err) => {
          this.passwordError.set(
            err?.error?.message || 'Failed to change password.'
          );
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.passwordForm.reset();
          this.passwordSuccess.set('Password changed successfully.');
        }
      });
  }

  onSetup2FA(): void {
    this.isSettingUp2FA.set(true);
    this.tfaError.set(null);

    this.authApi
      .setupTwoFactor()
      .pipe(
        finalize(() => this.isSettingUp2FA.set(false)),
        catchError((err) => {
          this.tfaError.set(err?.error?.message || 'Failed to set up 2FA.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result) {
          this.tfaSetup.set(result);
        }
      });
  }

  onEnable2FA(): void {
    const { code } = this.enableForm.getRawValue();

    this.isEnabling2FA.set(true);
    this.tfaError.set(null);

    this.authApi
      .enableTwoFactor({ code })
      .pipe(
        finalize(() => this.isEnabling2FA.set(false)),
        catchError((err) => {
          this.tfaError.set(err?.error?.message || 'Invalid code.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result) {
          this.recoveryCodes.set(result.codes);
          this.tfaSetup.set(null);
          this.tfaStatus.set({
            isEnabled: true,
            hasAuthenticator: true,
            recoveryCodesRemaining: result.codes.length,
            hasEmailOtp: this.tfaStatus()?.hasEmailOtp ?? true,
            webAuthnCredentialCount: this.tfaStatus()?.webAuthnCredentialCount ?? 0,
          });
          this.tfaSuccess.set('Authenticator app enabled!');
          this.enableForm.reset();
        }
      });
  }

  onDisable2FA(): void {
    const { code } = this.disableForm.getRawValue();

    this.isDisabling2FA.set(true);
    this.tfaError.set(null);

    this.authApi
      .disableTwoFactor({ code })
      .pipe(
        finalize(() => this.isDisabling2FA.set(false)),
        catchError((err) => {
          this.tfaError.set(err?.error?.message || 'Invalid code.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.tfaStatus.set({
            isEnabled: (this.tfaStatus()?.webAuthnCredentialCount ?? 0) > 0,
            hasAuthenticator: false,
            recoveryCodesRemaining: 0,
            hasEmailOtp: this.tfaStatus()?.hasEmailOtp ?? true,
            webAuthnCredentialCount: this.tfaStatus()?.webAuthnCredentialCount ?? 0,
          });
          this.recoveryCodes.set([]);
          this.showDisableForm.set(false);
          this.disableForm.reset();
          this.tfaSuccess.set('Authenticator app disabled.');
        }
      });
  }

  onGenerateRecoveryCodes(): void {
    this.isGeneratingCodes.set(true);
    this.tfaError.set(null);

    this.authApi
      .generateRecoveryCodes()
      .pipe(
        finalize(() => this.isGeneratingCodes.set(false)),
        catchError((err) => {
          this.tfaError.set(
            err?.error?.message || 'Failed to generate recovery codes.'
          );
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result) {
          this.recoveryCodes.set(result.codes);
          const status = this.tfaStatus();
          if (status) {
            this.tfaStatus.set({
              ...status,
              recoveryCodesRemaining: result.codes.length,
            });
          }
        }
      });
  }

  async onRegisterWebAuthn(): Promise<void> {
    // Check if WebAuthn is supported
    if (!window.PublicKeyCredential) {
      this.tfaError.set('WebAuthn is not supported in this browser.');
      return;
    }

    this.isRegisteringWebAuthn.set(true);
    this.tfaError.set(null);

    try {
      // Get registration options from server
      const optionsResponse = await this.authApi
        .getWebAuthnRegistrationOptions()
        .toPromise();

      if (!optionsResponse) {
        throw new Error('Failed to get registration options');
      }

      // Prepare options for navigator.credentials.create
      const options = optionsResponse.options as PublicKeyCredentialCreationOptions;

      const publicKeyOptions: PublicKeyCredentialCreationOptions = {
        ...options,
        challenge: this.base64UrlToBuffer(options.challenge as unknown as string),
        user: {
          ...options.user,
          id: this.base64UrlToBuffer(options.user.id as unknown as string),
        },
        excludeCredentials: options.excludeCredentials?.map((cred) => ({
          ...cred,
          id: this.base64UrlToBuffer(cred.id as unknown as string),
        })),
      };

      // Request credential from authenticator
      const credential = (await navigator.credentials.create({
        publicKey: publicKeyOptions,
      })) as PublicKeyCredential;

      if (!credential) {
        throw new Error('No credential returned');
      }

      // Store credential and show name input
      this.pendingWebAuthnOptions = optionsResponse.options;
      this.pendingWebAuthnCredential = credential;
      this.showWebAuthnNameInput.set(true);
      this.isRegisteringWebAuthn.set(false);
    } catch (err: unknown) {
      console.error('WebAuthn registration failed:', err);
      const error = err as Error;
      if (error.name === 'NotAllowedError') {
        this.tfaError.set('Registration was cancelled or timed out.');
      } else {
        this.tfaError.set(
          error.message || 'WebAuthn registration failed. Please try again.'
        );
      }
      this.isRegisteringWebAuthn.set(false);
    }
  }

  async onConfirmWebAuthnName(): Promise<void> {
    if (!this.pendingWebAuthnCredential || !this.webAuthnDeviceName.value) {
      return;
    }

    this.isRegisteringWebAuthn.set(true);

    try {
      const credential = this.pendingWebAuthnCredential;
      const response = credential.response as AuthenticatorAttestationResponse;

      // Build attestation response
      const attestationResponse = {
        id: credential.id,
        rawId: this.bufferToBase64Url(credential.rawId),
        type: credential.type,
        response: {
          attestationObject: this.bufferToBase64Url(response.attestationObject),
          clientDataJSON: this.bufferToBase64Url(response.clientDataJSON),
        },
      };

      await this.authApi
        .completeWebAuthnRegistration({
          attestationResponse,
          deviceName: this.webAuthnDeviceName.value,
        })
        .toPromise();

      // Refresh credentials list
      const credsResponse = await this.authApi.getWebAuthnCredentials().toPromise();
      if (credsResponse) {
        this.webAuthnCredentials.set(credsResponse.credentials);
      }

      // Update status
      const status = this.tfaStatus();
      if (status) {
        this.tfaStatus.set({
          ...status,
          isEnabled: true,
          webAuthnCredentialCount: (status.webAuthnCredentialCount || 0) + 1,
        });
      }

      this.tfaSuccess.set('Passkey registered successfully!');
      this.cancelWebAuthnRegistration();
    } catch (err: unknown) {
      console.error('WebAuthn registration completion failed:', err);
      const error = err as Error;
      this.tfaError.set(
        error.message || 'Failed to complete registration. Please try again.'
      );
    } finally {
      this.isRegisteringWebAuthn.set(false);
    }
  }

  cancelWebAuthnRegistration(): void {
    this.showWebAuthnNameInput.set(false);
    this.webAuthnDeviceName.reset();
    this.pendingWebAuthnOptions = null;
    this.pendingWebAuthnCredential = null;
  }

  onEditCredential(cred: WebAuthnCredential): void {
    this.editingCredentialId.set(cred.id);
    this.editingCredentialName.set(cred.deviceName);
  }

  cancelEditCredential(): void {
    this.editingCredentialId.set(null);
    this.editingCredentialName.set('');
  }

  onSaveCredentialName(credentialId: string): void {
    const newName = this.editingCredentialName().trim();
    if (!newName) return;

    this.isRenamingCredential.set(true);

    this.authApi
      .renameWebAuthnCredential(credentialId, { name: newName })
      .pipe(
        finalize(() => this.isRenamingCredential.set(false)),
        catchError((err) => {
          this.tfaError.set(
            err?.error?.message || 'Failed to rename credential.'
          );
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          // Update local list
          const creds = this.webAuthnCredentials();
          const updated = creds.map((c) =>
            c.id === credentialId ? { ...c, deviceName: newName } : c
          );
          this.webAuthnCredentials.set(updated);
          this.cancelEditCredential();
        }
      });
  }

  onDeleteCredential(credentialId: string): void {
    if (!confirm('Are you sure you want to delete this passkey?')) {
      return;
    }

    this.authApi
      .deleteWebAuthnCredential(credentialId)
      .pipe(
        catchError((err) => {
          this.tfaError.set(
            err?.error?.message || 'Failed to delete credential.'
          );
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          // Update local list
          const creds = this.webAuthnCredentials().filter(
            (c) => c.id !== credentialId
          );
          this.webAuthnCredentials.set(creds);

          // Update status
          const status = this.tfaStatus();
          if (status) {
            const newCount = Math.max(0, (status.webAuthnCredentialCount || 1) - 1);
            this.tfaStatus.set({
              ...status,
              isEnabled: status.hasAuthenticator || newCount > 0,
              webAuthnCredentialCount: newCount,
            });
          }

          this.tfaSuccess.set('Passkey deleted.');
        }
      });
  }

  formatDate(dateStr: string): string {
    const date = new Date(dateStr);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));

    if (diffDays === 0) {
      return 'today';
    } else if (diffDays === 1) {
      return 'yesterday';
    } else if (diffDays < 7) {
      return `${diffDays} days ago`;
    } else {
      return date.toLocaleDateString();
    }
  }

  getPasswordFieldError(field: string): string {
    const control = this.passwordForm.get(field);
    if (!control?.touched || !control.errors) return '';

    if (control.errors['required']) {
      const labels: Record<string, string> = {
        currentPassword: 'Current Password',
        newPassword: 'New Password',
        confirmPassword: 'Confirm Password',
      };
      return `${labels[field]} is required`;
    }

    if (control.errors['minlength']) {
      return 'Must be at least 8 characters';
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
