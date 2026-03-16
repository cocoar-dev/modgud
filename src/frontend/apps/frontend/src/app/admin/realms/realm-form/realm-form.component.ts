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
import { AdminApiService, Realm } from '../../../core';
import { UIService } from '../../../ui';

@Component({
  selector: 'app-realm-form',
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
  templateUrl: './realm-form.component.html',
  styleUrl: './realm-form.component.css',
})
export class RealmFormComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);
  private readonly ui = inject(UIService);

  // Route param input — uses :slug instead of :id
  slug = input<string>();

  readonly realm = signal<Realm | null>(null);
  readonly isLoading = signal(false);
  readonly isSaving = signal(false);
  readonly error = signal<string | null>(null);

  readonly isEditMode = () => !!this.slug();

  readonly form = this.fb.nonNullable.group({
    slug: ['', [Validators.required, Validators.pattern(/^[a-z][a-z0-9-]+$/)]],
    displayName: ['', [Validators.required]],
    description: [''],
  });

  ngOnInit(): void {
    this.ui.set((ctx) => {
      ctx.header.title = this.isEditMode() ? 'Edit Realm' : 'Create Realm';
      ctx.header.subTitle = this.isEditMode() ? 'Update realm information' : 'Create a new realm';
      ctx.content.scrollable = true;
    });
    if (this.isEditMode()) {
      this.form.controls.slug.disable();
      this.loadRealm();
    }
  }

  private loadRealm(): void {
    const realmSlug = this.slug();
    if (!realmSlug) return;

    this.isLoading.set(true);

    this.adminApi
      .getRealm(realmSlug)
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load realm.');
          return of(null);
        })
      )
      .subscribe((realm) => {
        if (realm) {
          this.realm.set(realm);
          this.form.patchValue({
            slug: realm.slug,
            displayName: realm.displayName,
            description: realm.description || '',
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
      const realmSlug = this.slug()!;
      this.adminApi
        .updateRealm(realmSlug, {
          displayName: formValue.displayName,
          description: formValue.description || null,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to update realm.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.router.navigate(['/admin/realms']);
          }
        });
    } else {
      this.adminApi
        .createRealm({
          slug: formValue.slug,
          displayName: formValue.displayName,
          description: formValue.description || undefined,
        })
        .pipe(
          finalize(() => this.isSaving.set(false)),
          catchError((err) => {
            this.error.set(err?.error?.message || 'Failed to create realm.');
            return of(null);
          })
        )
        .subscribe((result) => {
          if (result) {
            this.router.navigate(['/admin/realms']);
          }
        });
    }
  }

  getFieldError(field: string): string {
    const control = this.form.get(field);
    if (!control?.touched || !control.errors) return '';

    if (control.errors['required']) {
      return `${field === 'slug' ? 'Slug' : 'Display name'} is required`;
    }

    if (control.errors['pattern']) {
      return 'Slug must start with a letter and contain only lowercase letters, numbers, and hyphens';
    }

    return '';
  }
}
