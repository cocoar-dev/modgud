---
layout: home

hero:
  name: Cocoar.Auth
  text: Multi-Tenant Identity Provider
  tagline: Cookie-basiertes Login + voller OAuth 2.0 / OIDC-Server. Aufgebaut auf den Authentication- und Authorization-Slices aus TimeToDo (Reference-Implementation), erweitert um Multi-Realm, OAuth-Admin und GDPR-Self-Service.
  actions:
    - theme: brand
      text: Konzepte
      link: /concepts/glossary
    - theme: alt
      text: Architektur
      link: /guide/architecture
    - theme: alt
      text: API-Referenz
      link: /reference/auth-api

features:
  - title: Multi-Realm via Database-per-Tenant
    details: Marten MasterTableTenancy weist jeden Realm eine eigene PostgreSQL-Datenbank zu. Domain-basiertes Routing über das Host-Header — keine tenant_id-Spalte, keine Cross-Realm-Leaks.
  - title: Vertical-Slice-Basis
    details: Authentication-Slice (Login, 2FA, Magic Link, Passkey, OIDC, GDPR, Sessions) und Authorization-Slice (Groups, Roles, Permissions, Script-ABAC) werden direkt als C#-Projekt-Kopien eingebunden (Reference-Implementation aus TimeToDo). Cocoar.Auth ergänzt nur das IdP-spezifische.
  - title: Granulares Per-Resource-Gating
    details: Permissions im "resource:action"-Format (z.B. user:read, oauth-client:write). Per-Resource-Admin-Bypass und globaler app:admin als Notausgang. Sidebar und Endpoints prüfen denselben String.
  - title: OpenIddict 7 mit Marten-Stores
    details: Eigene Application-, Scope-, Authorization- und Token-Stores über Marten. Realm-aware Issuer-URLs via RealmIssuerHandler — jeder Realm ist ein eigener OIDC-Provider mit eigenem Discovery-Dokument.
  - title: Vollständiges 2FA-Spektrum
    details: TOTP, Email-OTP, FIDO2/Passkey und Magic Link. Plus 2FA-Enforcement-Middleware mit konfigurierbarer Grace-Period und per-User-Override.
  - title: GDPR-Self-Service
    details: User exportieren ihre Daten (Article 20), starten Account-Löschung mit Confirmation-Token, können sie wieder canceln. Marten Data-Masking + ArchiveStream sorgen für Compliance ohne historische Lücken.
---
