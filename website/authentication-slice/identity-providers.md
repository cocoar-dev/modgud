# Identity Providers (OIDC Federated Login)

The slice supports any number of external OIDC providers — Entra ID
(Microsoft), Google, Auth0, Keycloak, any OIDC-compliant IdP.
Configuration runs per realm via `IdpConfig` documents in the tenant
store.

## Mental model

- Each `IdpConfig` is an OIDC client against an external IdP
- It is registered at runtime as an ASP.NET Core authentication scheme
  (`DynamicOidcSchemeManager`)
- On login the OIDC flow is initiated against that scheme
- `ExternalIdentityLink` (`Issuer + Subject → UserId`) is the only
  stable anchor — nobody maps users by email
- `UserUpdateScript` (Jint JavaScript) maps claims onto user fields

## Flavors

`FlavorRegistry` holds all built-in IdP templates. Currently:

| Flavor | File | Notes |
|---|---|---|
| `EntraIdFlavor` | `Identity/ExternalAuth/Flavors/EntraIdFlavor.cs` | Microsoft Entra ID — tenant-specific authority, `?prompt=select_account` default |
| `GenericOidcFlavor` | `Identity/ExternalAuth/Flavors/GenericOidcFlavor.cs` | Standard OIDC — authority + client ID + secret are enough |

A flavor provides:

- Default values for `Authority`, `Scopes`, `ResponseType`
- Allowed `FlavorConfigField` list (which inputs the admin UI shows)
- An optional default for the `UserUpdateScript`

New flavors are added under `Identity/ExternalAuth/Flavors/` and
registered in `Program.cs`:

```csharp
builder.Services.AddSingleton<IIdentityProviderFlavor, MyCustomFlavor>();
```

## IdpConfig document

Marten document in the tenant store. Fields:

| Field | Meaning |
|---|---|
| `Id` | GUID, used as the scheme name `oidc-{guid}` |
| `Name` | Display name in the login UI ("Login with Acme SSO") |
| `Flavor` | `entra-id` / `generic-oidc` / ... |
| `Authority` | OIDC issuer URL |
| `ClientId` | OIDC client ID |
| `Scopes` | Array (e.g. `["openid", "email", "profile"]`) |
| `UserUpdateScript` | JavaScript snippet (Jint) |
| `StoreRawClaims` | bool — when true, every login stores the raw claims on the link (debug) |
| `IsActive` | bool — inactive providers show no login button |
| `IsDeleted` | bool — soft delete |

The **client secret** is not stored in the document but in a separate
`IdpSecretStore` (Marten document, separate table). This keeps the
secret out of event streams and audit logs.

## Dynamic scheme registration

ASP.NET Core's `AuthenticationOptions` is normally static — all schemes
must be known at boot. We want to add realm-owned IdpConfigs at
runtime.

Solution:

1. At boot, a **placeholder scheme** is registered
   (`DynamicOidcSchemeManager.SchemeNamePrefix + "placeholder"`) that
   wires up the `OpenIdConnectHandler` type and the options plumbing.
   The placeholder scheme never receives real traffic.

2. `OidcSchemeBootstrap` (HostedService) loads all `IdpConfig`
   documents of every active realm at startup and calls
   `DynamicOidcSchemeManager.Register(idpConfig)` for each.

3. `IdpConfigEventHandlers` (Wolverine handlers) react to
   create/update/delete events and call
   `DynamicOidcSchemeManager.Register/Reload/Unregister`.

4. The `DynamicOidcSchemeManager` registers a dedicated OIDC scheme
   `oidc-{guid}` per `IdpConfig` with the options from the document.

## UserUpdateScript

Every IdP delivers different claim structures. We map them via a
JavaScript snippet, executed in `Jint`.

The script gets two arguments:

```javascript
// claims: Dictionary<string, string[]> — everything that came in the OIDC token
// user: { firstname, lastname, email, acronym, accountName } — the current user snapshot

return {
  firstname: claims['given_name']?.[0] ?? user.firstname,
  lastname:  claims['family_name']?.[0] ?? user.lastname,
  email:     claims['email']?.[0] ?? user.email,
  acronym:   (claims['given_name']?.[0]?.[0] ?? '') +
             (claims['family_name']?.[0]?.[0] ?? '')
};
```

The returned patch is applied to the user (only the fields that come
back — skipping `acronym` is fine). Fields that aren't set remain
unchanged.

The test endpoint (`/api/admin/idp-config/{id}/test-script`) lets
admins dry-run the script with synthetic claims before deployment.

::: warning Script errors do NOT block login
If the script throws, the exception is stored in `LastScriptError` on
the `ExternalIdentityLink`, but the login goes through — the existing
user fields simply remain unchanged. The admin sees the error in the
IdP config detail. This prevents a buggy script from locking out every
SSO user.
:::

## ExternalIdentityLink

Marten document that maps `(Issuer, Subject) → UserId`. The only stable
anchor for SSO. Fields:

| Field | Meaning |
|---|---|
| `Id` | hash(Issuer + Subject) |
| `Issuer` | From `iss` claim |
| `Subject` | From `sub` claim |
| `UserId` | Linked Cocoar.Auth user |
| `IdpConfigId` | Which `IdpConfig` created the link |
| `LinkedAt` | First link |
| `LastLoginAt` | Most recent login through this link |
| `LastScriptOutput` | Patch the last script run produced |
| `LastScriptError` | Exception message of the last script run |
| `LastRawClaims` | Raw claim dict of the last login (only when `StoreRawClaims` is true) |

`LastScriptOutput`, `LastScriptError`, and `LastRawClaims` are **debug
artefacts** — overwritten on every login, not historised.

## Email conflict handling

If an OIDC login brings an email that already belongs to another user
(or to the same UserId but a different identity), the processor throws
`Idp.EmailConflict` and the login fails. Never merge accounts
implicitly — that is an account-takeover vector. The admin must
manually resolve the link (remove the link from the old user or attach
the new IdP as an additional login).

## JIT user creation

If an OIDC login finds no existing ExternalIdentityLink:

1. A `UserName` is generated from the claims (email or
   `preferred_username`)
2. A new user is created without password and without 2FA requirement
   (`TwoFactorExempt = false`; 2FA may be configured later)
3. `UserUpdateScript` runs to set the initial fields
4. An `ExternalIdentityLink` is created
5. The login cookie is set

The new user lands in no group → receives no permissions and no
bypass (no `realm:admin`, no `<app>:admin`). The admin must manually
add them to groups so they get any authorisation. Auto-membership
(see Authorization slice) can automate this.

## Account linking (self-service)

Logged-in users can link an additional OIDC provider to their account:

```http
POST /api/account/external-link/{idpConfigId}/start?returnUrl=/profile
```

The browser runs through the OIDC flow, comes back, the processor
recognises the logged-in user and creates an `ExternalIdentityLink`
instead of creating a new user.

Unlink:

```http
DELETE /api/account/external-link/{linkId}
```
