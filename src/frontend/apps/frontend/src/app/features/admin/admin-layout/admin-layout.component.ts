import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { CoarIconComponent } from '@cocoar/ui';

@Component({
  selector: 'app-admin-layout',
  standalone: true,
  imports: [CommonModule, RouterModule, CoarIconComponent],
  template: `
    <div class="admin-layout">
      <aside class="admin-sidebar">
        <h2 class="sidebar-title">Admin Panel</h2>
        <nav class="sidebar-nav">
          <a routerLink="/admin/users" routerLinkActive="active" class="nav-item">
            <coar-icon name="users" size="sm" />
            <span>Users</span>
          </a>
          <a routerLink="/admin/roles" routerLinkActive="active" class="nav-item">
            <coar-icon name="shield" size="sm" />
            <span>Roles</span>
          </a>
        </nav>
        <div class="sidebar-footer">
          <a routerLink="/" class="nav-item">
            <coar-icon name="arrow-left" size="sm" />
            <span>Back to App</span>
          </a>
        </div>
      </aside>
      <main class="admin-content">
        <router-outlet />
      </main>
    </div>
  `,
  styles: `
    .admin-layout {
      display: flex;
      min-height: calc(100vh - 60px);
    }

    .admin-sidebar {
      width: 240px;
      background: var(--color-surface-secondary);
      border-right: 1px solid var(--color-border-primary);
      display: flex;
      flex-direction: column;
      padding: 1.5rem 0;
    }

    .sidebar-title {
      margin: 0 0 1.5rem;
      padding: 0 1.5rem;
      font-size: 1.125rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .sidebar-nav {
      flex: 1;
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
      padding: 0 0.75rem;
    }

    .nav-item {
      display: flex;
      align-items: center;
      gap: 0.75rem;
      padding: 0.75rem;
      border-radius: var(--radius-md);
      text-decoration: none;
      color: var(--color-text-secondary);
      font-size: 0.875rem;
      font-weight: 500;
      transition: all 0.15s ease;
    }

    .nav-item:hover {
      background: var(--color-surface-primary);
      color: var(--color-text-primary);
    }

    .nav-item.active {
      background: var(--color-primary-subtle);
      color: var(--color-primary);
    }

    .sidebar-footer {
      padding: 0 0.75rem;
      border-top: 1px solid var(--color-border-primary);
      margin-top: 1rem;
      padding-top: 1rem;
    }

    .admin-content {
      flex: 1;
      padding: 2rem;
      overflow: auto;
    }
  `,
})
export class AdminLayoutComponent {}
