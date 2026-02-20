import { Component, inject, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import {
  CoarCardComponent,
  CoarIconComponent,
} from '@cocoar/ui';
import { AuthStateService } from '../../core/services/auth-state.service';

@Component({
  selector: 'app-home',
  standalone: true,
  imports: [CommonModule, RouterModule, CoarCardComponent, CoarIconComponent],
  template: `
    <div class="home">
      <header class="home-header">
        <h1>Welcome, {{ displayName() }}</h1>
        <p class="subtitle">Manage your account and security settings</p>
      </header>

      <div class="card-grid">
        <coar-card elevated padding="l" class="feature-card">
          <a routerLink="/profile" class="card-link">
            <coar-icon name="user" size="l" class="card-icon" />
            <h2>Profile</h2>
            <p>View and edit your personal information</p>
          </a>
        </coar-card>

        <coar-card elevated padding="l" class="feature-card">
          <a routerLink="/sessions" class="card-link">
            <coar-icon name="monitor" size="l" class="card-icon" />
            <h2>Sessions</h2>
            <p>Manage your active sessions and devices</p>
          </a>
        </coar-card>

        <coar-card elevated padding="l" class="feature-card">
          <a routerLink="/privacy" class="card-link">
            <coar-icon name="shield" size="l" class="card-icon" />
            <h2>Privacy</h2>
            <p>Export your data or manage account deletion</p>
          </a>
        </coar-card>

        @if (isAdmin()) {
          <coar-card elevated padding="l" class="feature-card admin-card">
            <a routerLink="/admin" class="card-link">
              <coar-icon name="settings" size="l" class="card-icon" />
              <h2>Admin Panel</h2>
              <p>Manage users and roles</p>
            </a>
          </coar-card>
        }
      </div>

      <section class="info-section">
        <h2>Account Information</h2>
        <div class="info-grid">
          <div class="info-item">
            <span class="info-label">Username</span>
            <span class="info-value">{{ currentUser()?.userName }}</span>
          </div>
          <div class="info-item">
            <span class="info-label">Email</span>
            <span class="info-value">{{ currentUser()?.email || 'Not set' }}</span>
          </div>
          <div class="info-item">
            <span class="info-label">Roles</span>
            <span class="info-value">{{ currentUser()?.roles?.join(', ') || 'None' }}</span>
          </div>
        </div>
      </section>
    </div>
  `,
  styles: `
    .home {
      max-width: 900px;
      margin: 0 auto;
    }

    .home-header {
      margin-bottom: 2rem;
    }

    .home-header h1 {
      margin: 0 0 0.5rem;
      font-size: 1.75rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .subtitle {
      margin: 0;
      font-size: 1rem;
      color: var(--color-text-secondary);
    }

    .card-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
      gap: 1.5rem;
      margin-bottom: 2rem;
    }

    .feature-card {
      transition: transform 0.15s ease, box-shadow 0.15s ease;
    }

    .feature-card:hover {
      transform: translateY(-2px);
    }

    .card-link {
      display: block;
      text-decoration: none;
      color: inherit;
    }

    .card-icon {
      color: var(--color-primary);
      margin-bottom: 0.75rem;
    }

    .admin-card .card-icon {
      color: var(--color-accent);
    }

    .feature-card h2 {
      margin: 0 0 0.5rem;
      font-size: 1.125rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .feature-card p {
      margin: 0;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .info-section {
      background: var(--color-surface-secondary);
      border-radius: var(--radius-lg);
      padding: 1.5rem;
    }

    .info-section h2 {
      margin: 0 0 1rem;
      font-size: 1.125rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .info-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 1rem;
    }

    .info-item {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
    }

    .info-label {
      font-size: 0.75rem;
      font-weight: 500;
      color: var(--color-text-tertiary);
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }

    .info-value {
      font-size: 0.875rem;
      color: var(--color-text-primary);
    }
  `,
})
export class HomeComponent {
  private readonly authState = inject(AuthStateService);

  readonly currentUser = this.authState.currentUser;
  readonly isAdmin = this.authState.isAdmin;

  readonly displayName = computed(() => {
    const user = this.currentUser();
    if (!user) return '';
    if (user.firstName) {
      return user.firstName;
    }
    return user.userName;
  });
}
