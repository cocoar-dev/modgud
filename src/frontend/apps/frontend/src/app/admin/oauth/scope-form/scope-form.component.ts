import { Component, inject, signal, OnInit, input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormBuilder, Validators, FormArray } from '@angular/forms';
import {
  CoarButtonComponent,
  CoarCardComponent,
  CoarNoteComponent,
  CoarTextInputComponent,
} from '@cocoar/ui';

import { catchError, of, finalize } from 'rxjs';
import { AdminApiService, OAuthScope, isStandardScope } from '../../../core';
import { UIService } from '../../../ui';

@Component({
  selector: 'app-oauth-scope-form',
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
  templateUrl: './scope-form.component.html',
  styleUrl: './scope-form.component.css',
})
export class OAuthScopeFormComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);
  private readonly ui = inject(UIService);

  id = input<string>();

  readonly scope = signal<OAuthScope | null>(null);
  readonly isLoading = signal(false);
  readonly isSaving = signal(false);
  readonly isDeleting = signal(false);
  readonly error = signal<string | null>(null);
  readonly isStandard = signal(false);

  readonly isEditMode = () => !!this.id();

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.minLength(2), Validators.pattern(/^[a-z][a-z0-9_:.-]*$/)]],
    displayName: [''],
    description: [''],
    resources: this.fb.array<string>([]),
  });

  get resourcesArray(): FormArray {
    return this.form.get('resources') as FormArray;
  }

  ngOnInit(): void {
    this.ui.set((ctx) => {
      ctx.header.title = this.isEditMode() ? 'Edit Scope' : 'Create Scope';
      ctx.header.subTitle = this.isEditMode() ? 'Update scope configuration' : 'Create a new OAuth scope';
      ctx.content.scrollable = true;
    });
    if (this.isEditMode()) {
      this.loadScope();
      this.form.get('name')?.disable();
    }
  }

  private loadScope(): void {
    const scopeId = this.id();
    if (!scopeId) return;

    this.isLoading.set(true);

    this.adminApi
      .getOAuthScope(scopeId)
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load scope.');
          return of(null);
        })
      )
      .subscribe((scope) => {
        if (scope) {
          this.scope.set(scope);
          this.isStandard.set(isStandardScope(scope.name));

          if (this.isStandard()) {
            this.error.set('Standard scopes cannot be modified.');
            this.form.disable();
          }

          this.form.patchValue({
            name: scope.name,
            displayName: scope.displayName || '',
            description: scope.description || '',
          });

          this.resourcesArray.clear();
          scope.resources.forEach(resource => {
            this.resourcesArray.push(this.fb.control(resource));
          });

          this.form.markAsPristine();
        }
      });
  }

  addResource(): void {
    this.resourcesArray.push(this.fb.control(''));
  }

  removeResource(index: number): void {
    this.resourcesArray.removeAt(index);
  }

  onSubmit(): void {
    if (this.form.invalid || this.isStandard()) return;

    this.isSaving.set(true);
    this.error.set(null);

    const formValue = this.form.getRawValue();
    const resources = formValue.resources
      .filter((r): r is string => r != null && r.trim() !== '');

    if (this.isEditMode()) {
      this.adminApi
        .updateOAuthScope(this.id()!, {
          displayName: formValue.displayName || undefined,
          description: formValue.description || undefined,
          resources,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to update scope.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.router.navigate(['/admin/oauth/scopes']);
          }
        });
    } else {
      this.adminApi
        .createOAuthScope({
          name: formValue.name,
          displayName: formValue.displayName || undefined,
          description: formValue.description || undefined,
          resources,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to create scope.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.router.navigate(['/admin/oauth/scopes']);
          }
        });
    }
  }

  onDelete(): void {
    const scopeId = this.id();
    const scopeName = this.scope()?.name;
    if (!scopeId || this.isStandard() || !confirm(`Are you sure you want to delete the scope "${scopeName}"?`)) {
      return;
    }

    this.isDeleting.set(true);
    this.error.set(null);

    this.adminApi
      .deleteOAuthScope(scopeId)
      .pipe(
        finalize(() => this.isDeleting.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete scope.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.router.navigate(['/admin/oauth/scopes']);
        }
      });
  }

  getFieldError(field: string): string {
    const control = this.form.get(field);
    if (!control?.touched || !control.errors) return '';

    if (control.errors['required']) {
      return 'This field is required';
    }

    if (control.errors['minlength']) {
      const minLength = control.errors['minlength'].requiredLength;
      return `Must be at least ${minLength} characters`;
    }

    if (control.errors['pattern']) {
      return 'Must start with a lowercase letter and contain only lowercase letters, numbers, underscores, colons, dots, or hyphens';
    }

    return '';
  }
}
