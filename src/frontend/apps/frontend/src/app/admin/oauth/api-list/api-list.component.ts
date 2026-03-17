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
import { AdminApiService, OAuthApi } from '../../../core';
import { UIService } from '../../../ui';

@Component({
  selector: 'app-oauth-api-list',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    CoarButtonComponent,
    CoarNoteComponent,
    AgGridAngular,
    CoarDataGridDirective,
  ],
  templateUrl: './api-list.component.html',
  styleUrl: './api-list.component.css',
})
export class OAuthApiListComponent implements OnInit {
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);
  readonly ui = inject(UIService);

  readonly apis = signal<OAuthApi[]>([]);
  readonly isLoading = signal(true);
  readonly deletingId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly apisGrid = CoarGridBuilder.create<OAuthApi>()
    .columns([
      col => col.field('name').header('Name').flex(1).sortable(),
      col => col.field('displayName').header('Display Name').flex(1)
        .valueFormatter((params) => (params.value as string) || '-'),
      col => col.field('enabled').header('Status').width(100)
        .valueFormatter((params) => params.value ? 'Enabled' : 'Disabled'),
      col => col.field('scopes').header('Scopes').flex(1)
        .valueFormatter((params) => {
          const scopes = params.value as string[];
          return scopes?.length > 0 ? scopes.join(', ') : '-';
        }),
      col => col.field('userClaims').header('User Claims').flex(1)
        .valueFormatter((params) => {
          const claims = params.value as string[];
          return claims?.length > 0 ? claims.join(', ') : '-';
        }),
      col => col.field('id').header('Actions').width(200).sortable(false)
        .valueFormatter(() => '')
        .cellClass('grid-actions-cell'),
    ])
    .rowData(this.apis())
    .rowId(params => params.data?.id || '')
    .onRowDoubleClicked(event => {
      if (event.data?.id) {
        this.ui.navigateToModal(event.data.id);
      }
    });

  ngOnInit(): void {
    this.ui.set((ctx) => {
      ctx.header.title = 'APIs';
      ctx.header.subTitle = 'Manage APIs for reference token introspection';
      ctx.content.scrollable = false;
    });
    this.loadApis();
  }

  private loadApis(): void {
    this.isLoading.set(true);
    this.error.set(null);

    this.adminApi
      .getOAuthApis()
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load APIs.');
          return of({ items: [], totalCount: 0 });
        })
      )
      .subscribe((result) => {
        this.apis.set(result.items);
        this.apisGrid.api?.setGridOption('rowData', result.items);
      });
  }

  onDelete(api: OAuthApi): void {
    if (!confirm(`Are you sure you want to delete the API "${api.name}"?`)) {
      return;
    }

    this.deletingId.set(api.id);
    this.error.set(null);
    this.success.set(null);

    this.adminApi
      .deleteOAuthApi(api.id)
      .pipe(
        finalize(() => this.deletingId.set(null)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete API.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.success.set('API deleted successfully.');
          this.loadApis();
        }
      });
  }
}
