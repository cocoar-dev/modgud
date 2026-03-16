import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import {
  CoarButtonComponent,
  CoarNoteComponent,
} from '@cocoar/ui';
import { AgGridAngular } from 'ag-grid-angular';
import { CoarGridBuilder, CoarDataGridDirective } from '@cocoar/data-grid';

import { catchError, of, finalize } from 'rxjs';
import { AdminApiService, Realm } from '../../../core';
import { UIService } from '../../../ui';

@Component({
  selector: 'app-realm-list',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    CoarButtonComponent,
    CoarNoteComponent,
    AgGridAngular,
    CoarDataGridDirective,
  ],
  templateUrl: './realm-list.component.html',
  styleUrl: './realm-list.component.css',
})
export class RealmListComponent implements OnInit {
  private readonly adminApi = inject(AdminApiService);
  readonly ui = inject(UIService);

  readonly realms = signal<Realm[]>([]);
  readonly isLoading = signal(true);
  readonly deletingSlug = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  readonly realmsGrid = CoarGridBuilder.create<Realm>()
    .columns([
      col => col.field('slug').header('Slug').flex(1).sortable(),
      col => col.field('displayName').header('Display Name').flex(2).sortable(),
      col => col.field('isActive').header('Active').width(100)
        .valueFormatter((params) => (params.value as boolean) ? 'Yes' : 'No'),
      col => col.field('needsSetup').header('Needs Setup').width(120)
        .valueFormatter((params) => (params.value as boolean) ? 'Yes' : 'No'),
      col => col.field('createdAt').header('Created').width(120)
        .valueFormatter((params) => {
          const value = params.value as string;
          return value ? this.formatDate(value) : '-';
        }),
      col => col.field('slug').header('Actions').width(200).sortable(false)
        .valueFormatter(() => '')
        .cellClass('grid-actions-cell'),
    ])
    .rowData(this.realms())
    .rowId(params => params.data?.slug || '')
    .onRowDoubleClicked(event => {
      if (event.data?.slug) {
        this.ui.navigateToModal(event.data.slug);
      }
    });

  ngOnInit(): void {
    this.ui.set((ctx) => {
      ctx.header.title = 'Realms';
      ctx.header.subTitle = 'Manage identity realms';
      ctx.content.scrollable = false;
    });
    this.loadRealms();
  }

  private loadRealms(): void {
    this.isLoading.set(true);
    this.error.set(null);

    this.adminApi
      .getRealms()
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load realms.');
          return of({ items: [], totalCount: 0 });
        })
      )
      .subscribe((result) => {
        this.realms.set(result.items);
        this.realmsGrid.api?.setGridOption('rowData', result.items);
      });
  }

  onDelete(realm: Realm): void {
    if (realm.isSystem) return;
    if (!confirm(`Are you sure you want to delete the realm "${realm.displayName}"?`)) {
      return;
    }

    this.deletingSlug.set(realm.slug);
    this.error.set(null);
    this.success.set(null);

    this.adminApi
      .deleteRealm(realm.slug)
      .pipe(
        finalize(() => this.deletingSlug.set(null)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to delete realm.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.success.set('Realm deleted successfully.');
          this.loadRealms();
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
