# Login Providers (OIDC Federated Login)

The slice models login providers as a single `LoginProvider` aggregate per
realm with a `Type` discriminator. Today the wired-up types are:

- `Internal` — built-in username + password (auto-seeded once per realm,
  not editable from the admin UI)
- `Oidc` — external OIDC IdPs: Entra ID (Microsoft), Google, Auth0,
  Keycloak, any OIDC-compliant provider

Reserved (shape exists, handlers don't yet):

- `Saml`, `Ldap`, `Kerberos` — the create endpoint rejects these with a
  centralized `LoginProvider.TypeNotSupported` error so the frontend doesn't
  have to encode a separate "not supported" UI state per type.

## Mental model

- Each `LoginProvider` of type `Oidc` is an OIDC client against an external
  IdP, registered at runtime as an ASP.NET Core authentication scheme by
  `DynamicOidcSchemeManager`
- On login the OIDC flow is initiated against that scheme
- `ExternalIdentityLink` (`Issuer + Subject → UserId`) is the only stable
  anchor — nobody maps users by email
- `UserUpdateScript` (Jint JavaScript) maps claims onto user fields

## Flavors

`LoginProviderFlavorRegistry` holds the OIDC templates. Currently:

| Flavor | Notes |
|---|---|
| `EntraIdFlavor` | Microsoft Entra ID — tenant-specific authority, `?prompt=select_account` default |
| `GenericOidcFlavor` | Standard OIDC — authority + client ID + secret are enough |

A flavor provides:

- Default values for `Authority`, `Scopes`, `ResponseType`
- Allowed `FlavorConfigField` list (which inputs the admin UI shows)
- An optional default for the `UserUpdateScript`

New flavors live under `Identity/LoginProviders/Flavors/` and are registered
in `Program.cs`.

## LoginProvider document

Marten document in the tenant store. Selected fields:

| Field | Meaning |
|---|---|
| `Id` | GUID, used as the scheme name `oidc-{guid}` for OIDC providers |
| `Type` | `Internal` / `Oidc` / `Saml` / `Ldap` / `Kerberos` |
| `IsBuiltIn` | True for the seeded Internal entry. Write commands reject edits. |
| `DisplayName` | Display name in the login UI ("Login with Acme SSO") |
| `Description` | Optional one-liner shown on hover / in admin UI |
| `Flavor` | `entra-id` / `generic-oidc` / ... (OIDC only) |
| `ClientId` | OIDC client ID |
| `Scopes` | Array (e.g. `["openid", "email", "profile"]`) |
| `UserUpdateScript` | JavaScript snippet (Jint) |
| `StoreRawClaims` | bool — when true, every login stores the raw claims on the link (debug) |
| `Enabled` | bool — disabled providers show no login button |
| `IsDeleted` | bool — soft delete (Internal entries cannot be deleted) |

The **client secret** is not stored on the document but in a separate
`LoginProviderSecretStore` (Marten document, separate table). This keeps the
secret out of event streams and audit logs.

## Dynamic scheme registration

ASP.NET Core's `AuthenticationOptions` is normally static — all schemes
must be known at boot. We want to add realm-owned LoginProviders at
runtime.

Solution:

1. At boot, a **placeholder scheme** is registered that wires up the
   `OpenIdConnectHandler` type and the options plumbing. The placeholder
   scheme never receives real traffic.
2. `OidcSchemeBootstrap` (HostedService) loads all `LoginProvider` documents
   of every active realm at startup that are `Type == Oidc` and calls
   `DynamicOidcSchemeManager.Register(...)` for each.
3. `LoginProviderEventHandlers` (Wolverine handlers) react to
   create/update/delete events and call
   `DynamicOidcSchemeManager.Register/Reload/Unregister`.
4. The `DynamicOidcSchemeManager` registers a dedicated OIDC scheme
   `oidc-{guid}` per OIDC `LoginProvider` with the options from the document.

Internal providers don't participate in this — they are served by the
local password-login path.

## UserUpdateScript

Every IdP delivers different claim structures. We map them via a
JavaScript snippet, executed in `Jint`.

The script gets the raw OIDC claims and returns a partial user record:

```javascript
// claims: Dictionary<string, string[]> — everything that came in the OIDC token

return {
  firstname: claims['given_name']?.[0],
  lastname:  claims['family_name']?.[0],
  email:     claims['email']?.[0],
  acronym:   (claims['given_name']?.[0]?.[0] ?? '') +
             (claims['family_name']?.[0]?.[0] ?? '')
};
```

The returned patch is applied to the user (only the fields that come
back — skipping `acronym` is fine). Fields that aren't set remain
unchanged.

The test endpoint (`POST /api/admin/login-providers/{id}/test-user-update`)
lets admins dry-run the script with synthetic claims before deployment.

::: warning Script errors do NOT block login
If the script throws, the exception is stored in `LastScriptError` on
the `ExternalIdentityLink`, but the login goes through — the existing
user fields simply remain unchanged. The admin sees the error in the
provider's detail. This prevents a buggy script from locking out every
SSO user.
:::

## ExternalIdentityLink

Marten document that maps `(Issuer, Subject) → UserId`. The only stable
anchor for SSO. Selected fields:

| Field | Meaning |
|---|---|
| `Id` | hash(Issuer + Subject) |
| `Issuer` | From `iss` claim |
| `Subject` | From `sub` claim |
| `UserId` | Linked Cocoar.Auth user |
| `LoginProviderId` | Which `LoginProvider` minted the link |
| `LinkedAt` | First link |
| `LastLoginAt` | Most recent login through this link |
| `LastScriptOutput` | Patch the last script run produced |
| `LastScriptError` | Exception message of the last script run |
| `LastRawClaims` | Raw claim dict of the last login (only when `StoreRawClaims` is true) |

`LastScriptOutput`, `LastScriptError`, and `LastRawClaims` are **debug
artefacts** — overwritten on every login, not historised.

The user-record claim that pins the originating provider on every issued
session is `cocoar.external.loginProviderId`.

## Email conflict handling

If an OIDC login brings an email that already belongs to another user
(or to the same UserId but a different identity), the processor throws
`Idp.EmailConflict` and the login fails. Never merge accounts
implicitly — that is an account-takeover vector. The admin must
manually resolve the link (remove the link from the old user or attach
the new provider as an additional login).

## JIT user creation

If an OIDC login finds no existing ExternalIdentityLink:

1. A `UserName` is generated from the claims (email or `preferred_username`)
2. A new user is created without password and without 2FA requirement
   (`TwoFactorExempt = false`; 2FA may be configured later)
3. `UserUpdateScript` runs to set the initial fields
4. An `ExternalIdentityLink` is created
5. The login cookie is set

The new user lands in no group → receives no permissions and no bypass
(no `realm:admin`, no `<app>:admin`). The admin must manually add them
to groups so they get any authorisation. Auto-membership (see Authorization
slice) can automate this.

## Account linking (self-service)

Logged-in users can link an additional OIDC provider to their account:

```http
GET /api/account/external-login/{loginProviderId}/start?returnUrl=/profile
```

The browser runs through the OIDC flow, comes back, the processor
recognises the logged-in user and creates an `ExternalIdentityLink`
instead of creating a new user.

Unlink:

```http
DELETE /api/account/external-links/{linkId}
```

The user-facing OIDC endpoints (login button list, `/start`, callback)
only return `Type == Oidc` providers. Internal-typed entries never appear
on those surfaces.
