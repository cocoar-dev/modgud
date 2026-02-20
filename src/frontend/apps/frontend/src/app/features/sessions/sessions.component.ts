import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import {
  CoarButtonComponent,
  CoarCardComponent,
  CoarNoteComponent,
  CoarIconComponent,
  CoarBadgeComponent,
} from '@cocoar/ui';
import { AuthApiService } from '../../core/services/auth-api.service';
import { Session } from '../../core/models/auth.models';
import { catchError, of, finalize } from 'rxjs';

@Component({
  selector: 'app-sessions',
  standalone: true,
  imports: [
    CommonModule,
    CoarButtonComponent,
    CoarCardComponent,
    CoarNoteComponent,
    CoarIconComponent,
    CoarBadgeComponent,
  ],
  template: `
    <div class="sessions">
      <header class="page-header">
        <div>
          <h1 class="page-title">Active Sessions</h1>
          <p class="page-subtitle">Manage your active sessions across devices</p>
        </div>
        <coar-button
          variant="danger"
          iconStart="logout"
          [loading]="isRevokingAll()"
          (clicked)="onRevokeAll()">
          Sign Out Everywhere
        </coar-button>
      </header>

      @if (error()) {
        <coar-note variant="error" padding="s" class="message">
          {{ error() }}
        </coar-note>
      }

      @if (success()) {
        <coar-note variant="success" padding="s" class="message">
          {{ success() }}
        </coar-note>
      }

      @if (isLoading()) {
        <div class="loading">
          <div class="spinner"></div>
          <p>Loading sessions...</p>
        </div>
      } @else if (sessions().length === 0) {
        <coar-note variant="info" padding="m">
          No active sessions found.
        </coar-note>
      } @else {
        <div class="sessions-list">
          @for (session of sessions(); track session.id) {
            <coar-card padding="m" class="session-card" [class.current]="session.isCurrent">
              <div class="session-info">
                <div class="session-device">
                  <coar-icon [name]="getDeviceIcon(session.deviceType)" size="l" class="device-icon" />
                  <div class="device-details">
                    <div class="device-name">
                      {{ session.browser || 'Unknown Browser' }}
                      @if (session.browserVersion) {
                        <span class="version">{{ session.browserVersion }}</span>
                      }
                      @if (session.isCurrent) {
                        <coar-badge variant="success" size="s">Current</coar-badge>
                      }
                    </div>
                    <div class="device-os">
                      {{ session.operatingSystem || 'Unknown OS' }}
                      @if (session.osVersion) {
                        {{ session.osVersion }}
                      }
                    </div>
                  </div>
                </div>
                <div class="session-meta">
                  <div class="meta-item">
                    <coar-icon name="map-pin" size="s" />
                    <span>{{ session.ipAddress || 'Unknown IP' }}</span>
                  </div>
                  <div class="meta-item">
                    <coar-icon name="clock" size="s" />
                    <span>Last active: {{ formatDate(session.lastActiveAt) }}</span>
                  </div>
                  <div class="meta-item">
                    <coar-icon name="calendar" size="s" />
                    <span>Started: {{ formatDate(session.createdAt) }}</span>
                  </div>
                </div>
              </div>
              <div class="session-actions">
                @if (!session.isCurrent) {
                  <coar-button
                    variant="ghost"
                    size="s"
                    [loading]="revokingId() === session.id"
                    (clicked)="onRevokeSession(session.id)">
                    Revoke
                  </coar-button>
                }
              </div>
            </coar-card>
          }
        </div>
      }
    </div>
  `,
  styles: `
    .sessions {
      max-width: 800px;
      margin: 0 auto;
    }

    .page-header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      margin-bottom: 1.5rem;
    }

    .page-title {
      margin: 0 0 0.25rem;
      font-size: 1.5rem;
      font-weight: 600;
      color: var(--color-text-primary);
    }

    .page-subtitle {
      margin: 0;
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .message {
      margin-bottom: 1rem;
    }

    .loading {
      display: flex;
      flex-direction: column;
      align-items: center;
      padding: 3rem;
    }

    .spinner {
      width: 32px;
      height: 32px;
      border: 3px solid var(--color-border-primary);
      border-top-color: var(--color-primary);
      border-radius: 50%;
      animation: spin 1s linear infinite;
    }

    @keyframes spin {
      to {
        transform: rotate(360deg);
      }
    }

    .sessions-list {
      display: flex;
      flex-direction: column;
      gap: 0.75rem;
    }

    .session-card {
      display: flex;
      justify-content: space-between;
      align-items: center;
    }

    .session-card.current {
      border-color: var(--color-success);
    }

    .session-info {
      flex: 1;
    }

    .session-device {
      display: flex;
      align-items: flex-start;
      gap: 0.75rem;
      margin-bottom: 0.75rem;
    }

    .device-icon {
      color: var(--color-text-secondary);
    }

    .device-details {
      flex: 1;
    }

    .device-name {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      font-weight: 500;
      color: var(--color-text-primary);
    }

    .version {
      font-weight: normal;
      color: var(--color-text-tertiary);
    }

    .device-os {
      font-size: 0.875rem;
      color: var(--color-text-secondary);
    }

    .session-meta {
      display: flex;
      flex-wrap: wrap;
      gap: 1rem;
    }

    .meta-item {
      display: flex;
      align-items: center;
      gap: 0.25rem;
      font-size: 0.75rem;
      color: var(--color-text-tertiary);
    }

    .session-actions {
      flex-shrink: 0;
    }
  `,
})
export class SessionsComponent implements OnInit {
  private readonly authApi = inject(AuthApiService);

  readonly sessions = signal<Session[]>([]);
  readonly isLoading = signal(true);
  readonly isRevokingAll = signal(false);
  readonly revokingId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly success = signal<string | null>(null);

  ngOnInit(): void {
    this.loadSessions();
  }

  private loadSessions(): void {
    this.isLoading.set(true);
    this.error.set(null);

    this.authApi
      .getSessions()
      .pipe(
        finalize(() => this.isLoading.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to load sessions.');
          return of({ sessions: [] });
        })
      )
      .subscribe((result) => {
        this.sessions.set(result.sessions);
      });
  }

  onRevokeSession(sessionId: string): void {
    this.revokingId.set(sessionId);
    this.error.set(null);
    this.success.set(null);

    this.authApi
      .revokeSession(sessionId)
      .pipe(
        finalize(() => this.revokingId.set(null)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to revoke session.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          this.sessions.update((sessions) =>
            sessions.filter((s) => s.id !== sessionId)
          );
          this.success.set('Session revoked successfully.');
        }
      });
  }

  onRevokeAll(): void {
    this.isRevokingAll.set(true);
    this.error.set(null);
    this.success.set(null);

    this.authApi
      .revokeAllSessions()
      .pipe(
        finalize(() => this.isRevokingAll.set(false)),
        catchError((err) => {
          this.error.set(err?.error?.message || 'Failed to revoke sessions.');
          return of(null);
        })
      )
      .subscribe((result) => {
        if (result !== null) {
          // This will likely redirect to login as all sessions are revoked
          this.success.set('All sessions revoked. You will be logged out.');
          window.location.href = '/login';
        }
      });
  }

  getDeviceIcon(deviceType?: string): string {
    switch (deviceType?.toLowerCase()) {
      case 'mobile':
        return 'smartphone';
      case 'tablet':
        return 'tablet';
      default:
        return 'monitor';
    }
  }

  formatDate(dateStr: string): string {
    const date = new Date(dateStr);
    return date.toLocaleDateString('en-US', {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
    });
  }
}
