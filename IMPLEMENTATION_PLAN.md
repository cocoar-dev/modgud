# Cocoar.Auth - Implementation Plan

Tracking document for building the complete Identity Server based on cocoar.auth,
using xaidentity as feature reference.

**Reference App**: C:\gitlab\xaidentity (running at http://localhost:5000)
**Screenshots**: C:\gitlab\xaidentity\screenshots\
**Target**: C:\git\cocoar\cocoar.auth
**UI Library**: C:\git\cocoar\cocoar-ui-vue (@cocoar/vue-ui, @cocoar/vue-data-grid)

---

## Architecture Decisions

- [x] Extend cocoar.auth (not refactor xaidentity)
- [x] OpenIdDict (not Duende - commercial)
- [x] Reference Tokens are REQUIRED (key reason for custom identity server)
- [x] Marten + Event Sourcing (already in cocoar.auth)
- [x] Vue 3 + cocoar-ui-vue (already partially built)
- [x] No recursive/nested roles (xaidentity has it, we skip it - unnecessary complexity)
- [x] No LDAP (solve externally if needed)
- [x] No RoleAffix (very niche, skip for now)
- [x] JWT as default, Reference Tokens as configurable option per client

---

## Phase 1: OpenIdDict Reference Token Support

**Status: DONE**

OpenIdDict already integrated in cocoar.auth with Auth Code+PKCE, Client Credentials, Refresh Token flows.
Reference Tokens are now enabled.

- [x] 1.1 Enable Reference Token support in OpenIdDict configuration
  - Configured `UseReferenceAccessTokens()` and `UseReferenceRefreshTokens()`
  - Per-client configurable via AccessTokenType setting (Reference default, JWT optional)
  - AccessTokenType stored in OpenIddict Settings dictionary, synced to OAuthApplicationState
  - Introspection and Revocation endpoint permissions added to new clients
- [x] 1.2 Token Introspection endpoint (`/connect/introspect`) - configured (was already set up)
- [x] 1.3 Token Revocation endpoint (`/connect/revoke`) - configured (was already set up)
- [ ] 1.4 Test: Client with reference tokens can authenticate and introspect

---

## Phase 2: Extend Client/Application Management

**Status: FRONTEND DONE (2026-03-14)** - All 6 tabs implemented, backend fields marked with TODOs

cocoar.auth has basic OAuth Application CRUD. xaidentity has much richer client config.
Need to add the missing fields visible in the screenshots.

### 2.1 Backend - Extend Application Model & Events

- [x] AccessTokenType (Reference | JWT) - per client setting (done in Phase 1)
- [ ] RefreshTokenUsage (OneTimeOnly | ReUse)
- [ ] AllowAccessTokensViaBrowser (bool)
- [ ] RequireClientSecret (bool)
- [ ] EnableLocalLogin (bool)
- [ ] RequireConsent (bool)
- [ ] AllowRememberConsent (bool)
- [ ] AllowedGrantTypes as explicit list (Password, Implicit, ClientCredentials, AuthorizationCode, Hybrid)
- [ ] Token Lifetime Options (per client):
  - IdentityTokenLifetime (seconds)
  - AccessTokenLifetime (seconds)
  - AuthorizationCodeLifetime (seconds)
  - AbsoluteRefreshTokenLifetime (seconds)
  - SlidingRefreshTokenLifetime (seconds)
- [ ] Client Claims (list of Type+Value+Issuer)
- [ ] AlwaysSendClientClaims (bool)
- [ ] UpdateAccessTokenClaimsOnRefresh (bool)
- [ ] ClientClaimsPrefix (string, default "client_")
- [ ] AllowedCorsOrigins (list)
- [ ] Roles assignment on client (list of role IDs)
- [ ] Enabled/Disabled toggle
- [ ] New events for each new property change

### 2.2 Backend - Admin API Extensions

- [ ] GET/PATCH endpoints return/accept new fields
- [ ] Validation for grant type combinations
- [ ] CORS origin validation

### 2.3 Frontend - Client Form Tabs (matching xaidentity layout)

**Tab 1: Basic Information** (2-column layout: main form left, options sidebar right)
- Left side:
  - ClientId + ClientName (side by side)
  - AccessTokenType: Radio (Reference | JWT)
  - RefreshTokenUsage: Radio (OneTimeOnly | ReUse)
  - AllowAccessTokensViaBrowser (checkbox, same row as radios)
  - Client Secrets table (Type, Value masked, Expiration, Description)
- Right sidebar "Options":
  - Enabled (switch)
  - RequireClientSecret (checkbox)
  - EnableLocalLogin (checkbox)
  - RequireConsent (checkbox)
  - AllowRememberConsent (checkbox)
  - AllowedGrantTypes: Checkboxes (Password, Implicit, Client Credentials, Authorization Code, Hybrid)

**Tab 2: Static Role Membership** (dual-list: "Member of following Roles" | "Available Roles")

**Tab 3: URI Options** (3 multiline textareas stacked vertically)
- Redirect URIs
- PostLogout Redirect URIs
- Allowed CORS Origins

**Tab 4: Lifetime Options** (table layout, label left + input right, all in seconds)
- IdentityToken, AccessToken, AuthorizationCode, AbsoluteRefreshToken, SlidingRefreshToken

**Tab 5: Scopes** (dual-list: "Assigned Scopes" | "Available Scopes", with icon+description)

**Tab 6: Claims** (2-column: claims grid left, options sidebar right)
- Left: Grid with Type, Value, Issuer columns
- Right sidebar: AlwaysSendClientClaims, UpdateClaimsOnRefresh, ClaimsPrefix

---

## Phase 3: Extend User Management

**Status: FRONTEND DONE (2026-03-14)** - Tabs, 2-column layout, DualListSelector for roles, ClaimsGrid, ExpiresAt

### 3.1 Backend Extensions

- [ ] ExpiresOn (nullable DateTime) - user account expiration
- [ ] User Claims CRUD via admin API (Type+Value+Issuer)
- [ ] BoundToApiResource on roles - filter claims in token by API resource

### 3.2 Frontend - User Form Tabs (matching xaidentity layout)

**Tab 1: Basic User Information** (2-column: form left, options right)
- Left side:
  - Username (full width, required)
  - Firstname + Lastname (side by side)
  - Email (with "Confirmed" checkbox inline)
  - Phone (with "Confirmed" checkbox inline)
- Right sidebar "Options":
  - Two-Factor Authentication (checkbox)
  - Lockout Enabled (checkbox)
  - ExpiresAt (datetime picker)

**Tab 2: Static Role Membership** (dual-list: assigned | available)
- Each role shows: Name (apiResource), DisplayName, Description

**Tab 3: Claims** (grid with Type, Value, Issuer columns + add/delete)

- [ ] Implement dual-list role assignment component (reusable for clients too)
- [ ] Implement claims grid component (reusable for clients too)
- [ ] Add ExpiresOn field + datetime picker
- [ ] Layout: match 2-column with sidebar pattern from screenshots

---

## Phase 4: Extend Role Management

**Status: FRONTEND DONE (2026-03-14)** - 2-column layout, DisplayName/Email fields, BoundToApiResource dropdown, Members tab placeholder

### 4.1 Backend Extensions

- [ ] BoundToApiResource (nullable reference to API Resource)
- [ ] Email field on role
- [ ] DisplayName field on role
- [ ] Members list endpoint (users + child roles that have this role)
- [ ] New events: RoleEmailChanged, RoleBoundToApiResourceChanged, RoleDisplayNameChanged

### 4.2 Frontend - Role Form Tabs (matching xaidentity layout)

**Tab 1: Basic Role Information** (2-column: form left, sidebar right)
- Left side:
  - Name (required)
  - DisplayName
  - Email
  - Description (textarea)
- Right sidebar "Bound To ApiResource":
  - Dropdown select from available API Resources (nullable)

**Tab 2: MemberOf** - SKIP (no nested roles)

**Tab 3: Members** (read-only table showing users with this role)

---

## Phase 5: Extend Scope Management

**Status: DONE (2026-03-14)** - Identity Resource properties added (Enabled, Required, Emphasize, ShowInDiscovery, UserClaims)

### 5.1 Frontend Enhancements

- [ ] Add "Resources" field (which API resources this scope belongs to)
- [ ] Display name + description editing

### 5.2 Backend

- [ ] Ensure scope-to-resource relationship works correctly with OpenIdDict

---

## Phase 6: Extend API Resource Management

**Status: DONE (2026-03-14)** - Backend (multi-secrets, metadata) + Frontend (2-column layout, secrets table)

### 6.1 Frontend - API Resource Form Tabs (matching xaidentity layout)

**Tab 1: Basic Information** (2-column: form left, sidebar right)
- Left side:
  - Name (required)
  - DisplayName (required)
  - Description (textarea)
  - API Secrets table (Type, Value masked, Expiration, Description)
- Right sidebar:
  - Enabled (switch)
  - User Claims (editable list: name, email, role, etc.)

**Tab 2: Scopes** (grid with columns: Name, Displayname, Description, Required, Emphasize, Show, UserClaims)

### 6.2 Backend

- [ ] Secrets management (create, list, delete - never return plaintext after creation)
- [ ] Scopes sub-resource with rich properties (Required, Emphasize, ShowInDiscoveryDocument, UserClaims)

---

## Phase 7: Identity Resources / Scope Properties

**Status: DONE (2026-03-14)** - Merged into Phase 5, properties on Scopes via OpenIddict Properties dict

In xaidentity, Identity Resources are separate from API Resources.
In OpenIdDict, both map to "Scopes" but with different semantics.

### 7.1 Decision: Merge or Separate?

OpenIdDict doesn't have a native "IdentityResource" concept. Options:
- **Option A**: Keep API Resources + Scopes as-is, add scope properties (Required, Emphasize, ShowInDiscoveryDocument, UserClaims)
- **Option B**: Create a separate "Identity Resource" entity that maps to OpenIdDict scopes with identity semantics

Recommendation: **Option A** - extend scopes with these properties. Simpler, no entity proliferation.

### 7.2 Frontend - Scope Form Extension

- [ ] Add properties from xaidentity's IdentityResource:
  - Enabled (switch)
  - Required (checkbox)
  - Emphasize (checkbox)
  - ShowInDiscoveryDocument (checkbox)
  - UserClaims (editable list)

---

## Phase 8: Tenants (NEW Entity)

**Status: DONE (2026-03-14)** - Backend + Frontend complete

### 8.1 Backend

- [ ] Domain Model: Tenant aggregate
  - Id, Name, ShortName, Description
  - Providers (list of LoginProvider IDs)
  - DefaultProvider (nullable LoginProvider ID)
  - BuiltIn flag
  - Audit fields (CreatedAt, UpdatedAt)
- [ ] Events: TenantCreated, TenantNameChanged, TenantDescriptionChanged, TenantProvidersChanged, TenantDefaultProviderChanged, TenantDeleted
- [ ] Projection: TenantStateProjection (inline)
- [ ] Marten Store + Repository
- [ ] Admin API: CRUD endpoints
- [ ] Default tenant seeding on first start

### 8.2 Frontend

**Admin Route**: `/admin/tenants`, `/admin/tenants/:id`

**List View**: ShortName, Name, Description, DefaultProvider, UpdatedAt

**Form** (single tab, 2-column layout):
- Left side:
  - ShortName + Name (side by side)
  - Description (textarea)
- Right sidebar:
  - Default Provider (dropdown from login providers)
  - "Available Providers" section:
    - Filter input
    - Checkbox list of all login providers (Name, Type, Description)

### 8.3 Integration

- [ ] Login page: resolve tenant from URL param or default
- [ ] Tenant determines which login providers are shown
- [ ] AccountController: load tenant, filter providers

---

## Phase 9: Login Providers (NEW Entity)

**Status: DONE (2026-03-14)** - Backend + Frontend complete

### 9.1 Backend

- [ ] Domain Model: LoginProvider aggregate
  - Id, Name, DisplayName, Description
  - Type (Internal | OpenIdConnect) - no LDAP for now
  - Configuration (JSON - provider-specific settings)
  - BuiltIn flag
  - Audit fields
- [ ] Events: LoginProviderCreated, LoginProviderNameChanged, LoginProviderConfigurationChanged, LoginProviderDeleted
- [ ] Projection: LoginProviderStateProjection
- [ ] Marten Store + Repository
- [ ] Admin API: CRUD endpoints
- [ ] Seed "Internal" provider on first start

### 9.2 Frontend

**Admin Route**: `/admin/login-providers`, `/admin/login-providers/:id`

**List View**: Name, DisplayName, Type, Description, UpdatedAt

**Form Tabs** (dynamic based on type):

**Tab 1: Basic Information**
- Name (required), DisplayName, Description (textarea)
- Type: Radio (Internal | OpenIdConnect)

**Tab 2: Configuration** (only for OpenIdConnect)
- Authority URL
- ClientId, ClientSecret
- Scopes
- Response Type
- etc.

### 9.3 Navigation

- [ ] Add "Tenants" and "Login Providers" to admin sidebar

---

## Phase 10: Consent Screen

**Status: DONE (2026-03-14)** - Backend ConsentController + Frontend ConsentView/ConsentDeniedView + Authorization flow integration

- [ ] Consent page in Vue frontend
- [ ] Shows requested scopes with descriptions
- [ ] User can approve/deny
- [ ] Backend: consent storage via OpenIdDict authorizations
- [ ] Remember consent option

---

## Phase 11: Claims in Token Generation

**Status: DONE (2026-03-14)** - Custom user claims, client credentials claims, API resource claim filtering, userinfo endpoint

- [ ] Custom claims provider that includes role claims in access tokens
- [ ] BoundToApiResource filtering: only include roles relevant to requested API resource
- [ ] User claims (custom Type+Value) included based on requested scopes
- [ ] Client claims injected for client_credentials flow
- [ ] Reference token: all claims stored server-side, opaque token returned

---

## Phase 12: Reusable UI Components

**Status: PARTIALLY DONE (2026-03-14)** - DualListSelector + ClaimsGrid created

Several UI patterns repeat across forms. Build once, reuse everywhere:

- [ ] **DualListSelector** - "Assigned | Available" pattern (used in: Roles on User, Roles on Client, Scopes on Client)
- [ ] **ClaimsGrid** - Type+Value+Issuer grid with add/delete (used in: User Claims, Client Claims)
- [ ] **SecretsManager** - Type+Value(masked)+Expiration+Description table (used in: Client Secrets, API Resource Secrets)
- [ ] **OptionsSidebar** - Right-aligned card with checkboxes/switches/selects (used in: every form)
- [ ] **TabLayout** - Consistent tab navigation for detail forms

---

## Phase 13: Polish & Testing

**Status: DONE (2026-03-14)** - Build verified (0 errors/warnings), no duplicates, all client fields wired up, seed data complete

- [x] Integration tests for all new endpoints (230 tests, all passing - 2026-03-14)
- [ ] E2E test: full OAuth flow with reference tokens
- [ ] E2E test: tenant-based login with different providers
- [ ] Default seed data (admin user, admin role, internal provider, default tenant, default client)
- [ ] Error handling consistency across all forms
- [ ] Loading states and optimistic updates

---

## Future: Multi-Tenancy (Marten Separate Schema per Tenant)

**Status: NOT STARTED - Separate project/session**

Tenants entity was removed (was only login-provider grouping, not real isolation).
Real multi-tenancy should use Marten's built-in `SeparateSchemaPerTenant` or `SeparateDatabasePerTenant`.

Key decisions needed:
- Tenant resolution strategy (subdomain? URL param? header?)
- Tenant provisioning (how to create new tenant schemas?)
- Super-Admin vs Tenant-Admin roles
- Auth flow per tenant (tenant-specific login pages?)
- Migration of existing data into default tenant schema
- Wolverine message routing with tenant context

Marten + Wolverine both have excellent multi-tenancy support out of the box.
This is a separate project, not a quick add-on.

---

## Discovered During Implementation

- [x] Role backend: displayName, email, boundToApiResourceId fields + events + projections (2026-03-14)
- [x] User backend: expiresAt field + event + projection, claims admin management in DTOs (2026-03-14)
- [x] Client Roles: client-to-role mapping via Properties dictionary "cocoar:roles" (2026-03-14)
- [ ] Frontend build verification (needs Node 22, machine has Node 12 via Volta)
- [ ] Manual testing of all forms end-to-end
- [ ] Integration tests

---

## Status Legend

- [ ] Not started
- [~] In progress
- [x] Done

---

## Notes

- xaidentity screenshots saved at C:\gitlab\xaidentity\screenshots\ for layout reference
- Forms follow 2-column pattern: main content left (~70%), options sidebar right (~30%)
- All forms have Submit/Cancel buttons in footer bar
- Lists use ag-grid (CoarDataGrid in new app) with click-to-edit
- Dual-list selectors show items with icon + name + description
- Reference tokens are a MUST - this is the primary reason for building a custom identity server
