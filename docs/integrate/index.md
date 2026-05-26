---
title: Integrate
description: Hooking your app up to Modgud — as an OAuth client, as a resource server protecting an API, or via cookie sessions for an internal SPA.
---

# Integrate

How to make your application talk to Modgud. Start with the
walkthrough that matches your scenario, then dive into the
protocol-specific pages.

## Walkthroughs

- [Resource server (.NET)](./resource-server) — the most common
  Cocoar scenario: protect an ASP.NET Core API with Modgud-issued
  tokens via the `Modgud.Client.AspNetCore` NuGet package.
- [SaaS app walkthrough](./saas-walkthrough) — full
  user-facing-app integration: client registration, login redirect,
  resource_access claims, role-based gating.

## Protocol pages

- [OAuth / OpenIddict](./oauth) — supported grant types, scopes,
  the discovery document, JWT vs reference tokens.
- [Login providers (OIDC federation)](./login-providers) — federate
  external IdPs (Entra ID, Google, Okta, any OIDC source) so users
  sign in with their existing accounts.
- [Login flows](./login-flows) — the on-wire shape of every
  supported user-facing flow.
- [2FA (TOTP, Email, Passkey)](./two-factor) — enrolling and
  enforcing second-factor authentication.

## Cookie / session integration

- [Cookies & sessions](./cookies-and-sessions) — when to use the
  cookie-session pattern instead of OAuth (typically: internal SPAs
  on the same domain as the IdP).

## Background work

- [Scheduling (Quartz)](./scheduling) — registering background jobs
  that operate against Modgud data.
