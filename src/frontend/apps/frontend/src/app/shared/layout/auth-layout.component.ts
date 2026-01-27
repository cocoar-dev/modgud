import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { CoarIconComponent } from '@cocoar/ui';

@Component({
  selector: 'app-auth-layout',
  standalone: true,
  imports: [CommonModule, RouterModule, CoarIconComponent],
  template: `
    <div class="auth-layout">
      <header class="auth-header">
        <a routerLink="/" class="logo">
          <coar-icon name="shield" size="lg" />
          <span class="logo-text">Cocoar Auth</span>
        </a>
      </header>

      <main class="auth-main">
        <router-outlet />
      </main>

      <footer class="auth-footer">
        <p>&copy; 2026 Cocoar Auth. All rights reserved.</p>
      </footer>
    </div>
  `,
  styles: `
    .auth-layout {
      min-height: 100vh;
      display: flex;
      flex-direction: column;
      background: var(--color-surface-secondary);
    }

    .auth-header {
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 1.5rem;
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

    .auth-main {
      flex: 1;
      display: flex;
      align-items: center;
      justify-content: center;
    }

    .auth-footer {
      padding: 1rem;
      text-align: center;
    }

    .auth-footer p {
      margin: 0;
      font-size: 0.75rem;
      color: var(--color-text-tertiary);
    }
  `,
})
export class AuthLayoutComponent {}
