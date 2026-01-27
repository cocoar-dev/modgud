import { Component, inject, signal, OnInit, input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import {
  CoarButtonComponent,
  CoarCardComponent,
  CoarNoteComponent,
  CoarTextInputComponent,
} from '@cocoar/ui';
import { AdminApiService } from '../../../../core/services/admin-api.service';
import { Role } from '../../../../core/models/auth.models';
import { catchError, of, finalize } from 'rxjs';

@Component({
  selector: 'app-role-form',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    ReactiveFormsModule,
    CoarButtonComponent,
    CoarCardComponent,
    CoarNoteComponent,
    CoarTextInputComponent,
  ],
  template: `
    <div class="role-form">
      <header class="page-header">
        <div>
          <h1 class="page-title">{{ isEditMode() ? 'Edit Role' : 'Create Role' }}</h1>
          <p class="page-subtitle">
            {{ isEditMode() ? 'Update role information' : 'Create a new role' }}
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
            <div class="form-group">
              <coar-text-input
                label="Role Name"
                placeholder="Enter role name"
                formControlName="name"
                [required]="true"
                [error]="getFieldError('name')" />
            </div>

            <div class="form-group">
              <coar-text-input
                label="Description"
                placeholder="Enter role description"
                formControlName="description"
                [rows]="3" />
            </div>

            <div class="form-actions">
              <coar-button
                variant="ghost"
                routerLink="/admin/roles">
                Cancel
              </coar-button>
              <coar-button
                type="submit"
                variant="primary"
                [loading]="isSaving()"
                [disabled]="form.invalid || form.pristine">
                {{ isEditMode() ? 'Save Changes' : 'Create Role' }}
              </coar-button>
            </div>
          </form>
        </coar-card>

        @if (isEditMode() && role()) {
          <coar-card padding="lg" color="error" class="danger-zone">
            <h2>Danger Zone</h2>

            <div class="danger-action">
              <div>
                <strong>Delete Role</strong>
                <p>Permanently delete this role. Users with this role will lose it.</p>
              </div>
              <coar-button
                variant="danger"
                size="sm"
                [loading]="isDeleting()"
                (clicked)="onDelete()">
                Delete Role
              </coar-button>
            </div>
          </coar-card>
        }
      }
    </div>
  `,
  styles: `
    .role-form {
      max-width: 600px;
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

    .form-group {
      margin-bottom: 1rem;
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
  `,
})
export class RoleFormComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);

  // Route param input
  id = input<string>();

  readonly role = signal<Role | null>(null);
  readonly isLoading = signal(false);
  readonly isSaving = signal(false);
  readonly isDeleting = signal(false);
  readonly error = signal<string | null>(null);

  readonly isEditMode = () => !!this.id();

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.minLength(2)]],
    description: [''],
  });

  ngOnInit(): void {
    if (this.isEditMode()) {
      this.loadRole();
    }
  }

  private loadRole(): void {
    const roleId = this.id();
    if (!roleId) return;

    this.isLoading.set(true);

    this.adminApi
      .getRole(roleId)
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load role.');
          return of(null);
        })
      )
      .subscribe((role) => {
        if (role) {
          this.role.set(role);
          this.form.patchValue({
            name: role.name,
            description: role.description || '',
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
      const roleId = this.id()!;
      this.adminApi
        .updateRole(roleId, {
          name: formValue.name,
          description: formValue.description || null,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to update role.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.router.navigate(['/admin/roles']);
          }
        });
    } else {
      this.adminApi
        .createRole({
          name: formValue.name,
          description: formValue.description || undefined,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to create role.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.router.navigate(['/admin/roles']);
          }
        });
    }
  }

  onDelete(): void {
    const roleId = this.id();
    const roleName = this.role()?.name;
    if (!roleId || !confirm(`Are you sure you want to delete the role "${roleName}"?`)) {
      return;
    }

    this.isDeleting.set(true);
    this.error.set(null);

    this.adminApi
      .deleteRole(roleId)
      .pipe(
        finalize(() => this.isDeleting.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete role.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.router.navigate(['/admin/roles']);
        }
      });
  }

  getFieldError(field: string): string {
    const control = this.form.get(field);
    if (!control?.touched || !control.errors) return '';

    if (control.errors['required']) {
      return 'Role name is required';
    }

    if (control.errors['minlength']) {
      const minLength = control.errors['minlength'].requiredLength;
      return `Must be at least ${minLength} characters`;
    }

    return '';
  }
}
