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
  OAuthApi,
  OAuthScope,
} from '../../../core';
import { UIService } from '../../../ui';

@Component({
  selector: 'app-oauth-api-form',
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
  templateUrl: './api-form.component.html',
  styleUrl: './api-form.component.css',
})
export class OAuthApiFormComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);
  private readonly ui = inject(UIService);

  id = input<string>();

  readonly api = signal<OAuthApi | null>(null);
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
    this.ui.set((ctx) => {
      ctx.header.title = this.isEditMode() ? 'Edit API' : 'Create API';
      ctx.header.subTitle = this.isEditMode() ? 'Update API configuration' : 'Register a new API for introspection';
      ctx.content.scrollable = true;
    });
    this.loadScopes();
    if (this.isEditMode()) {
      this.loadApi();
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

  private loadApi(): void {
    const apiId = this.id();
    if (!apiId) return;

    this.isLoading.set(true);

    this.adminApi
      .getOAuthApi(apiId)
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load API.');
          return of(null);
        })
      )
      .subscribe((api) => {
        if (api) {
          this.api.set(api);
          this.populateForm(api);
        }
      });
  }

  private populateForm(api: OAuthApi): void {
    this.form.patchValue({
      name: api.name,
      displayName: api.displayName || '',
      description: api.description || '',
      enabled: api.enabled,
    });

    this.userClaimsArray.clear();
    api.userClaims.forEach(claim => {
      this.userClaimsArray.push(this.fb.control(claim));
    });
    if (this.userClaimsArray.length === 0) {
      this.addUserClaim();
    }

    this.availableScopes().forEach((scope, index) => {
      this.scopesArray.at(index)?.setValue(api.scopes.includes(scope.name));
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
        .updateOAuthApi(this.id()!, {
          displayName: formValue.displayName || undefined,
          description: formValue.description || undefined,
          enabled: formValue.enabled,
          scopes: selectedScopes,
          userClaims,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to update API.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.router.navigate(['/admin/oauth/apis']);
          }
        });
    } else {
      this.adminApi
        .createOAuthApi({
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
            this.error.set(err?.error?.message || 'Failed to create API.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.generatedSecret.set(result.apiSecret);
            this.success.set('API created successfully. Copy the API secret below - it will not be shown again.');
          }
        });
    }
  }

  onRegenerateSecret(): void {
    const apiId = this.id();
    if (!apiId || !confirm('Are you sure you want to regenerate the API secret? The old secret will stop working immediately.')) {
      return;
    }

    this.isRegenerating.set(true);
    this.error.set(null);
    this.generatedSecret.set(null);

    this.adminApi
      .regenerateApiSecret(apiId)
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
    const apiId = this.id();
    const apiName = this.api()?.name;
    if (!apiId || !confirm(`Are you sure you want to delete the API "${apiName}"?`)) {
      return;
    }

    this.isDeleting.set(true);
    this.error.set(null);

    this.adminApi
      .deleteOAuthApi(apiId)
      .pipe(
        finalize(() => this.isDeleting.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete API.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.router.navigate(['/admin/oauth/apis']);
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
