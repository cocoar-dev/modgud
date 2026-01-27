import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import {
  CoarButtonComponent,
  CoarCardComponent,
  CoarNoteComponent,
  CoarIconComponent,
  CoarPasswordInputComponent,
  CoarTextInputComponent,
} from '@cocoar/ui';
import { AuthApiService } from '../../core/services/auth-api.service';
import { AuthStateService } from '../../core/services/auth-state.service';
import { DeletionStatus, UserDataExport } from '../../core/models/auth.models';
import { catchError, of, finalize } from 'rxjs';

@Component({
  selector: 'app-privacy',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    CoarButtonComponent,
    CoarCardComponent,
    CoarNoteComponent,
    CoarIconComponent,
    CoarPasswordInputComponent,
    CoarTextInputComponent,
  ],
  template: `
    <div class="privacy">
      <h1 class="page-title">Privacy & Data</h1>
      <p class="page-subtitle">Manage your data and account deletion</p>

      <!-- Data Export Section -->
      <coar-card padding="lg" class="section-card">
        <div class="section-header">
          <coar-icon name="download" size="lg" class="section-icon" />
          <div>
            <h2>Export Your Data</h2>
            <p>Download a copy of all your personal data (GDPR Article 20)</p>
          </div>
        </div>

        @if (exportError()) {
          <coar-note color="error" padding="sm" class="message">
            {{ exportError() }}
          </coar-note>
        }

        <coar-button
          variant="secondary"
          iconStart="download"
          [loading]="isExporting()"
          (clicked)="onExportData()">
          Download My Data
        </coar-button>
      </coar-card>

      <!-- Account Deletion Section -->
      <coar-card padding="lg" class="section-card">
        <div class="section-header">
          <coar-icon name="trash-2" size="lg" class="section-icon danger" />
          <div>
            <h2>Delete Account</h2>
            <p>Permanently delete your account and all associated data</p>
          </div>
        </div>

        @if (deletionError()) {
          <coar-note color="error" padding="sm" class="message">
            {{ deletionError() }}
          </coar-note>
        }

        @if (deletionSuccess()) {
          <coar-note color="success" padding="sm" class="message">
            {{ deletionSuccess() }}
          </coar-note>
        }

        @if (deletionStatus()?.isPending) {
          <coar-note color="warning" padding="md" class="message">
            <strong>Deletion Pending</strong>
            <p>
              Your account is scheduled for deletion. Please check your email
              and confirm the deletion before
              {{ formatDate(deletionStatus()?.confirmationDeadline) }}.
            </p>
          </coar-note>

          <div class="deletion-actions">
            <coar-button
              variant="ghost"
              [loading]="isCancelling()"
              (clicked)="onCancelDeletion()">
              Cancel Deletion
            </coar-button>
          </div>
        } @else {
          <coar-note color="warning" padding="md" class="message">
            <strong>Warning:</strong> This action cannot be undone. All your data
            will be permanently deleted.
          </coar-note>

          @if (showDeleteForm()) {
            <form [formGroup]="deleteForm" (ngSubmit)="onRequestDeletion()">
              <div class="form-group">
                <coar-password-input
                  label="Confirm Password"
                  placeholder="Enter your password"
                  formControlName="password"
                  hint="Enter your password to confirm"
                  [required]="true" />
              </div>

              <div class="form-group">
                <coar-text-input
                  label="Reason (Optional)"
                  placeholder="Why are you leaving?"
                  formControlName="reason"
                  [rows]="3" />
              </div>

              <div class="form-actions">
                <coar-button
                  variant="ghost"
                  (clicked)="showDeleteForm.set(false)">
                  Cancel
                </coar-button>
                <coar-button
                  type="submit"
                  variant="danger"
                  [loading]="isDeleting()"
                  [disabled]="deleteForm.invalid">
                  Request Account Deletion
                </coar-button>
              </div>
            </form>
          } @else {
            <coar-button
              variant="danger"
              iconStart="trash-2"
              (clicked)="showDeleteForm.set(true)">
              Delete My Account
            </coar-button>
          }
        }
      </coar-card>

      <!-- Data Rights Info -->
      <coar-card padding="lg" class="section-card info-card">
        <h2>Your Data Rights</h2>
        <div class="rights-grid">
          <div class="right-item">
            <coar-icon name="eye" size="md" />
            <h3>Right to Access</h3>
            <p>You can request a copy of all personal data we hold about you.</p>
          </div>
          <div class="right-item">
            <coar-icon name="edit" size="md" />
            <h3>Right to Rectification</h3>
            <p>You can update your personal information in your profile settings.</p>
          </div>
          <div class="right-item">
            <coar-icon name="trash-2" size="md" />
            <h3>Right to Erasure</h3>
            <p>You can request permanent deletion of your account and data.</p>
          </div>
          <div class="right-item">
            <coar-icon name="download" size="md" />
            <h3>Right to Portability</h3>
            <p>You can export your data in a machine-readable format.</p>
          </div>
        </div>
      </coar-card>
    </div>
  `,
  styles: `
    .privacy {
      max-width: 800px;
      margin: 0 auto;
    }

    .page-title {
      margin: 0 0 0.25rem;
      font-size: 1.5rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .page-subtitle {
      margin: 0 0 1.5rem;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .section-card {
      margin-bottom: 1.5rem;
    }

    .section-header {
      display: flex;
      gap: 1rem;
      margin-bottom: 1rem;
    }

    .section-icon {
      color: var(--color-primary);
      flex-shrink: 0;
    }

    .section-icon.danger {
      color: var(--color-error);
    }

    .section-header h2 {
      margin: 0 0 0.25rem;
      font-size: 1.125rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .section-header p {
      margin: 0;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .message {
      margin-bottom: 1rem;
    }

    .form-group {
      margin-bottom: 1rem;
    }

    .form-actions {
      display: flex;
      gap: 0.75rem;
      justify-content: flex-end;
    }

    .deletion-actions {
      display: flex;
      gap: 0.75rem;
    }

    .info-card h2 {
      margin: 0 0 1.5rem;
      font-size: 1.125rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .rights-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 1.5rem;
    }

    .right-item {
      text-align: center;
    }

    .right-item coar-icon {
      color: var(--color-primary);
      margin-bottom: 0.75rem;
    }

    .right-item h3 {
      margin: 0 0 0.5rem;
      font-size: 0.875rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .right-item p {
      margin: 0;
      font-size: 0.75rem;
      color: var(--color-text-secondary);
    }
  `,
})
export class PrivacyComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly authApi = inject(AuthApiService);
  private readonly authState = inject(AuthStateService);

  readonly deletionStatus = signal<DeletionStatus | null>(null);
  readonly isExporting = signal(false);
  readonly isDeleting = signal(false);
  readonly isCancelling = signal(false);
  readonly showDeleteForm = signal(false);

  readonly exportError = signal<string | null>(null);
  readonly deletionError = signal<string | null>(null);
  readonly deletionSuccess = signal<string | null>(null);

  readonly deleteForm = this.fb.nonNullable.group({
    password: ['', [Validators.required]],
    reason: [''],
  });

  ngOnInit(): void {
    this.loadDeletionStatus();
  }

  private loadDeletionStatus(): void {
    this.authApi
      .getDeletionStatus()
      .pipe(
        catchError(() => of({ isPending: false, isDeleted: false, isDataMasked: false }))
      )
      .subscribe((status) => {
        this.deletionStatus.set(status);
      });
  }

  onExportData(): void {
    this.isExporting.set(true);
    this.exportError.set(null);

    this.authApi
      .exportData()
      .pipe(
        finalize(() => this.isExporting.set(false)),
        catchError((err) => {
          this.exportError.set(err?.error?.message || 'Failed to export data.');
          return of(null);
        })
      )
      .subscribe((data) => {
        if (data) {
          this.downloadJson(data, 'my-data-export.json');
        }
      });
  }

  onRequestDeletion(): void {
    const { password, reason } = this.deleteForm.getRawValue();

    this.isDeleting.set(true);
    this.deletionError.set(null);
    this.deletionSuccess.set(null);

    this.authApi
      .requestDeletion({ password, reason: reason || undefined })
      .pipe(
        finalize(() => this.isDeleting.set(false)),
        catchError((err) => {
          this.deletionError.set(
            err?.error?.message || 'Failed to request account deletion.'
          );
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result) {
          this.deletionSuccess.set(result.message);
          this.showDeleteForm.set(false);
          this.deleteForm.reset();
          this.loadDeletionStatus();
        }
      });
  }

  onCancelDeletion(): void {
    this.isCancelling.set(true);
    this.deletionError.set(null);
    this.deletionSuccess.set(null);

    this.authApi
      .cancelDeletion()
      .pipe(
        finalize(() => this.isCancelling.set(false)),
        catchError((err) => {
          this.deletionError.set(
            err?.error?.message || 'Failed to cancel deletion.'
          );
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.deletionSuccess.set('Account deletion cancelled.');
          this.loadDeletionStatus();
        }
      });
  }

  formatDate(dateStr?: string): string {
    if (!dateStr) return 'Unknown';
    const date = new Date(dateStr);
    return date.toLocaleDateString('en-US', {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
    });
  }

  private downloadJson(data: UserDataExport, filename: string): void {
    const blob = new Blob([JSON.stringify(data, null, 2)], {
      type: 'application/json',
    });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
  }
}
