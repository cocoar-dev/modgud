import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import {
  CoarButtonComponent,
  CoarNoteComponent,
} from '@cocoar/ui';
import { AgGridAngular } from 'ag-grid-angular';
import { CoarGridBuilder, CoarDataGridDirective } from '@cocoar/data-grid';

import { catchError, of, finalize } from 'rxjs';
import { AdminApiService, Role } from '../../../core';
import { UIService } from '../../../ui';

@Component({
  selector: 'app-role-list',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    CoarButtonComponent,
    CoarNoteComponent,
    AgGridAngular,
    CoarDataGridDirective,
  ],
  templateUrl: './role-list.component.html',
  styleUrl: './role-list.component.css',
})
export class RoleListComponent implements OnInit {
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);
  readonly ui = inject(UIService);

  readonly roles = signal<Role[]>([]);
  readonly isLoading = signal(true);
  readonly deletingId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly rolesGrid = CoarGridBuilder.create<Role>()
    .columns([
      col => col.field('name').header('Name').flex(1).sortable(),
      col => col.field('description').header('Description').flex(2)
        .valueFormatter((params) => (params.value as string) || '-'),
      col => col.field('createdAt').header('Created').width(120)
        .valueFormatter((params) => {
          const value = params.value as string;
          return value ? this.formatDate(value) : '-';
        }),
      col => col.field('id').header('Actions').width(200).sortable(false)
        .valueFormatter(() => '')
        .cellClass('grid-actions-cell'),
    ])
    .rowData(this.roles())
    .rowId(params => params.data?.id || '')
    .onRowDoubleClicked(event => {
      if (event.data?.id) {
        this.ui.navigateToModal(event.data.id);
      }
    });

  ngOnInit(): void {
    this.ui.set((ctx) => {
      ctx.header.title = 'Roles';
      ctx.header.subTitle = 'Manage user roles';
      ctx.content.scrollable = false;
    });
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
        // Update grid data
        this.rolesGrid.api?.setGridOption('rowData', result.items);
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
