import { Component, inject, signal, OnInit, input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormBuilder, Validators, FormArray } from '@angular/forms';
import {
  CoarButtonComponent,
  CoarCardComponent,
  CoarNoteComponent,
  CoarTextInputComponent,
  CoarCheckboxComponent,
} from '@cocoar/ui';

import { catchError, of, finalize } from 'rxjs';
import {
  AdminApiService,
  OAuthApiResource,
  OAuthScope,
} from '../../../core';

@Component({
  selector: 'app-oauth-api-resource-form',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    ReactiveFormsModule,
    CoarButtonComponent,
    CoarCardComponent,
    CoarNoteComponent,
    CoarTextInputComponent,
    CoarCheckboxComponent,
  ],
  templateUrl: './api-resource-form.component.html',
  styleUrl: './api-resource-form.component.css',
})
export class OAuthApiResourceFormComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);

  id = input<string>();

  readonly apiResource = signal<OAuthApiResource | null>(null);
  readonly availableScopes = signal<OAuthScope[]>([]);
  readonly isLoading = signal(false);
  readonly isSaving = signal(false);
  readonly isDeleting = signal(false);
  readonly isRegenerating = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);
  readonly generatedSecret = signal<string | null>(null);

  readonly isEditMode = () => !!this.id();

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.minLength(3)]],
    displayName: [''],
    description: [''],
    enabled: [true],
    scopes: this.fb.array<boolean>([]),
    userClaims: this.fb.array<string>([]),
  });

  get scopesArray(): FormArray {
    return this.form.get('scopes') as FormArray;
  }

  get userClaimsArray(): FormArray {
    return this.form.get('userClaims') as FormArray;
  }

  ngOnInit(): void {
    this.loadScopes();
    if (this.isEditMode()) {
      this.loadApiResource();
      this.form.get('name')?.disable();
    } else {
      this.addUserClaim();
    }
  }

  private loadScopes(): void {
    this.adminApi
      .getOAuthScopes()
      .pipe(
        catchError(() => of({ items: [], totalCount: 0 }))
      )
      .subscribe((result) => {
        this.availableScopes.set(result.items);
        this.initializeScopesArray(result.items);
      });
  }

  private initializeScopesArray(scopes: OAuthScope[]): void {
    this.scopesArray.clear();
    scopes.forEach(() => {
      this.scopesArray.push(this.fb.control(false));
    });
  }

  private loadApiResource(): void {
    const resourceId = this.id();
    if (!resourceId) return;

    this.isLoading.set(true);

    this.adminApi
      .getOAuthApiResource(resourceId)
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load API resource.');
          return of(null);
        })
      )
      .subscribe((resource) => {
        if (resource) {
          this.apiResource.set(resource);
          this.populateForm(resource);
        }
      });
  }

  private populateForm(resource: OAuthApiResource): void {
    this.form.patchValue({
      name: resource.name,
      displayName: resource.displayName || '',
      description: resource.description || '',
      enabled: resource.enabled,
    });

    this.userClaimsArray.clear();
    resource.userClaims.forEach(claim => {
      this.userClaimsArray.push(this.fb.control(claim));
    });
    if (this.userClaimsArray.length === 0) {
      this.addUserClaim();
    }

    this.availableScopes().forEach((scope, index) => {
      this.scopesArray.at(index)?.setValue(resource.scopes.includes(scope.name));
    });

    this.form.markAsPristine();
  }

  addUserClaim(): void {
    this.userClaimsArray.push(this.fb.control(''));
  }

  removeUserClaim(index: number): void {
    this.userClaimsArray.removeAt(index);
  }

  onSubmit(): void {
    if (this.form.invalid) return;

    this.isSaving.set(true);
    this.error.set(null);
    this.success.set(null);
    this.generatedSecret.set(null);

    const formValue = this.form.getRawValue();

    const selectedScopes = this.availableScopes()
      .filter((_, index) => formValue.scopes[index])
      .map(s => s.name);

    const userClaims = formValue.userClaims
      .filter((claim): claim is string => claim != null && claim.trim() !== '');

    if (this.isEditMode()) {
      this.adminApi
        .updateOAuthApiResource(this.id()!, {
          displayName: formValue.displayName || undefined,
          description: formValue.description || undefined,
          enabled: formValue.enabled,
          scopes: selectedScopes,
          userClaims,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to update API resource.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.router.navigate(['/admin/oauth/api-resources']);
          }
        });
    } else {
      this.adminApi
        .createOAuthApiResource({
          name: formValue.name,
          displayName: formValue.displayName || undefined,
          description: formValue.description || undefined,
          enabled: formValue.enabled,
          scopes: selectedScopes,
          userClaims,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to create API resource.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.generatedSecret.set(result.apiSecret);
            this.success.set('API resource created successfully. Copy the API secret below - it will not be shown again.');
          }
        });
    }
  }

  onRegenerateSecret(): void {
    const resourceId = this.id();
    if (!resourceId || !confirm('Are you sure you want to regenerate the API secret? The old secret will stop working immediately.')) {
      return;
    }

    this.isRegenerating.set(true);
    this.error.set(null);
    this.generatedSecret.set(null);

    this.adminApi
      .regenerateApiSecret(resourceId)
      .pipe(
        finalize(() => this.isRegenerating.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to regenerate API secret.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result) {
          this.generatedSecret.set(result.apiSecret);
          this.success.set('API secret regenerated. Copy it now - it will not be shown again.');
        }
      });
  }

  onDelete(): void {
    const resourceId = this.id();
    const resourceName = this.apiResource()?.name;
    if (!resourceId || !confirm(`Are you sure you want to delete the API resource "${resourceName}"?`)) {
      return;
    }

    this.isDeleting.set(true);
    this.error.set(null);

    this.adminApi
      .deleteOAuthApiResource(resourceId)
      .pipe(
        finalize(() => this.isDeleting.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete API resource.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.router.navigate(['/admin/oauth/api-resources']);
        }
      });
  }

  copyToClipboard(text: string): void {
    navigator.clipboard.writeText(text).then(() => {
      this.success.set('Copied to clipboard!');
      setTimeout(() => this.success.set(null), 2000);
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

    return '';
  }
}
