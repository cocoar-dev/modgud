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

          @if (tfaStatus()?.isEnabled) {
            <div class="tfa-enabled">
              <coar-note color="success" padding="md">
                <strong>Two-factor authentication is enabled</strong>
                <p>Your account is protected with an authenticator app.</p>
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
                  Disable 2FA
                </coar-button>
              </div>

              @if (recoveryCodes().length > 0) {
                <div class="recovery-codes">
                  <h3>Recovery Codes</h3>
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
                  <h3>Disable Two-Factor Authentication</h3>
                  <p>Enter your authenticator code to disable 2FA.</p>
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
                        Disable 2FA
                      </coar-button>
                    </div>
                  </form>
                </div>
              }
            </div>
          } @else {
            <div class="tfa-setup">
              @if (!tfaSetup()) {
                <p>Protect your account with two-factor authentication.</p>
                <coar-button
                  variant="primary"
                  [loading]="isSettingUp2FA()"
                  (clicked)="onSetup2FA()">
                  Set Up 2FA
                </coar-button>
              } @else {
                <h3>Scan QR Code</h3>
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
                    Enable 2FA
                  </coar-button>
                </form>
              }
            </div>
          }
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

    .recovery-codes h3 {
      margin: 0 0 0.75rem;
      font-size: 1rem;
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

    .disable-form h3 {
      margin: 0 0 0.5rem;
      font-size: 1rem;
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

    .tfa-setup h3 {
      margin: 0 0 0.5rem;
      font-size: 1.125rem;
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

  readonly showDisableForm = signal(false);

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
    ]).subscribe({
      next: ([profile, tfaStatus]) => {
        this.profile.set(profile);
        this.tfaStatus.set(tfaStatus);
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
          });
          this.tfaSuccess.set('Two-factor authentication enabled!');
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
            isEnabled: false,
            hasAuthenticator: false,
            recoveryCodesRemaining: 0,
          });
          this.recoveryCodes.set([]);
          this.showDisableForm.set(false);
          this.disableForm.reset();
          this.tfaSuccess.set('Two-factor authentication disabled.');
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
}
