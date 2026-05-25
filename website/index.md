---
layout: home

hero:
  name: Modgud
  text: Multi-tenant Identity Provider
  tagline: 'Named after <em>Móðguðr</em>, the watcher of Gjallarbrú.<br><br>OAuth 2.0 / OpenID Connect server with multi-app permissions, granular RBAC, and full database-per-tenant isolation.<br><br>Drop-in for any ASP.NET Core resource server — and the auth foundation of the Cocoar suite.'
  image:
    light: /logo_light.svg
    dark: /logo_dark.svg
    alt: Modgud shield logo

features:
  - icon: '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="12 2 2 7 12 12 22 7 12 2"/><polyline points="2 17 12 22 22 17"/><polyline points="2 12 12 17 22 12"/></svg>'
    title: Multi-tenant by design
    details: Every realm gets its own PostgreSQL database via Marten's master-table tenancy. Domain-based routing maps Host headers to tenants — no tenant_id columns, no cross-realm leaks possible.
  - icon: '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="7.5" cy="15.5" r="5.5"/><path d="m21 2-9.6 9.6"/><path d="m15.5 7.5 3 3L22 7l-3-3"/></svg>'
    title: Multi-app permission model
    details: Apps are first-class. Permissions are app-scoped (timetodo:todo:write), groups carry an activation list (BoundTo), roles bind to one app, and the resolver answers per-app permission queries in-memory.
  - icon: '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="5" width="20" height="14" rx="2"/><path d="M16 10h2"/><path d="M16 14h2"/><path d="M6.17 15a3 3 0 0 1 5.66 0"/><circle cx="9" cy="11" r="2"/></svg>'
    title: Keycloak-style resource_access
    details: Tokens carry resource_access keyed by app slug. A drop-in IClaimsTransformation library flattens the right block into ClaimTypes.Role so [Authorize(Roles="...")] works without per-endpoint plumbing.
  - icon: '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="18" cy="5" r="3"/><circle cx="6" cy="12" r="3"/><circle cx="18" cy="19" r="3"/><line x1="8.59" x2="15.42" y1="13.51" y2="17.49"/><line x1="15.41" x2="8.59" y1="6.51" y2="11.49"/></svg>'
    title: Distribution API for granular permissions
    details: Resource servers fetch live, app-scoped permissions through GET /api/v1/distribution/me-permissions. Bearer + RS-Auth headers, 30 s cache, no token bloat. Permission revocation is effective within 30 s.
  - icon: '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 10a2 2 0 0 0-2 2c0 1.02-.1 2.51-.26 4"/><path d="M14 13.12c0 2.38 0 6.38-1 8.88"/><path d="M17.29 21.02c.12-.6.43-2.3.5-3.02"/><path d="M2 12a10 10 0 0 1 18-6"/><path d="M2 16h.01"/><path d="M21.8 16c.2-2 .131-5.354 0-6"/><path d="M5 19.5C5.5 18 6 15 6 12c0-.7.12-1.37.34-2"/><path d="M8.65 22c.21-.66.45-1.32.57-2"/><path d="M9 6.8a6 6 0 0 1 9 5.2v2"/></svg>'
    title: Full 2FA spectrum + WebAuthn
    details: TOTP, email-OTP, FIDO2/Passkey, magic-link. 2FA enforcement middleware with grace period and per-user override.
  - icon: '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/><path d="m9 12 2 2 4-4"/></svg>'
    title: GDPR-ready
    details: Self-service data export (Article 20), confirmable account deletion, Marten data-masking that scrubs PII from event streams while preserving audit-chain integrity.
---

## About the name

**Modgud** takes its name from **Móðguðr**, the watcher of *Gjallarbrú* in Norse mythology — a bridge between worlds, where she challenged every traveler with the same question an IdP asks: *"Who are you, and what brings you here?"* A fitting namesake.
