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

import { catchError, of, finalize, debounceTime, Subject, map } from 'rxjs';
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

  readonly users$ = this.adminApi.getUsers();
  readonly users = this.users$.pipe(
    map(result => result.items),
  )

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
      col => col.field('userName').header('Username').sortable().pinned("left").lockPosition("left").cellClass('tw:font-semibold'),

      col => col.field('firstName').header('Firstname'),
      col => col.field('lastName').header('Lastname'),
      col => col.field('email').header('Email').sortable().cellStyle(params => ({
        opacity: params.data?.emailConfirmed ? '1' : '0.5',
      })),
      col => col.field('isActive').header('Status').flex(1)
        .valueGetter((params) => {
          const user = params.data as User;
          if (!user.isActive) {
            return 'Inactive';
          } else if (user.lockoutEnd) {
            return 'Locked';
          }
          const twoFa = user.twoFactorEnabled ? ' (2FA)' : '';
          return 'Active' + twoFa;
        }),
      col => col.date('createdAt').header('Created').width(120),
    ])
    .rowData$(this.users)
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
  }

  onSearch(query: string): void {
    this.searchSubject.next(query);
  }

  onPageChange(newPage: number): void {
    this.page.set(newPage);
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
        }
      });
  }

}
