import { Route } from '@angular/router';

export const adminRoutes: Route[] = [
  {
    path: '',
    loadComponent: () =>
      import('./admin.component').then(
        (m) => m.AdminComponent
      ),
    children: [
      {
        path: '',
        redirectTo: 'users',
        pathMatch: 'full',
      },
      {
        path: 'users',
        loadComponent: () =>
          import('./users/user-list/user-list.component').then(
            (m) => m.UserListComponent
          ),
      },
      {
        path: 'users/create',
        loadComponent: () =>
          import('./users/user-form/user-form.component').then(
            (m) => m.UserFormComponent
          ),
      },
      {
        path: 'users/:id',
        loadComponent: () =>
          import('./users/user-form/user-form.component').then(
            (m) => m.UserFormComponent
          ),
      },
      {
        path: 'roles',
        loadComponent: () =>
          import('./roles/role-list/role-list.component').then(
            (m) => m.RoleListComponent
          ),
      },
      {
        path: 'roles/create',
        loadComponent: () =>
          import('./roles/role-form/role-form.component').then(
            (m) => m.RoleFormComponent
          ),
      },
      {
        path: 'roles/:id',
        loadComponent: () =>
          import('./roles/role-form/role-form.component').then(
            (m) => m.RoleFormComponent
          ),
      },
      {
        path: 'oauth/clients',
        loadComponent: () =>
          import('./oauth/client-list/client-list.component').then(
            (m) => m.OAuthClientListComponent
          ),
      },
      {
        path: 'oauth/clients/create',
        loadComponent: () =>
          import('./oauth/client-form/client-form.component').then(
            (m) => m.OAuthClientFormComponent
          ),
      },
      {
        path: 'oauth/clients/:id',
        loadComponent: () =>
          import('./oauth/client-form/client-form.component').then(
            (m) => m.OAuthClientFormComponent
          ),
      },
      {
        path: 'oauth/scopes',
        loadComponent: () =>
          import('./oauth/scope-list/scope-list.component').then(
            (m) => m.OAuthScopeListComponent
          ),
      },
      {
        path: 'oauth/scopes/create',
        loadComponent: () =>
          import('./oauth/scope-form/scope-form.component').then(
            (m) => m.OAuthScopeFormComponent
          ),
      },
      {
        path: 'oauth/scopes/:id',
        loadComponent: () =>
          import('./oauth/scope-form/scope-form.component').then(
            (m) => m.OAuthScopeFormComponent
          ),
      },
      {
        path: 'oauth/api-resources',
        loadComponent: () =>
          import('./oauth/api-resource-list/api-resource-list.component').then(
            (m) => m.OAuthApiResourceListComponent
          ),
      },
      {
        path: 'oauth/api-resources/create',
        loadComponent: () =>
          import('./oauth/api-resource-form/api-resource-form.component').then(
            (m) => m.OAuthApiResourceFormComponent
          ),
      },
      {
        path: 'oauth/api-resources/:id',
        loadComponent: () =>
          import('./oauth/api-resource-form/api-resource-form.component').then(
            (m) => m.OAuthApiResourceFormComponent
          ),
      },
    ],
  },
];
