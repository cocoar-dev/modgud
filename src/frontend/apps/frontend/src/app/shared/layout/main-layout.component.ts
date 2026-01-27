import { Component, inject, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import { AuthStateService } from '../../core/services/auth-state.service';
import { CoarButtonComponent, CoarIconComponent } from '@cocoar/ui';

@Component({
  selector: 'app-main-layout',
  standalone: true,
  imports: [CommonModule, RouterModule, CoarButtonComponent, CoarIconComponent],
  template: `
    <div class="layout">
      <header class="header">
        <div class="header-left">
          <a routerLink="/" class="logo">
            <coar-icon name="shield" size="lg" />
            <span class="logo-text">Cocoar Auth</span>
          </a>
        </div>

        @if (isAuthenticated()) {
          <nav class="nav">
            <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{exact: true}" class="nav-link">
              Home
            </a>
            <a routerLink="/profile" routerLinkActive="active" class="nav-link">
              Profile
            </a>
            <a routerLink="/sessions" routerLinkActive="active" class="nav-link">
              Sessions
            </a>
            <a routerLink="/privacy" routerLinkActive="active" class="nav-link">
              Privacy
            </a>
            @if (isAdmin()) {
              <a routerLink="/admin" routerLinkActive="active" class="nav-link nav-link--admin">
                Admin
              </a>
            }
          </nav>
        }

        <div class="header-right">
          @if (isAuthenticated()) {
            <div class="user-info">
              <span class="user-name">{{ displayName() }}</span>
              <coar-button
                variant="ghost"
                size="sm"
                iconStart="logout"
                (clicked)="onLogout()">
                Logout
              </coar-button>
            </div>
          } @else {
            <coar-button
              variant="primary"
              size="sm"
              routerLink="/login">
              Login
            </coar-button>
          }
        </div>
      </header>

      <main class="main">
        <router-outlet />
      </main>

      <footer class="footer">
        <p>&copy; 2026 Cocoar Auth. All rights reserved.</p>
      </footer>
    </div>
  `,
  styles: `
    .layout {
      min-height: 100vh;
      display: flex;
      flex-direction: column;
    }

    .header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0 1.5rem;
      height: 60px;
      background: var(--color-surface-primary);
      border-bottom: 1px solid var(--color-border-primary);
    }

    .header-left {
      display: flex;
      align-items: center;
    }

    .logo {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      text-decoration: none;
      color: var(--color-text-primary);
    }

    .logo-text {
      font-size: 1.25rem;
      font-weight: 600;
    }

    .nav {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }

    .nav-link {
      padding: 0.5rem 1rem;
      border-radius: var(--radius-md);
      text-decoration: none;
      color: var(--color-text-secondary);
      font-size: 0.875rem;
      font-weight: 500;
      transition: all 0.15s ease;
    }

    .nav-link:hover {
      background: var(--color-surface-secondary);
      color: var(--color-text-primary);
    }

    .nav-link.active {
      background: var(--color-primary-subtle);
      color: var(--color-primary);
    }

    .nav-link--admin {
      color: var(--color-accent);
    }

    .nav-link--admin.active {
      background: var(--color-accent-subtle);
      color: var(--color-accent);
    }

    .header-right {
      display: flex;
      align-items: center;
    }

    .user-info {
      display: flex;
      align-items: center;
      gap: 0.75rem;
    }

    .user-name {
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .main {
      flex: 1;
      padding: 2rem;
      max-width: 1200px;
      width: 100%;
      margin: 0 auto;
    }

    .footer {
      padding: 1rem;
      text-align: center;
      border-top: 1px solid var(--color-border-primary);
      background: var(--color-surface-primary);
    }

    .footer p {
      margin: 0;
      font-size: 0.875rem;
      color: var(--color-text-tertiary);
    }
  `,
})
export class MainLayoutComponent {
  private readonly authState = inject(AuthStateService);
  private readonly router = inject(Router);

  readonly isAuthenticated = this.authState.isAuthenticated;
  readonly isAdmin = this.authState.isAdmin;
  readonly currentUser = this.authState.currentUser;

  readonly displayName = computed(() => {
    const user = this.currentUser();
    if (!user) return '';
    if (user.firstName && user.lastName) {
      return `${user.firstName} ${user.lastName}`;
    }
    return user.userName;
  });

  onLogout(): void {
    this.authState.logout('/login');
  }
}
