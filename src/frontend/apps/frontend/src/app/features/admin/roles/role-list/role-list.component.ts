import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import {
  CoarButtonComponent,
  CoarCardComponent,
  CoarNoteComponent,
  CoarIconComponent,
} from '@cocoar/ui';
import { AdminApiService } from '../../../../core/services/admin-api.service';
import { Role } from '../../../../core/models/auth.models';
import { catchError, of, finalize } from 'rxjs';

@Component({
  selector: 'app-role-list',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    CoarButtonComponent,
    CoarCardComponent,
    CoarNoteComponent,
    CoarIconComponent,
  ],
  template: `
    <div class="role-list">
      <header class="page-header">
        <div>
          <h1 class="page-title">Roles</h1>
          <p class="page-subtitle">Manage user roles</p>
        </div>
        <coar-button
          variant="primary"
          iconStart="plus"
          routerLink="/admin/roles/create">
          Create Role
        </coar-button>
      </header>

      @if (error()) {
        <coar-note color="error" padding="sm" class="message">
          {{ error() }}
        </coar-note>
      }

      @if (success()) {
        <coar-note color="success" padding="sm" class="message">
          {{ success() }}
        </coar-note>
      }

      @if (isLoading()) {
        <div class="loading">
          <div class="spinner"></div>
          <p>Loading roles...</p>
        </div>
      } @else {
        <coar-card padding="none" class="roles-table-card">
          <table class="roles-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Description</th>
                <th>Created</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (role of roles(); track role.id) {
                <tr>
                  <td class="role-name">
                    <coar-icon name="shield" size="sm" class="role-icon" />
                    <span>{{ role.name }}</span>
                  </td>
                  <td>
                    {{ role.description || '-' }}
                  </td>
                  <td class="text-muted">{{ formatDate(role.createdAt) }}</td>
                  <td class="actions">
                    <coar-button
                      variant="ghost"
                      size="sm"
                      iconStart="edit"
                      [routerLink]="['/admin/roles', role.id]">
                      Edit
                    </coar-button>
                    <coar-button
                      variant="ghost"
                      size="sm"
                      iconStart="trash-2"
                      [loading]="deletingId() === role.id"
                      (clicked)="onDelete(role)">
                      Delete
                    </coar-button>
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="4" class="empty-state">
                    <coar-icon name="shield" size="lg" />
                    <p>No roles found</p>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </coar-card>
      }
    </div>
  `,
  styles: `
    .role-list {
      max-width: 1000px;
    }

    .page-header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
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

    .roles-table-card {
      overflow: hidden;
    }

    .roles-table {
      width: 100%;
      border-collapse: collapse;
    }

    .roles-table th,
    .roles-table td {
      padding: 0.75rem 1rem;
      text-align: left;
      border-bottom: 1px solid var(--color-border-primary);
    }

    .roles-table th {
      background: var(--color-surface-secondary);
      font-size: 0.75rem;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--color-text-tertiary);
    }

    .roles-table td {
      font-size: 0.875rem;
      color: var(--color-text-primary);
    }

    .role-name {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      font-weight: 500;
    }

    .role-icon {
      color: var(--color-primary);
    }

    .text-muted {
      color: var(--color-text-tertiary);
    }

    .actions {
      display: flex;
      gap: 0.25rem;
      justify-content: flex-end;
    }

    .empty-state {
      text-align: center;
      padding: 3rem !important;
      color: var(--color-text-tertiary);
    }

    .empty-state coar-icon {
      margin-bottom: 0.5rem;
    }

    .empty-state p {
      margin: 0;
    }
  `,
})
export class RoleListComponent implements OnInit {
  private readonly adminApi = inject(AdminApiService);

  readonly roles = signal<Role[]>([]);
  readonly isLoading = signal(true);
  readonly deletingId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  ngOnInit(): void {
    this.loadRoles();
  }

  private loadRoles(): void {
    this.isLoading.set(true);
    this.error.set(null);

    this.adminApi
      .getRoles()
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load roles.');
          return of({ items: [], totalCount: 0 });
        })
      )
      .subscribe((result) => {
        this.roles.set(result.items);
      });
  }

  onDelete(role: Role): void {
    if (!confirm(`Are you sure you want to delete the role "${role.name}"?`)) {
      return;
    }

    this.deletingId.set(role.id);
    this.error.set(null);
    this.success.set(null);

    this.adminApi
      .deleteRole(role.id)
      .pipe(
        finalize(() => this.deletingId.set(null)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete role.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.success.set('Role deleted successfully.');
          this.loadRoles();
        }
      });
  }

  formatDate(dateStr: string): string {
    const date = new Date(dateStr);
    return date.toLocaleDateString('en-US', {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
    });
  }
}
