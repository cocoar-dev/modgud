---
layout: home

hero:
  name: Cocoar.Auth
  text: Multi-Tenant Identity Provider
  tagline: Clean Architecture, Event Sourcing, Realm Isolation. Built with ASP.NET Core, Marten, and Vue.
  actions:
    - theme: brand
      text: Get Started
      link: /guide/overview
    - theme: alt
      text: API Reference
      link: /reference/auth-api

features:
  - title: Multi-Tenant Realms
    details: Each realm is a fully autonomous identity provider with its own database, users, roles, and OAuth clients. The system realm manages all others.
  - title: Event Sourcing + CQRS
    details: All user and role mutations are event-sourced via Marten. CQRS with Wolverine for clean command/query separation.
  - title: Realm-Aware SPA
    details: The Vue frontend detects the realm from the URL, prefixes API calls, and adapts the UI. Zero realm-specific code in views.
  - title: Cookie-Based Security
    details: HttpOnly, Secure, SameSite=Lax cookies scoped per realm. Reference tokens for OAuth. No JWTs in the browser.
  - title: Full 2FA Support
    details: TOTP authenticator apps, email OTP, WebAuthn/Passkeys, and recovery codes. All per-realm isolated.
  - title: GDPR Compliant
    details: Data export (Article 20), account deletion with confirmation period, and Marten's built-in data masking.
---
