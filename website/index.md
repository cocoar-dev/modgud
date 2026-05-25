---
layout: home

hero:
  name: Modgud
  text: Multi-tenant Identity Provider
  tagline: OAuth 2.0 / OpenID Connect server with multi-app permissions, granular RBAC, full database-per-tenant isolation, and a Keycloak-shaped resource_access model — drop-in for Cocoar SaaS apps and any ASP.NET Core resource server.
  actions:
    - theme: brand
      text: Get started
      link: /getting-started/
    - theme: alt
      text: Concepts
      link: /concepts/apps-and-resource-access
    - theme: alt
      text: Integrate a resource server
      link: /guide/integrating-resource-server
    - theme: alt
      text: Source on GitHub
      link: https://github.com/cocoar-dev/Modgud

features:
  - title: Multi-tenant by design
    details: Every realm gets its own PostgreSQL database via Marten's master-table tenancy. Domain-based routing maps Host headers to tenants — no tenant_id columns, no cross-realm leaks possible.
  - title: Multi-app permission model
    details: Apps are first-class. Permissions are app-scoped (timetodo:todo:write), groups carry an activation list (BoundTo), roles bind to one app, and the resolver answers per-app permission queries in O(memory).
  - title: Keycloak-style resource_access
    details: Tokens carry resource_access keyed by app slug. A drop-in IClaimsTransformation library flattens the right block into ClaimTypes.Role so [Authorize(Roles="...")] works without per-endpoint plumbing.
  - title: Distribution API for granular permissions
    details: Resource servers fetch live, app-scoped permissions through GET /api/v1/distribution/me-permissions. Bearer + RS-Auth headers, 30 s cache, no token bloat. Permission revocation is effective within 30 s.
  - title: Full 2FA spectrum + WebAuthn
    details: TOTP, email-OTP, FIDO2/Passkey, magic-link. 2FA enforcement middleware with grace period and per-user override.
  - title: GDPR-ready
    details: Self-service data export (Article 20), confirmable account deletion, Marten data-masking that scrubs PII from event streams while preserving audit-chain integrity.
---
