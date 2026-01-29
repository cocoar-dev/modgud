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

import { catchError, of, finalize } from 'rxjs';
import { AdminApiService, Role } from '../../../core';

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
  templateUrl: './role-form.component.html',
  styleUrl: './role-form.component.css',
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
