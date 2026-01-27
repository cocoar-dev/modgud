---

id: ng-routing-edge-router
title: Edge Router for Authenticated Angular Applications
status: stable
scope: angular-routing
tags: [angular, router, authentication, best-practice]
appliesTo: ["Angular >= 15"]
----------------------------

# Edge Router for Authenticated Angular Applications

## TL;DR

If an Angular application requires authentication, **always use an Edge Router at the root level**:

* Public routes (e.g. `/login`) are declared explicitly
* The authenticated application shell is **lazy-loaded**
* Access to the main application is guarded via `canMatch`

This creates a clear, predictable routing structure and prevents accidental loading of authenticated application code for unauthenticated users.

---

## The Problem

Angular routing can be implemented in many different ways:

* Guards on every route
* Guards on components
* Mixing public and private routes inside lazy modules

All of these *work*, but they quickly lead to:

* Inconsistent mental models between projects
* Accidental eager loading of protected code
* Hard-to-debug redirect loops
* Confusion for new developers (and AI agents)

We want **one canonical routing shape** that is immediately recognizable across all projects.

---

## The Rule

> **If an Angular app requires login, it MUST use an Edge Router at the root level.**

This means:

* Public routes live at the edge (root routing config)
* The authenticated application is lazy-loaded behind a guard
* Authentication is enforced *before* route matching

---

## Reference Implementation

### Root Routes

```ts
import { Route } from '@angular/router';
import { LoginComponent } from './login/login.component';
import { authGuard } from './auth/auth.guard';

export const appRoutes: Route[] = [
  // Public routes (no authentication required)
  {
    path: 'login',
    component: LoginComponent,
  },

  // Authenticated routes (require authentication)
  // Main area is lazy-loaded only when the user is authenticated
  {
    path: '',
    canMatch: [authGuard],
    loadChildren: () => import('./main/main.routes').then(m => m.mainRoutes),
  },

  // Fallback for unknown routes
  {
    path: '**',
    redirectTo: '/',
  },
];
```

---

## Why `canMatch`?

`canMatch` acts **before** a route is considered a match.

This has important consequences:

* The lazy-loaded module is **not loaded at all** if the user is not authenticated
* Unauthorized users never even enter the main routing tree
* The router structure itself documents intent: *"this area does not exist unless you are authenticated"*

Using `canActivate` instead would:

* Allow route matching to happen first
* Potentially load the lazy module
* Move the responsibility too far inside the application

For authenticated applications, `canMatch` is the correct guard for the edge.

---

## Guard Contract (Required)

To avoid redirect loops and undefined behavior, the authentication guard must follow this contract:

* If the user **is authenticated** → return `true`
* If the user **is not authenticated**:

  * Allow access to public routes (e.g. `/login`)
  * Redirect all other routes to `/login`
  * Preserve the intended target via `returnUrl`

### Example Guard

```ts
import { CanMatchFn, Router } from '@angular/router';
import { inject } from '@angular/core';

export const authGuard: CanMatchFn = (route, segments) => {
  const router = inject(Router);
  const url = '/' + segments.map(s => s.path).join('/');

  const isAuthenticated = /* check session / token */ false;

  if (isAuthenticated) {
    return true;
  }

  return router.createUrlTree(['/login'], {
    queryParams: { returnUrl: url },
  });
};
```

---

## Variations

### Additional Public Routes

If your app has more public pages (e.g. `/privacy`, `/imprint`, `/sso/callback`), declare them **explicitly** next to `/login` in the edge router.

Do **not** hide public routes inside the authenticated lazy module.

---

### Fallback Behavior

The fallback route can be adjusted based on preference:

* `redirectTo: '/'` → guard will redirect unauthenticated users to `/login`
* `redirectTo: '/login'` → unknown routes always land on login

Both are acceptable, but the choice must be consistent per project.

---

## Do / Don't

### Do

* ✅ Use a single edge router at the root
* ✅ Lazy-load the authenticated application shell
* ✅ Use `canMatch` for authentication boundaries
* ✅ Keep public routes explicit and minimal

### Don't

* ❌ Mix public and authenticated routes in the same lazy module
* ❌ Guard every child route individually
* ❌ Rely on `canActivate` for application-level access control
* ❌ Let routing shape differ between projects without a reason

---

## Intent

This pattern is not about technical limitation — it is about **consistency and readability**.

Any developer (or AI agent) opening the routing config should immediately understand:

* Which routes are public
* Where authentication starts
* Where the main application begins

If this structure is followed everywhere, onboarding becomes faster and architectural discussions disappear.
