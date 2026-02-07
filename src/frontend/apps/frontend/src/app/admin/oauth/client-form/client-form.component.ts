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
  OAuthClient,
  OAuthScope,
} from '../../../core';

@Component({
  selector: 'app-oauth-client-form',
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
  templateUrl: './client-form.component.html',
  styleUrl: './client-form.component.css',
})
export class OAuthClientFormComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);

  id = input<string>();

  readonly client = signal<OAuthClient | null>(null);
  readonly availableScopes = signal<OAuthScope[]>([]);
  readonly isLoading = signal(false);
  readonly isSaving = signal(false);
  readonly isDeleting = signal(false);
  readonly isRegenerating = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);
  readonly generatedSecret = signal<string | null>(null);

  readonly isEditMode = () => !!this.id();

  readonly clientTypeOptions = [
    { value: 'public', label: 'Public (SPA, Mobile Apps)' },
    { value: 'confidential', label: 'Confidential (Server-side Apps)' },
  ];

  readonly consentTypeOptions = [
    { value: 'implicit', label: 'Implicit (First-party apps)' },
    { value: 'explicit', label: 'Explicit (Third-party apps)' },
    { value: 'external', label: 'External (Pre-authorized)' },
  ];

  readonly form = this.fb.nonNullable.group({
    clientId: ['', [Validators.required, Validators.minLength(3)]],
    displayName: [''],
    clientType: ['public' as 'public' | 'confidential', Validators.required],
    consentType: ['implicit' as 'explicit' | 'implicit' | 'external', Validators.required],
    redirectUris: this.fb.array<string>([]),
    postLogoutRedirectUris: this.fb.array<string>([]),
    scopes: this.fb.array<boolean>([]),
  });

  get redirectUrisArray(): FormArray {
    return this.form.get('redirectUris') as FormArray;
  }

  get postLogoutRedirectUrisArray(): FormArray {
    return this.form.get('postLogoutRedirectUris') as FormArray;
  }

  get scopesArray(): FormArray {
    return this.form.get('scopes') as FormArray;
  }

  ngOnInit(): void {
    this.loadScopes();
    if (this.isEditMode()) {
      this.loadClient();
      this.form.get('clientId')?.disable();
    } else {
      this.addRedirectUri();
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

  private loadClient(): void {
    const clientId = this.id();
    if (!clientId) return;

    this.isLoading.set(true);

    this.adminApi
      .getOAuthClient(clientId)
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load OAuth client.');
          return of(null);
        })
      )
      .subscribe((client) => {
        if (client) {
          this.client.set(client);
          this.populateForm(client);
        }
      });
  }

  private populateForm(client: OAuthClient): void {
    this.form.patchValue({
      clientId: client.clientId,
      displayName: client.displayName || '',
      clientType: client.clientType,
      consentType: client.consentType,
    });

    this.redirectUrisArray.clear();
    client.redirectUris.forEach(uri => {
      this.redirectUrisArray.push(this.fb.control(uri));
    });
    if (this.redirectUrisArray.length === 0) {
      this.addRedirectUri();
    }

    this.postLogoutRedirectUrisArray.clear();
    client.postLogoutRedirectUris.forEach(uri => {
      this.postLogoutRedirectUrisArray.push(this.fb.control(uri));
    });

    const clientScopes = client.permissions
      .filter(p => p.startsWith('scp:'))
      .map(p => p.substring(4));

    this.availableScopes().forEach((scope, index) => {
      this.scopesArray.at(index)?.setValue(clientScopes.includes(scope.name));
    });

    this.form.markAsPristine();
  }

  addRedirectUri(): void {
    this.redirectUrisArray.push(this.fb.control(''));
  }

  removeRedirectUri(index: number): void {
    this.redirectUrisArray.removeAt(index);
  }

  addPostLogoutRedirectUri(): void {
    this.postLogoutRedirectUrisArray.push(this.fb.control(''));
  }

  removePostLogoutRedirectUri(index: number): void {
    this.postLogoutRedirectUrisArray.removeAt(index);
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

    const redirectUris = formValue.redirectUris
      .filter((uri): uri is string => uri != null && uri.trim() !== '');
    const postLogoutRedirectUris = formValue.postLogoutRedirectUris
      .filter((uri): uri is string => uri != null && uri.trim() !== '');

    if (this.isEditMode()) {
      this.adminApi
        .updateOAuthClient(this.id()!, {
          displayName: formValue.displayName || undefined,
          consentType: formValue.consentType,
          redirectUris,
          postLogoutRedirectUris,
          scopes: selectedScopes,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to update OAuth client.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.router.navigate(['/admin/oauth/clients']);
          }
        });
    } else {
      this.adminApi
        .createOAuthClient({
          clientId: formValue.clientId,
          displayName: formValue.displayName || undefined,
          clientType: formValue.clientType,
          consentType: formValue.consentType,
          redirectUris,
          postLogoutRedirectUris,
          scopes: selectedScopes,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to create OAuth client.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            if (result.clientSecret) {
              this.generatedSecret.set(result.clientSecret);
              this.success.set('OAuth client created successfully. Copy the client secret below - it will not be shown again.');
            } else {
              this.router.navigate(['/admin/oauth/clients']);
            }
          }
        });
    }
  }

  onRegenerateSecret(): void {
    const clientId = this.id();
    if (!clientId || !confirm('Are you sure you want to regenerate the client secret? The old secret will stop working immediately.')) {
      return;
    }

    this.isRegenerating.set(true);
    this.error.set(null);
    this.generatedSecret.set(null);

    this.adminApi
      .regenerateClientSecret(clientId)
      .pipe(
        finalize(() => this.isRegenerating.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to regenerate client secret.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result) {
          this.generatedSecret.set(result.clientSecret);
          this.success.set('Client secret regenerated. Copy it now - it will not be shown again.');
        }
      });
  }

  onDelete(): void {
    const clientId = this.id();
    const clientName = this.client()?.clientId;
    if (!clientId || !confirm(`Are you sure you want to delete the OAuth client "${clientName}"?`)) {
      return;
    }

    this.isDeleting.set(true);
    this.error.set(null);

    this.adminApi
      .deleteOAuthClient(clientId)
      .pipe(
        finalize(() => this.isDeleting.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete OAuth client.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.router.navigate(['/admin/oauth/clients']);
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
