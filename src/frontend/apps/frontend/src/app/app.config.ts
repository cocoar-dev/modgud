import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  APP_INITIALIZER,
  inject,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { appRoutes } from './app.routes';
import { credentialsInterceptor } from './core/interceptors/credentials.interceptor';
import { AuthStateService } from './core/services/auth-state.service';
import {
  provideCoarLocalization,
  provideCoarI18nHttpSource,
  provideCoarL10nHttpSource,
} from '@cocoar/localization';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';

// Register AG Grid modules
ModuleRegistry.registerModules([AllCommunityModule]);

/**
 * Initialize authentication state on app startup.
 */
function initializeAuth(): () => void {
  const authState = inject(AuthStateService);
  return () => authState.initialize();
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(appRoutes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([credentialsInterceptor])),
    {
      provide: APP_INITIALIZER,
      useFactory: initializeAuth,
      multi: true,
    },
    // Core localization system (language management + L10n + i18n)
    provideCoarLocalization({
      defaultLanguage: 'en',
    }),
    // L10n HTTP source for locale data (date/number/currency formatting)
    provideCoarL10nHttpSource(), // Defaults to /locales/{lang}.json
    // i18n HTTP source for translations
    provideCoarI18nHttpSource(), // Defaults to /i18n/{lang}.json
  ],
};
