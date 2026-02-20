import { Component, inject, signal, OnInit, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import {
  CoarButtonComponent,
  CoarNoteComponent,
  CoarTextInputComponent,
} from '@cocoar/ui';
import { AgGridAngular } from 'ag-grid-angular';
import { CoarGridBuilder, CoarDataGridDirective } from '@cocoar/data-grid';

import { catchError, of, finalize, debounceTime, Subject } from 'rxjs';
import { AdminApiService, PaginationParams, User } from '../../../core';
import { UIService } from '../../../ui';

@Component({
  selector: 'app-user-list',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    FormsModule,
    CoarButtonComponent,
    CoarNoteComponent,
    CoarTextInputComponent,
    AgGridAngular,
    CoarDataGridDirective,
  ],
  templateUrl: './user-list.component.html',
  styleUrl: './user-list.component.css',
})
export class UserListComponent implements OnInit {
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);
  readonly ui = inject(UIService);
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

  readonly usersGrid = CoarGridBuilder.create<User>()
    .columns([
      col => col.field('userName').header('Username').flex(1).sortable(),
      col => col.field('email').header('Email').flex(1).sortable()
        .valueFormatter((params) => {
          if (!params.value) return '-';
          const user = params.data as User;
          const verified = user.emailConfirmed ? '' : ' (Unverified)';
          return params.value + verified;
        }),
      col => col.field('firstName').header('Name').flex(1)
        .valueGetter((params) => {
          const user = params.data as User;
          if (user.firstName || user.lastName) {
            return `${user.firstName || ''} ${user.lastName || ''}`.trim();
          }
          return '-';
        }),
      col => col.field('isActive').header('Status').width(150)
        .valueFormatter((params) => {
          const user = params.data as User;
          if (!user.isActive) {
            return 'Inactive';
          } else if (user.lockoutEnd) {
            return 'Locked';
          }
          const twoFa = user.twoFactorEnabled ? ' (2FA)' : '';
          return 'Active' + twoFa;
        }),
      col => col.field('createdAt').header('Created').width(120)
        .valueFormatter((params) => {
          const value = params.value as string;
          return value ? this.formatDate(value) : '-';
        }),
      col => col.field('id').header('Actions').width(180).sortable(false)
        .valueFormatter(() => '')
        .cellClass('grid-actions-cell'),
    ])
    .rowData(this.users())
    .rowId(params => params.data?.id || '')
    .onRowDoubleClicked(event => {
      if (event.data?.id) {
        this.ui.navigateToModal(event.data.id);
      }
    });

  ngOnInit(): void {
    this.ui.set((ctx) => {
      ctx.header.title = 'Users';
      ctx.header.subTitle = 'Manage user accounts';
      ctx.content.scrollable = false;
    });
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
        // Update grid data
        this.usersGrid.api?.setGridOption('rowData', result.items);
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
