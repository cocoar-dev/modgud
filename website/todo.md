# TODOs

Tracking page for outstanding implementation tasks. Items here represent decisions made during documentation that need to be reflected in code.

## Naming / Refactoring

- [ ] **Rename "API Resource" → "API" in code and UI**
  - Backend: `OAuthApiResource` → `OAuthApi`, `ApiResourceListView` → `ApiListView`, endpoints `/admin/oauth/api-resources` → `/admin/oauth/apis`
  - Frontend: Vue views, admin-api.ts methods, models, router paths
  - Database: Marten document types, projection names
  - Scope: Full rename across backend + frontend + tests

## Features

- [ ] **Per-client access token type (Reference vs JWT)**
  - Add `AccessTokenType` setting to OAuth client configuration (Reference = default, JWT = optional)
  - UI: dropdown in client form
  - Backend: configure OpenIddict per-client token format
  - See: [Glossary > Access Token Types](/concepts/glossary#access-token-types)

- [ ] **Device Code grant type**
  - For Smart TVs, CLI tools
  - See: [Glossary > Grant Types](/concepts/glossary#grant-type)

## Architecture

- [ ] **Simplify realm URL scheme**
  - Remove `/realms/` prefix — realm is always the first path segment: `/{realm}/api/...`
  - `https://auth.example.com/` redirects to `/{system-realm}/`
  - System realm is just another realm at `/system/api/...`
  - Three resolution strategies (checked in order):
    1. **Custom FQDN** — `https://login.acme.com/api` → realm lookup by configured hostname
    2. **Subdomain** — `https://acme.auth.example.com/api` → subdomain as slug
    3. **Path** — `https://auth.example.com/acme/api` → first path segment as slug
  - Each realm can configure its public URLs (one or more FQDNs/subdomains)
  - Realm slug validation must reject reserved names (`swagger`, `health`, etc.) — though with realm-first routing these paths live under `/{realm}/` anyway
  - Scope: RealmMiddleware, frontend RealmContext, Vite proxy, cookie paths, OIDC issuer URLs, tests

## Documentation

- [ ] **Mermaid diagrams for flows**
  - Replace ASCII flow diagrams with Mermaid (VitePress supports it natively)
  - OAuth flow, account lifecycle, realm resolution, token validation
  - Install `mermaid` plugin for VitePress or use built-in markdown support

## Known Issues

- [ ] **Realm deletion does not drop tenant database**
  - Requires Wolverine daemon coordination
  - Currently only removes realm metadata from system tenant
  - See: [User Guide > Managing Realms](/user-guide/realms#deleting-a-realm)
