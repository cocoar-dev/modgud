import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import {
  CoarButtonComponent,
  CoarNoteComponent,
  CoarBadgeComponent,
} from '@cocoar/ui';
import { AgGridAngular } from 'ag-grid-angular';
import { CoarGridBuilder, CoarDataGridDirective } from '@cocoar/data-grid';

import { catchError, of, finalize } from 'rxjs';
import { AdminApiService, OAuthScope, isStandardScope } from '../../../core';
import { UIService } from '../../../ui';

@Component({
  selector: 'app-oauth-scope-list',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    CoarButtonComponent,
    CoarNoteComponent,
    CoarBadgeComponent,
    AgGridAngular,
    CoarDataGridDirective,
  ],
  templateUrl: './scope-list.component.html',
  styleUrl: './scope-list.component.css',
})
export class OAuthScopeListComponent implements OnInit {
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);
  readonly ui = inject(UIService);

  readonly scopes = signal<OAuthScope[]>([]);
  readonly isLoading = signal(true);
  readonly deletingId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly scopesGrid = CoarGridBuilder.create<OAuthScope>()
    .columns([
      col => col.field('name').header('Name').flex(1).sortable(),
      col => col.field('displayName').header('Display Name').flex(1)
        .valueFormatter((params) => (params.value as string) || '-'),
      col => col.field('description').header('Description').flex(2)
        .valueFormatter((params) => (params.value as string) || '-'),
      col => col.field('resources').header('Resources').flex(1)
        .valueFormatter((params) => {
          const resources = params.value as string[];
          return resources?.length > 0 ? resources.join(', ') : '-';
        }),
      col => col.field('id').header('Actions').width(200).sortable(false)
        .valueFormatter(() => '')
        .cellClass('grid-actions-cell'),
    ])
    .rowData(this.scopes())
    .rowId(params => params.data?.id || '')
    .onRowDoubleClicked(event => {
      if (event.data?.id && !this.isStandardScope(event.data)) {
        this.ui.navigateToModal(event.data.id);
      }
    });

  ngOnInit(): void {
    this.ui.set((ctx) => {
      ctx.header.title = 'OAuth Scopes';
      ctx.header.subTitle = 'Manage OAuth 2.0 / OpenID Connect scopes';
      ctx.content.scrollable = false;
    });
    this.loadScopes();
  }

  private loadScopes(): void {
    this.isLoading.set(true);
    this.error.set(null);

    this.adminApi
      .getOAuthScopes()
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load OAuth scopes.');
          return of({ items: [], totalCount: 0 });
        })
      )
      .subscribe((result) => {
        this.scopes.set(result.items);
        this.scopesGrid.api?.setGridOption('rowData', result.items);
      });
  }

  isStandardScope(scope: OAuthScope): boolean {
    return isStandardScope(scope.name);
  }

  onDelete(scope: OAuthScope): void {
    if (this.isStandardScope(scope)) {
      this.error.set('Standard scopes cannot be deleted.');
      return;
    }

    if (!confirm(`Are you sure you want to delete the scope "${scope.name}"?`)) {
      return;
    }

    this.deletingId.set(scope.id);
    this.error.set(null);
    this.success.set(null);

    this.adminApi
      .deleteOAuthScope(scope.id)
      .pipe(
        finalize(() => this.deletingId.set(null)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete scope.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.success.set('Scope deleted successfully.');
          this.loadScopes();
        }
      });
  }
}
