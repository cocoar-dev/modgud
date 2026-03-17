# Realms

## What is a Realm?

A realm is a **fully autonomous identity provider**. It is the fundamental isolation boundary in Cocoar.Auth.

Each realm has:
- Its own **PostgreSQL database** (complete data isolation)
- Its own **users and roles**
- Its own **OAuth clients, scopes, and API resources**
- Its own **OpenID Connect discovery endpoint**
- Its own **login providers** (Google, Microsoft, SAML, etc.)
- Its own **cookie scope** (sessions don't leak across realms)

## The Equality Principle

Every feature works for **all realms equally**. The system realm is not special — it's just a realm that can additionally manage other realms.

```
┌─────────────────────────────────────────────┐
│              System Realm                    │
│  ┌────────────────────────────────────────┐  │
│  │  Users, Roles, OAuth, 2FA, Sessions   │  │  ← Same as any realm
│  └────────────────────────────────────────┘  │
│  ┌────────────────────────────────────────┐  │
│  │  + Realm Management (CRUD)            │  │  ← Only extra capability
│  └────────────────────────────────────────┘  │
└─────────────────────────────────────────────┘

┌─────────────────────────────────────────────┐
│              Acme Realm                      │
│  ┌────────────────────────────────────────┐  │
│  │  Users, Roles, OAuth, 2FA, Sessions   │  │
│  └────────────────────────────────────────┘  │
└─────────────────────────────────────────────┘
```

## URL Structure

Every request lives within a realm context. The realm is always present in the URL — there is no "default" that omits it. `https://auth.example.com/` simply redirects to the system realm.

### Path-Based (default)

The realm slug is the first path segment:

| URL | Realm |
|-----|-------|
| `https://auth.example.com/system/` | System realm |
| `https://auth.example.com/acme/` | Acme realm |
| `https://auth.example.com/corp/` | Corp realm |

API endpoints:

| Example | |
|---------|---|
| `/{realm}/api/auth/login` | Login |
| `/{realm}/api/admin/users` | Admin users |
| `/{realm}/.well-known/openid-configuration` | OIDC discovery |

### Subdomain-Based

Each realm can also be accessed via subdomain:

| URL | Realm |
|-----|-------|
| `https://system.auth.example.com/api/...` | System realm |
| `https://acme.auth.example.com/api/...` | Acme realm |

### Custom Domain

Realms can configure custom FQDNs:

| URL | Realm |
|-----|-------|
| `https://login.acme.com/api/...` | Acme realm (custom domain) |

### Realm Resolution

Every incoming request is matched against the configured realm URLs. If no realm matches, the request gets a 404.

```
Request rein
    │
    ├── Host: login.acme.com      → bekannt? → Ja → Realm "acme"
    │
    ├── Host: acme.auth.example.com → bekannt? → Ja → Realm "acme"
    │
    ├── Path: /acme/api/...        → bekannt? → Ja → Realm "acme"
    │
    └── Nichts matcht → 404
```

Each realm can have multiple configured URLs (path, subdomain, custom domain). The system checks all of them. There is no fallback — if the URL doesn't belong to a realm, it doesn't exist.

### The System Realm

The system realm has two small special rules:

1. **Cannot be deleted or deactivated** — it's always available
2. **Path-based access is always guaranteed** — `/{system-slug}/` cannot be removed from its URL configuration

This ensures there's always a known entry point to manage the system, even if DNS or custom domains are misconfigured.

`https://auth.example.com/` (root without realm) redirects to the system realm.

::: danger TODO
The current implementation uses `/realms/{slug}/api` with a special case for the system realm at `/api`. The simplified scheme described here (realm as first path segment, subdomain and custom domain support) is planned. See [TODOs](/todo#architecture).
:::

## Database Isolation

Each realm gets a dedicated PostgreSQL database:

```
cocoar_auth_master   → Realm registry
cocoar_auth_system   → System realm
cocoar_auth_acme     → Acme realm
cocoar_auth_corp     → Corp realm
```

This provides the strongest possible isolation:
- No risk of cross-realm data leaks
- Independent backup/restore per realm
- Different retention policies possible
- Realm deletion = drop database

## Realm Lifecycle

```
Create Realm  →  needsSetup: true  →  First Admin Setup  →  Ready
     ↓                                      ↓
  Database created                   Admin account created
  Schema applied                     Auto-logged-in
  Scopes seeded                      needsSetup: false
```

See [Managing Realms](/user-guide/realms) for step-by-step instructions.
