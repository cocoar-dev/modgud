# XAIdentity Forms Reference

Complete blueprint of all admin forms from xaidentity for rebuilding in cocoar.auth with Vue.

## 1. USERS

### List View Columns
- Username (fixed left, highlighted)
- Firstname (bold)
- Lastname (bold)
- Email (faded if not confirmed)
- Phone (faded if not confirmed)
- Logins (tags: "Internal" + external providers)
- UpdatedAt

### Detail Form Tabs

**Tab 1: Basic Information**
- UserName (required)
- FirstName, LastName
- Email (required), EmailConfirmed (checkbox)
- PhoneNumber, PhoneNumberConfirmed (checkbox)
- Options: TwoFactorEnabled, LockoutEnabled, ExpiresOn (datetime picker)

**Tab 2: Static Role Membership**
- Roles (multi-select from available roles)

**Tab 3: Claims**
- Claims grid (add/edit/delete Type+Value pairs)

### Context Menu
- Create User, Open in new Tab, Set Password (bulk), Remove Password (bulk), Delete (bulk)

### Data Model
```
UserListItem { Id, UserName, FirstName, LastName, Email, PhoneNumber,
  EmailConfirmed, PhoneNumberConfirmed, HasPassword, Logins[],
  ExpiresOn, CreatedAt, UpdatedAt, LastModifiedBy }
UserItem extends UserListItem { TwoFactorEnabled, LockoutEnabled,
  Claims[], ExternalClaims[], Roles[] }
```

---

## 2. ROLES

### List View Columns
- Name (fixed left, highlighted)
- ApiResource (tags)
- DisplayName, Email, Description
- Members (tags: "X roles/Y users")
- UpdatedAt

### Detail Form Tabs

**Tab 1: Basic Information**
- Name (required), DisplayName, Email
- Description (textarea)
- BoundToApiResource (dropdown, nullable)

**Tab 2: MemberOf**
- MemberOf (multi-select roles, excluding self)

**Tab 3: Members**
- Read-only table (Type + Name columns)

### Data Model
```
XARole { Id, Name, NormalizedName, Email, DisplayName, Description,
  BoundToApiResource, MemberOf[], Members[], BuiltIn,
  CreatedAt, UpdatedAt, LastModifiedBy }
```

---

## 3. CLIENTS (Most Complex)

### List View Columns
- Enabled (switch toggle, fixed left)
- ClientId (fixed left, highlighted)
- ClientName
- AllowedGrantTypes (colored tags: password=blue, implicit=green, hybrid=red, authorization_code=purple, client_credentials=yellow)
- AllowedScopes (tags)
- UpdatedAt

### Detail Form Tabs

**Tab 1: Basic Information**
- ClientId (required), ClientName (required)
- AccessTokenType: Radio (Reference | JWT)
- RefreshTokenUsage: Radio (OneTimeOnly | ReUse)
- AllowAccessTokensViaBrowser (checkbox)
- Client Secrets management section
- Options: Enabled (switch), RequireClientSecret, EnableLocalLogin, RequireConsent, AllowRememberConsent
- AllowedGrantTypes: Checkboxes (Password, Implicit, Client Credentials, Authorization Code, Hybrid)
- RoleAffix: Dropdown (None | Prefix | Suffix)

**Tab 2: Static Role Membership**
- Roles (multi-select)

**Tab 3: URI Options**
- RedirectUris (list editor)
- PostLogoutRedirectUris (list editor)
- AllowedCorsOrigins (list editor)

**Tab 4: Lifetime Options** (all in seconds)
- IdentityTokenLifetime, AccessTokenLifetime
- AuthorizationCodeLifetime, AbsoluteRefreshTokenLifetime
- SlidingRefreshTokenLifetime

**Tab 5: Scopes**
- AllowedScopes (multi-select from identity + API scopes)

**Tab 6: Claims**
- Claims grid
- Options: AlwaysSendClientClaims, UpdateAccessTokenClaimsOnRefresh, ClientClaimsPrefix

### Data Model
```
XAClient extends Client { Id, ClientId, ClientName, Enabled,
  AllowedGrantTypes[], AllowedScopes[], AccessTokenType, RefreshTokenUsage,
  AllowAccessTokensViaBrowser, RequireClientSecret, EnableLocalLogin,
  RequireConsent, AllowRememberConsent, ClientSecrets[],
  IdentityTokenLifetime, AccessTokenLifetime, AuthorizationCodeLifetime,
  AbsoluteRefreshTokenLifetime, SlidingRefreshTokenLifetime,
  Claims[], AlwaysSendClientClaims, ClientClaimsPrefix,
  AllowedCorsOrigins[], RedirectUris[], PostLogoutRedirectUris[],
  Roles[], RoleAffix, BuiltIn, CreatedAt, UpdatedAt, LastModifiedBy }
```

---

## 4. API RESOURCES

### List View Columns
- Enabled (switch toggle, fixed left)
- Name (fixed left, highlighted)
- DisplayName, Description

### Detail Form Tabs

**Tab 1: Basic Information**
- Name (required), DisplayName (required)
- Description (textarea)
- ApiSecrets management section
- Enabled (switch)
- UserClaims (list editor: add/remove claim types)

**Tab 2: Scopes**
- Scopes manager (add/edit/delete scopes with properties)

### Data Model
```
XAApiResource { Id, Name, DisplayName, Description, Enabled,
  ApiSecrets[], Scopes[], UserClaims[],
  BuiltIn, CreatedAt, UpdatedAt, LastModifiedBy }
```

**Note for OpenIdDict:** API Resources don't exist as a concept. Map to OpenIdDict Scopes with additional metadata.

---

## 5. IDENTITY RESOURCES

### List View Columns
- Enabled (switch toggle, fixed left)
- Name (fixed left, highlighted)
- DisplayName, Description

### Detail Form Tabs

**Tab 1: Basic Information**
- Name (required), DisplayName (required)
- Description (textarea)
- Options: Enabled (switch), Required, Emphasize, ShowInDiscoveryDocument
- UserClaims (list editor)

### Data Model
```
XAIdentityResource { Id, Name, DisplayName, Description, Enabled,
  Required, Emphasize, ShowInDiscoveryDocument, UserClaims[],
  BuiltIn, CreatedAt, UpdatedAt, LastModifiedBy }
```

**Note for OpenIdDict:** Identity Resources map to OpenIdDict Scopes. The UserClaims define which claims are included when the scope is granted.

---

## 6. TENANTS

### List View Columns
- ShortName (fixed left, highlighted)
- Name, Description
- DefaultProvider (tag)
- UpdatedAt, LastModifiedBy

### Detail Form (Single Tab)

**Basic Information:**
- ShortName, Name
- Description (textarea)
- DefaultProvider (dropdown from login providers, nullable)
- Providers (multi-select checkboxes with filter)

### Data Model
```
XATenantDbModel { Id, Name, ShortName, Description,
  Providers[], DefaultProvider,
  BuiltIn, CreatedAt, UpdatedAt, LastModifiedBy }
```

---

## 7. LOGIN PROVIDERS

### List View Columns
- Name (fixed left, highlighted)
- DisplayName
- Type (Internal | Ldap | OpenIdConnect)
- Description
- UpdatedAt, LastModifiedBy

### Detail Form Tabs (dynamic by type)

**Tab 1: Basic Information** (always)
- Name (required), DisplayName
- Description (textarea)
- Type: Radio (Internal | Ldap | OpenIdConnect)

**Tab 2: Configuration** (Ldap/OIDC only)
- For Ldap: BindUserDN, BindUserPassword, BindBaseDN, LDAP Hosts grid (Host, Port, UseSsl, Enabled)
- For OpenIdConnect: (authority, clientId, etc.)

**Tab 3: Ldap Sync** (Ldap only, edit mode)
- LDAP user synchronization component

### Data Model
```
LoginProviderDto { Id, Type, Name, DisplayName, Description, Configuration }
LdapProviderConfiguration { Hosts[], BindBaseDN, BindUserDN, BindUserPassword }
LdapServer { Enabled, Host, Port, UseSsl }
```

---

## UI PATTERNS

- All entities have audit fields: CreatedAt, UpdatedAt, LastModifiedBy
- All entities have a BuiltIn flag (prevents deletion of system entities)
- Tabs for grouping related form sections
- ag-grid for all list views with context menus
- Multi-select + bulk actions
- Create vs Edit mode differentiation
- Debug tab (dev only) showing JSON preview
- Double-click grid row to open detail
- Escape key clears selection
