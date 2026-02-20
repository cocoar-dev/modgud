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
import { catchError, of, finalize, forkJoin } from 'rxjs';
import { AdminApiService, Role, User } from '../../../core';
import { UIService } from '../../../ui';

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
  templateUrl: './user-form.component.html',
  styleUrl: './user-form.component.css',
})
export class UserFormComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);
  private readonly ui = inject(UIService);

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
    this.ui.set((ctx) => {
      ctx.header.title = this.isEditMode() ? 'Edit User' : 'Create User';
      ctx.header.subTitle = this.isEditMode() ? 'Update user information' : 'Create a new user account';
      ctx.content.scrollable = true;
    });
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
