import { Component, inject, signal, OnInit, input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import {
  CoarButtonComponent,
  CoarCardComponent,
  CoarNoteComponent,
  CoarTextInputComponent,
  CoarPasswordInputComponent,
  CoarCheckboxComponent,
  CoarMultiSelectComponent,
} from '@cocoar/ui';
import { AdminApiService } from '../../../../core/services/admin-api.service';
import { User, Role } from '../../../../core/models/auth.models';
import { catchError, of, finalize, forkJoin } from 'rxjs';

@Component({
  selector: 'app-user-form',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    ReactiveFormsModule,
    CoarButtonComponent,
    CoarCardComponent,
    CoarNoteComponent,
    CoarTextInputComponent,
    CoarPasswordInputComponent,
    CoarCheckboxComponent,
    CoarMultiSelectComponent,
  ],
  template: `
    <div class="user-form">
      <header class="page-header">
        <div>
          <h1 class="page-title">{{ isEditMode() ? 'Edit User' : 'Create User' }}</h1>
          <p class="page-subtitle">
            {{ isEditMode() ? 'Update user information' : 'Create a new user account' }}
          </p>
        </div>
      </header>

      @if (error()) {
        <coar-note color="error" padding="sm" class="message">
          {{ error() }}
        </coar-note>
      }

      @if (isLoading()) {
        <div class="loading">
          <div class="spinner"></div>
          <p>Loading...</p>
        </div>
      } @else {
        <coar-card padding="lg">
          <form [formGroup]="form" (ngSubmit)="onSubmit()">
            <div class="form-section">
              <h2>Account Information</h2>

              <div class="form-row">
                <div class="form-group">
                  <coar-text-input
                    label="Username"
                    placeholder="Enter username"
                    formControlName="userName"
                    autocomplete="username"
                    [required]="true"
                    [disabled]="isEditMode()"
                    [error]="getFieldError('userName')" />
                </div>

                <div class="form-group">
                  <coar-text-input
                    label="Email"
                    placeholder="user&#64;example.com"
                    formControlName="email"
                    autocomplete="email"
                    [error]="getFieldError('email')" />
                </div>
              </div>

              @if (!isEditMode()) {
                <div class="form-group">
                  <coar-password-input
                    label="Password"
                    placeholder="Enter password"
                    formControlName="password"
                    autocomplete="new-password"
                    [required]="true"
                    [error]="getFieldError('password')" />
                </div>
              }
            </div>

            <div class="form-section">
              <h2>Personal Information</h2>

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
            </div>

            <div class="form-section">
              <h2>Roles & Permissions</h2>

              <div class="form-group">
                <coar-multi-select
                  label="Roles"
                  placeholder="Select roles..."
                  formControlName="roles"
                  [options]="roleOptions()" />
              </div>
            </div>

            <div class="form-section">
              <h2>Account Settings</h2>

              <div class="checkbox-group">
                <coar-checkbox
                  formControlName="isActive"
                  label="Account is active" />

                <coar-checkbox
                  formControlName="lockoutEnabled"
                  label="Enable account lockout" />

                @if (isEditMode()) {
                  <coar-checkbox
                    formControlName="emailConfirmed"
                    label="Email is verified" />

                  <coar-checkbox
                    formControlName="twoFactorEnabled"
                    label="Two-factor authentication enabled" />
                }
              </div>
            </div>

            <div class="form-actions">
              <coar-button
                variant="ghost"
                routerLink="/admin/users">
                Cancel
              </coar-button>
              <coar-button
                type="submit"
                variant="primary"
                [loading]="isSaving()"
                [disabled]="form.invalid || form.pristine">
                {{ isEditMode() ? 'Save Changes' : 'Create User' }}
              </coar-button>
            </div>
          </form>
        </coar-card>

        @if (isEditMode() && user()) {
          <coar-card padding="lg" color="error" class="danger-zone">
            <h2>Danger Zone</h2>

            <div class="danger-actions">
              <div class="danger-action">
                <div>
                  <strong>Reset Password</strong>
                  <p>Set a new password for this user</p>
                </div>
                <coar-button
                  variant="secondary"
                  size="sm"
                  (clicked)="showResetPassword.set(true)">
                  Reset Password
                </coar-button>
              </div>

              <div class="danger-action">
                <div>
                  <strong>Delete User</strong>
                  <p>Permanently delete this user account</p>
                </div>
                <coar-button
                  variant="danger"
                  size="sm"
                  [loading]="isDeleting()"
                  (clicked)="onDelete()">
                  Delete
                </coar-button>
              </div>
            </div>

            @if (showResetPassword()) {
              <div class="reset-password-form">
                <coar-password-input
                  label="New Password"
                  placeholder="Enter new password"
                  [(value)]="newPassword"
                  autocomplete="new-password" />
                <div class="reset-actions">
                  <coar-button
                    variant="ghost"
                    size="sm"
                    (clicked)="showResetPassword.set(false); newPassword = ''">
                    Cancel
                  </coar-button>
                  <coar-button
                    variant="primary"
                    size="sm"
                    [loading]="isResettingPassword()"
                    [disabled]="!newPassword"
                    (clicked)="onResetPassword()">
                    Reset
                  </coar-button>
                </div>
              </div>
            }
          </coar-card>
        }
      }
    </div>
  `,
  styles: `
    .user-form {
      max-width: 800px;
    }

    .page-header {
      margin-bottom: 1.5rem;
    }

    .page-title {
      margin: 0 0 0.25rem;
      font-size: 1.5rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .page-subtitle {
      margin: 0;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .message {
      margin-bottom: 1rem;
    }

    .loading {
      display: flex;
      flex-direction: column;
      align-items: center;
      padding: 3rem;
    }

    .spinner {
      width: 32px;
      height: 32px;
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

    .form-section {
      margin-bottom: 2rem;
    }

    .form-section h2 {
      margin: 0 0 1rem;
      font-size: 1rem;
      font-weight: 600;
      color: var(--color-text-primary);
      padding-bottom: 0.5rem;
      border-bottom: 1px solid var(--color-border-primary);
    }

    .form-row {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 1rem;
    }

    .form-group {
      margin-bottom: 1rem;
    }

    .checkbox-group {
      display: flex;
      flex-direction: column;
      gap: 0.75rem;
    }

    .form-actions {
      display: flex;
      gap: 0.75rem;
      justify-content: flex-end;
      padding-top: 1rem;
      border-top: 1px solid var(--color-border-primary);
    }

    .danger-zone {
      margin-top: 1.5rem;
    }

    .danger-zone h2 {
      margin: 0 0 1rem;
      font-size: 1rem;
      font-weight: 600;
      color: var(--color-error);
    }

    .danger-actions {
      display: flex;
      flex-direction: column;
      gap: 1rem;
    }

    .danger-action {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 0.75rem;
      background: var(--color-surface-primary);
      border-radius: var(--radius-md);
    }

    .danger-action strong {
      display: block;
      font-size: 0.875rem;
      color: var(--color-text-primary);
    }

    .danger-action p {
      margin: 0.25rem 0 0;
      font-size: 0.75rem;
      color: var(--color-text-secondary);
    }

    .reset-password-form {
      margin-top: 1rem;
      padding: 1rem;
      background: var(--color-surface-primary);
      border-radius: var(--radius-md);
    }

    .reset-actions {
      display: flex;
      gap: 0.5rem;
      justify-content: flex-end;
      margin-top: 0.75rem;
    }
  `,
})
export class UserFormComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);

  // Route param input
  id = input<string>();

  readonly user = signal<User | null>(null);
  readonly roles = signal<Role[]>([]);
  readonly isLoading = signal(false);
  readonly isSaving = signal(false);
  readonly isDeleting = signal(false);
  readonly isResettingPassword = signal(false);
  readonly showResetPassword = signal(false);
  readonly error = signal<string | null>(null);

  newPassword = '';

  readonly isEditMode = () => !!this.id();

  readonly roleOptions = () =>
    this.roles().map((role) => ({
      value: role.id,
      label: role.name,
    }));

  readonly form = this.fb.nonNullable.group({
    userName: ['', [Validators.required, Validators.minLength(3)]],
    email: ['', [Validators.email]],
    password: [''],
    firstName: [''],
    lastName: [''],
    phoneNumber: [''],
    roles: [[] as string[]],
    isActive: [true],
    lockoutEnabled: [true],
    emailConfirmed: [false],
    twoFactorEnabled: [false],
  });

  ngOnInit(): void {
    if (this.isEditMode()) {
      this.loadUserAndRoles();
    } else {
      this.loadRoles();
      // Password required for create
      this.form.get('password')?.setValidators([Validators.required, Validators.minLength(8)]);
      this.form.get('password')?.updateValueAndValidity();
    }
  }

  private loadRoles(): void {
    this.adminApi.getRoles().subscribe({
      next: (result) => this.roles.set(result.items),
      error: () => this.error.set('Failed to load roles.'),
    });
  }

  private loadUserAndRoles(): void {
    const userId = this.id();
    if (!userId) return;

    this.isLoading.set(true);

    // Load both roles and user in parallel, then patch form after both complete
    forkJoin({
      roles: this.adminApi.getRoles(),
      user: this.adminApi.getUser(userId),
    })
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load user data.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result) {
          // Set roles FIRST so multi-select has options
          this.roles.set(result.roles.items);

          // Then set user and patch form
          const user = result.user;
          this.user.set(user);
          this.form.patchValue({
            userName: user.userName,
            email: user.email || '',
            firstName: user.firstName || '',
            lastName: user.lastName || '',
            phoneNumber: user.phoneNumber || '',
            roles: user.roles,
            isActive: user.isActive,
            lockoutEnabled: user.lockoutEnabled,
            emailConfirmed: user.emailConfirmed,
            twoFactorEnabled: user.twoFactorEnabled,
          });
          this.form.markAsPristine();
        }
      });
  }

  onSubmit(): void {
    if (this.form.invalid) return;

    this.isSaving.set(true);
    this.error.set(null);

    const formValue = this.form.getRawValue();

    if (this.isEditMode()) {
      const userId = this.id()!;
      this.adminApi
        .updateUser(userId, {
          email: formValue.email || null,
          firstName: formValue.firstName || null,
          lastName: formValue.lastName || null,
          phoneNumber: formValue.phoneNumber || null,
          roles: formValue.roles,
          isActive: formValue.isActive,
          lockoutEnabled: formValue.lockoutEnabled,
          emailConfirmed: formValue.emailConfirmed,
          twoFactorEnabled: formValue.twoFactorEnabled,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to update user.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.router.navigate(['/admin/users']);
          }
        });
    } else {
      this.adminApi
        .createUser({
          userName: formValue.userName,
          password: formValue.password,
          email: formValue.email || undefined,
          firstName: formValue.firstName || undefined,
          lastName: formValue.lastName || undefined,
          phoneNumber: formValue.phoneNumber || undefined,
          roles: formValue.roles,
          isActive: formValue.isActive,
          lockoutEnabled: formValue.lockoutEnabled,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to create user.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.router.navigate(['/admin/users']);
          }
        });
    }
  }

  onDelete(): void {
    const userId = this.id();
    if (!userId || !confirm('Are you sure you want to delete this user?')) return;

    this.isDeleting.set(true);
    this.error.set(null);

    this.adminApi
      .deleteUser(userId)
      .pipe(
        finalize(() => this.isDeleting.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete user.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.router.navigate(['/admin/users']);
        }
      });
  }

  onResetPassword(): void {
    const userId = this.id();
    if (!userId || !this.newPassword) return;

    this.isResettingPassword.set(true);
    this.error.set(null);

    this.adminApi
      .resetUserPassword(userId, { newPassword: this.newPassword })
      .pipe(
        finalize(() => this.isResettingPassword.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to reset password.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.showResetPassword.set(false);
          this.newPassword = '';
          // Show success somehow
        }
      });
  }

  getFieldError(field: string): string {
    const control = this.form.get(field);
    if (!control?.touched || !control.errors) return '';

    if (control.errors['required']) {
      const labels: Record<string, string> = {
        userName: 'Username',
        password: 'Password',
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
