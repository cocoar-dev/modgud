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
import { AdminApiService, OAuthClient } from '../../../core';

@Component({
  selector: 'app-oauth-client-list',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    CoarButtonComponent,
    CoarNoteComponent,
    AgGridAngular,
    CoarDataGridDirective,
  ],
  templateUrl: './client-list.component.html',
  styleUrl: './client-list.component.css',
})
export class OAuthClientListComponent implements OnInit {
  private readonly adminApi = inject(AdminApiService);
  private readonly router = inject(Router);

  readonly clients = signal<OAuthClient[]>([]);
  readonly isLoading = signal(true);
  readonly deletingId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly clientsGrid = CoarGridBuilder.create<OAuthClient>()
    .columns([
      col => col.field('clientId').header('Client ID').flex(1).sortable(),
      col => col.field('displayName').header('Display Name').flex(1)
        .valueFormatter((params) => (params.value as string) || '-'),
      col => col.field('clientType').header('Type').width(120)
        .valueFormatter((params) => {
          const value = params.value as string;
          return value === 'confidential' ? 'Confidential' : 'Public';
        }),
      col => col.field('consentType').header('Consent').width(100)
        .valueFormatter((params) => {
          const value = params.value as string;
          return value.charAt(0).toUpperCase() + value.slice(1);
        }),
      col => col.field('redirectUris').header('Redirect URIs').flex(1)
        .valueFormatter((params) => {
          const uris = params.value as string[];
          return uris?.length > 0 ? uris.join(', ') : '-';
        }),
      col => col.field('id').header('Actions').width(200).sortable(false)
        .valueFormatter(() => '')
        .cellClass('grid-actions-cell'),
    ])
    .rowData(this.clients())
    .rowId(params => params.data?.id || '')
    .onRowDoubleClicked(event => {
      if (event.data?.id) {
        this.router.navigate(['/admin/oauth/clients', event.data.id]);
      }
    });

  ngOnInit(): void {
    this.loadClients();
  }

  private loadClients(): void {
    this.isLoading.set(true);
    this.error.set(null);

    this.adminApi
      .getOAuthClients()
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load OAuth clients.');
          return of({ items: [], totalCount: 0 });
        })
      )
      .subscribe((result) => {
        this.clients.set(result.items);
        this.clientsGrid.api?.setGridOption('rowData', result.items);
      });
  }

  onDelete(client: OAuthClient): void {
    if (!confirm(`Are you sure you want to delete the OAuth client "${client.clientId}"?`)) {
      return;
    }

    this.deletingId.set(client.id);
    this.error.set(null);
    this.success.set(null);

    this.adminApi
      .deleteOAuthClient(client.id)
      .pipe(
        finalize(() => this.deletingId.set(null)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete OAuth client.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.success.set('OAuth client deleted successfully.');
          this.loadClients();
        }
      });
  }
}
