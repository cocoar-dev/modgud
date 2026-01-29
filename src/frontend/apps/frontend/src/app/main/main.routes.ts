import { Route } from '@angular/router';

/**
 * Routes for the authenticated main application area.
 * These routes are lazy-loaded and only accessible to authenticated users.
 */
export const mainRoutes: Route[] = [
  {
    path: '',
    loadComponent: () =>
      import('./main.component').then(
        (m) => m.MainLayoutComponent
      ),
    children: [
      {
        path: '',
        pathMatch: 'full',
        loadComponent: () =>
          import('../features/home/home.component').then((m) => m.HomeComponent),
      },
      {
        path: 'profile',
        loadComponent: () =>
          import('../features/profile/profile.component').then(
            (m) => m.ProfileComponent
          ),
      },
      {
        path: 'sessions',
        loadComponent: () =>
          import('../features/sessions/sessions.component').then(
            (m) => m.SessionsComponent
          ),
      },
      {
        path: 'privacy',
        loadComponent: () =>
          import('../features/privacy/privacy.component').then(
            (m) => m.PrivacyComponent
          ),
      },
    ],
  },
];
