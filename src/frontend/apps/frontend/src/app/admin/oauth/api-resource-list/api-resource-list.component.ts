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
import { AdminApiService, OAuthApiResource } from '../../../core';
import { UIService } from '../../../ui';

@Component({
  selector: 'app-oauth-api-resource-list',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    CoarButtonComponent,
    CoarNoteComponent,
    AgGridAngular,
    CoarDataGridDirective,
  ],
  templateUrl: './api-resource-list.component.html',
  styleUrl: './api-resource-list.component.css',
})
export class OAuthApiResourceListComponent implements OnInit {
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);
  readonly ui = inject(UIService);

  readonly apiResources = signal<OAuthApiResource[]>([]);
  readonly isLoading = signal(true);
  readonly deletingId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly apiResourcesGrid = CoarGridBuilder.create<OAuthApiResource>()
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
    .rowData(this.apiResources())
    .rowId(params => params.data?.id || '')
    .onRowDoubleClicked(event => {
      if (event.data?.id) {
        this.ui.navigateToModal(event.data.id);
      }
    });

  ngOnInit(): void {
    this.ui.set((ctx) => {
      ctx.header.title = 'API Resources';
      ctx.header.subTitle = 'Manage API resources for reference token introspection';
      ctx.content.scrollable = false;
    });
    this.loadApiResources();
  }

  private loadApiResources(): void {
    this.isLoading.set(true);
    this.error.set(null);

    this.adminApi
      .getOAuthApiResources()
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load API resources.');
          return of({ items: [], totalCount: 0 });
        })
      )
      .subscribe((result) => {
        this.apiResources.set(result.items);
        this.apiResourcesGrid.api?.setGridOption('rowData', result.items);
      });
  }

  onDelete(apiResource: OAuthApiResource): void {
    if (!confirm(`Are you sure you want to delete the API resource "${apiResource.name}"?`)) {
      return;
    }

    this.deletingId.set(apiResource.id);
    this.error.set(null);
    this.success.set(null);

    this.adminApi
      .deleteOAuthApiResource(apiResource.id)
      .pipe(
        finalize(() => this.deletingId.set(null)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete API resource.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.success.set('API resource deleted successfully.');
          this.loadApiResources();
        }
      });
  }
}
