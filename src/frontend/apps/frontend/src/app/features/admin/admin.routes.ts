import { Route } from '@angular/router';

export const adminRoutes: Route[] = [
  {
    path: '',
    loadComponent: () =>
      import('./admin-layout/admin-layout.component').then(
        (m) => m.AdminLayoutComponent
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
    ],
  },
];
