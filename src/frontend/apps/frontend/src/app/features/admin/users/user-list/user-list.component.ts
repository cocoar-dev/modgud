import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import {
  CoarButtonComponent,
  CoarCardComponent,
  CoarNoteComponent,
  CoarTextInputComponent,
  CoarBadgeComponent,
  CoarIconComponent,
} from '@cocoar/ui';
import { AdminApiService } from '../../../../core/services/admin-api.service';
import { User, PaginationParams } from '../../../../core/models/auth.models';
import { catchError, of, finalize, debounceTime, Subject } from 'rxjs';

@Component({
  selector: 'app-user-list',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    FormsModule,
    CoarButtonComponent,
    CoarCardComponent,
    CoarNoteComponent,
    CoarTextInputComponent,
    CoarBadgeComponent,
    CoarIconComponent,
  ],
  template: `
    <div class="user-list">
      <header class="page-header">
        <div>
          <h1 class="page-title">Users</h1>
          <p class="page-subtitle">Manage user accounts</p>
        </div>
        <coar-button
          variant="primary"
          iconStart="plus"
          routerLink="/admin/users/create">
          Create User
        </coar-button>
      </header>

      <div class="filters">
        <coar-text-input
          placeholder="Search users..."
          [value]="searchQuery()"
          (valueChange)="onSearch($event)"
          prefix="search" />
      </div>

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
          <p>Loading users...</p>
        </div>
      } @else {
        <coar-card padding="none" class="users-table-card">
          <table class="users-table">
            <thead>
              <tr>
                <th>Username</th>
                <th>Email</th>
                <th>Name</th>
                <th>Status</th>
                <th>Created</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (user of users(); track user.id) {
                <tr>
                  <td class="user-name">
                    <span>{{ user.userName }}</span>
                  </td>
                  <td>
                    <span>{{ user.email || '-' }}</span>
                    @if (user.email && !user.emailConfirmed) {
                      <coar-badge color="warning" size="xs">Unverified</coar-badge>
                    }
                  </td>
                  <td>
                    @if (user.firstName || user.lastName) {
                      {{ user.firstName }} {{ user.lastName }}
                    } @else {
                      <span class="text-muted">-</span>
                    }
                  </td>
                  <td>
                    @if (!user.isActive) {
                      <coar-badge color="error" size="sm">Inactive</coar-badge>
                    } @else if (user.lockoutEnd) {
                      <coar-badge color="warning" size="sm">Locked</coar-badge>
                    } @else {
                      <coar-badge color="success" size="sm">Active</coar-badge>
                    }
                    @if (user.twoFactorEnabled) {
                      <coar-badge color="info" size="sm">2FA</coar-badge>
                    }
                  </td>
                  <td class="text-muted">{{ formatDate(user.createdAt) }}</td>
                  <td class="actions">
                    <coar-button
                      variant="ghost"
                      size="sm"
                      iconStart="edit"
                      [routerLink]="['/admin/users', user.id]">
                      Edit
                    </coar-button>
                    @if (user.lockoutEnd) {
                      <coar-button
                        variant="ghost"
                        size="sm"
                        iconStart="unlock"
                        [loading]="unlockingId() === user.id"
                        (clicked)="onUnlock(user.id)">
                        Unlock
                      </coar-button>
                    }
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="6" class="empty-state">
                    <coar-icon name="users" size="lg" />
                    <p>No users found</p>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </coar-card>

        @if (totalCount() > pageSize) {
          <div class="pagination">
            <coar-button
              variant="ghost"
              size="sm"
              [disabled]="page() === 1"
              (clicked)="onPageChange(page() - 1)">
              Previous
            </coar-button>
            <span class="page-info">
              Page {{ page() }} of {{ totalPages() }}
            </span>
            <coar-button
              variant="ghost"
              size="sm"
              [disabled]="page() >= totalPages()"
              (clicked)="onPageChange(page() + 1)">
              Next
            </coar-button>
          </div>
        }
      }
    </div>
  `,
  styles: `
    .user-list {
      max-width: 1200px;
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

    .filters {
      margin-bottom: 1rem;
      max-width: 320px;
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

    .users-table-card {
      overflow: hidden;
    }

    .users-table {
      width: 100%;
      border-collapse: collapse;
    }

    .users-table th,
    .users-table td {
      padding: 0.75rem 1rem;
      text-align: left;
      border-bottom: 1px solid var(--color-border-primary);
    }

    .users-table th {
      background: var(--color-surface-secondary);
      font-size: 0.75rem;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--color-text-tertiary);
    }

    .users-table td {
      font-size: 0.875rem;
      color: var(--color-text-primary);
    }

    .user-name {
      font-weight: 500;
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

    .pagination {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 1rem;
      margin-top: 1rem;
    }

    .page-info {
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }
  `,
})
export class UserListComponent implements OnInit {
  private readonly adminApi = inject(AdminApiService);
  private readonly searchSubject = new Subject<string>();

  readonly users = signal<User[]>([]);
  readonly totalCount = signal(0);
  readonly page = signal(1);
  readonly pageSize = 20;
  readonly searchQuery = signal('');

  readonly isLoading = signal(true);
  readonly unlockingId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly totalPages = () => Math.ceil(this.totalCount() / this.pageSize);

  ngOnInit(): void {
    this.loadUsers();

    this.searchSubject.pipe(debounceTime(300)).subscribe((query) => {
      this.searchQuery.set(query);
      this.page.set(1);
      this.loadUsers();
    });
  }

  private loadUsers(): void {
    this.isLoading.set(true);
    this.error.set(null);

    const params: PaginationParams = {
      page: this.page(),
      pageSize: this.pageSize,
      search: this.searchQuery() || undefined,
    };

    this.adminApi
      .getUsers(params)
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load users.');
          return of({ items: [], totalCount: 0, page: 1, pageSize: this.pageSize });
        })
      )
      .subscribe((result) => {
        this.users.set(result.items);
        this.totalCount.set(result.totalCount);
      });
  }

  onSearch(query: string): void {
    this.searchSubject.next(query);
  }

  onPageChange(newPage: number): void {
    this.page.set(newPage);
    this.loadUsers();
  }

  onUnlock(userId: string): void {
    this.unlockingId.set(userId);
    this.error.set(null);
    this.success.set(null);

    this.adminApi
      .unlockUser(userId)
      .pipe(
        finalize(() => this.unlockingId.set(null)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to unlock user.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.success.set('User unlocked successfully.');
          this.loadUsers();
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
