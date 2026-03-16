import { Route } from '@angular/router';
import { createRouteData, IRoutedFragmentConfig, ComponentRoutedFragment } from '@cocoar/ui-routing';
import { ModalOptions } from '../ui';

type FragmentConfig = IRoutedFragmentConfig<ComponentRoutedFragment<ModalOptions>>;

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
        data: createRouteData<FragmentConfig>({
          routedFragments: [
            {
              type: 'component',
              path: 'create',
              loadComponent: () =>
                import('./users/user-form/user-form.component').then((m) => m.UserFormComponent),
              options: { closeOnBackdropClick: false },
            },
            {
              type: 'component',
              path: ':id',
              loadComponent: () =>
                import('./users/user-form/user-form.component').then((m) => m.UserFormComponent),
              options: { closeOnBackdropClick: false, width: '70%', height: '80%'},
            },
          ],
        }),
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
        path: 'realms',
        loadComponent: () =>
          import('./realms/realm-list/realm-list.component').then(
            (m) => m.RealmListComponent
          ),
        data: createRouteData<FragmentConfig>({
          routedFragments: [
            {
              type: 'component',
              path: 'create',
              loadComponent: () =>
                import('./realms/realm-form/realm-form.component').then((m) => m.RealmFormComponent),
              options: { closeOnBackdropClick: false },
            },
            {
              type: 'component',
              path: ':slug',
              loadComponent: () =>
                import('./realms/realm-form/realm-form.component').then((m) => m.RealmFormComponent),
              options: { closeOnBackdropClick: false },
            },
          ],
        }),
      },
      {
        path: 'realms/create',
        loadComponent: () =>
          import('./realms/realm-form/realm-form.component').then(
            (m) => m.RealmFormComponent
          ),
      },
      {
        path: 'realms/:slug',
        loadComponent: () =>
          import('./realms/realm-form/realm-form.component').then(
            (m) => m.RealmFormComponent
          ),
      },
      {
        path: 'roles',
        loadComponent: () =>
          import('./roles/role-list/role-list.component').then(
            (m) => m.RoleListComponent
          ),
        data: createRouteData<FragmentConfig>({
          routedFragments: [
            {
              type: 'component',
              path: 'create',
              loadComponent: () =>
                import('./roles/role-form/role-form.component').then((m) => m.RoleFormComponent),
              options: { closeOnBackdropClick: false },
            },
            {
              type: 'component',
              path: ':id',
              loadComponent: () =>
                import('./roles/role-form/role-form.component').then((m) => m.RoleFormComponent),
              options: { closeOnBackdropClick: false },
            },
          ],
        }),
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
        data: createRouteData<FragmentConfig>({
          routedFragments: [
            {
              type: 'component',
              path: 'create',
              loadComponent: () =>
                import('./oauth/client-form/client-form.component').then((m) => m.OAuthClientFormComponent),
              options: { closeOnBackdropClick: false },
            },
            {
              type: 'component',
              path: ':id',
              loadComponent: () =>
                import('./oauth/client-form/client-form.component').then((m) => m.OAuthClientFormComponent),
              options: { closeOnBackdropClick: false },
            },
          ],
        }),
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
        data: createRouteData<FragmentConfig>({
          routedFragments: [
            {
              type: 'component',
              path: 'create',
              loadComponent: () =>
                import('./oauth/scope-form/scope-form.component').then((m) => m.OAuthScopeFormComponent),
              options: { closeOnBackdropClick: false },
            },
            {
              type: 'component',
              path: ':id',
              loadComponent: () =>
                import('./oauth/scope-form/scope-form.component').then((m) => m.OAuthScopeFormComponent),
              options: { closeOnBackdropClick: false },
            },
          ],
        }),
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
        data: createRouteData<FragmentConfig>({
          routedFragments: [
            {
              type: 'component',
              path: 'create',
              loadComponent: () =>
                import('./oauth/api-resource-form/api-resource-form.component').then((m) => m.OAuthApiResourceFormComponent),
              options: { closeOnBackdropClick: false },
            },
            {
              type: 'component',
              path: ':id',
              loadComponent: () =>
                import('./oauth/api-resource-form/api-resource-form.component').then((m) => m.OAuthApiResourceFormComponent),
              options: { closeOnBackdropClick: false },
            },
          ],
        }),
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
