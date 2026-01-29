import { Component, inject, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import { AuthStateService } from '../core/services/auth-state.service';
import { CoarMenuComponent, CoarMenuHeadingComponent, CoarMenuItemComponent, CoarSidebarComponent } from '@cocoar/ui';
import { CoarI18nPipe, CoarLocalizationService } from '@cocoar/localization';

@Component({
  selector: 'app-main-layout',
  standalone: true,
  imports: [CommonModule, RouterModule,
    CoarSidebarComponent,
    CoarMenuComponent,
    CoarMenuItemComponent,
    CoarMenuHeadingComponent,
    CoarI18nPipe,
  ],
  templateUrl: './main.component.html',
  styleUrl: './main.component.css',
})
export class MainLayoutComponent {
  private readonly authState = inject(AuthStateService);
  private readonly router = inject(Router);

  readonly locale = inject(CoarLocalizationService);

  readonly isAuthenticated = this.authState.isAuthenticated;
  readonly isAdmin = this.authState.isAdmin;
  readonly currentUser = this.authState.currentUser;

  toggleLanguage(): void {
    const currentLang = this.locale.languageState.value;
    const newLang = currentLang === 'de' ? 'en' : 'de';
    this.locale.setLanguage(newLang);
  }

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
