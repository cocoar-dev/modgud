# Login Providers (OIDC and SAML Federation)

::: tip Looking for the admin walkthrough?
This page is the technical / integration reference — provider model,
dynamic scheme registration, `UserUpdateScript` runtime, link schema.
For the step-by-step "set up Entra ID in the admin UI" walkthrough see
[Admin → Login Providers](/admin/login-providers).
:::

The slice models login providers as one `LoginProvider` aggregate per realm
with a protocol `Type` discriminator. Today the wired-up types are:

- `Internal` — built-in username + password (auto-seeded once per realm,
  not editable from the admin UI)
- `Oidc` — Microsoft Entra ID and standards-compatible OIDC providers such
  as Google, Auth0 or Keycloak
- `Saml` — Microsoft Entra Enterprise Applications and
  standards-compatible SAML 2.0 providers such as ADFS or Okta

Reserved (shape exists, handlers don't yet):

- `Ldap`, `Kerberos` — creation is rejected with the centralized
  `LoginProvider.TypeNotSupported` error.

Modgud is an OIDC provider to downstream applications. In SAML federation it
acts only as the **Service Provider (SP)** that consumes an upstream
assertion; it does not issue SAML assertions.

## Mental model

- Each OIDC provider is registered at runtime as an ASP.NET Core
  authentication scheme by `DynamicOidcSchemeManager`.
- Each SAML provider is registered in the realm-aware
  `DynamicSamlSchemeManager`; its public entry point is
  `/saml/{slug}/login`.
- OIDC completes through its per-provider callback and
  `/api/account/external-login/finish`. SAML completes through the
  per-provider `/saml/{slug}/acs` endpoint.
- Both protocols pass their validated external principal to the same
  `ExternalLoginProcessor`.
- `ExternalIdentityLink` (`Issuer + Subject → UserId`) is the only stable
  anchor — nobody maps users by email
- `UserUpdateScript` (Jint JavaScript) maps external claims onto user fields.

## Flavors

OIDC and SAML have separate flavor registries:

| Protocol | Flavor | Notes |
|---|---|---|
| OIDC | `EntraId` | Microsoft Entra ID — tenant-specific authority and Entra defaults |
| OIDC | `GenericOidc` | Standards-compatible OIDC — authority + client ID + secret |
| SAML | `GenericSaml` | Vendor-neutral SAML 2.0 SP configuration |
| SAML | `EntraIdSaml` | Microsoft Entra Enterprise Application defaults |
| SAML | `AdfsSaml` | Active Directory Federation Services defaults |

A flavor provides:

- Protocol-appropriate defaults such as OIDC authority/scopes or SAML
  attribute mappings
- The allowed `FlavorConfigField` list, which controls the admin UI
- An optional default for the `UserUpdateScript`

The flavor key does not change the protocol support boundary. Every SAML
flavor remains SP-only and SP-initiated in v1.

## LoginProvider document

Marten document in the tenant store. Selected fields:

| Field | Meaning |
|---|---|
| `Id` | GUID, internal identifier — also the base of the OIDC authentication scheme name |
| `Slug` | URL-stable, admin-chosen identifier. Immutable after creation. Used in OIDC callback and SAML SP/ACS URLs so deleting and recreating a provider can keep the same upstream configuration |
| `Type` | `Internal` / `Oidc` / `Saml` / `Ldap` / `Kerberos` |
| `IsBuiltIn` | True for the seeded Internal entry. Write commands reject edits. |
| `DisplayName` | Display name in the login UI ("Login with Acme SSO") |
| `Description` | Optional one-liner shown on hover / in admin UI |
| `Flavor` | Protocol-specific template key |
| `ClientId` | OIDC client ID; empty for SAML |
| `Scopes` | OIDC scopes; empty for SAML |
| `FlavorData` | OIDC connection settings or SAML metadata/attribute settings |
| `UserUpdateScript` | JavaScript snippet (Jint) |
| `StoreRawClaims` | bool — when true, every login stores the raw claims on the link (debug) |
| `Enabled` | bool — disabled providers show no login button |
| `IsDeleted` | bool — soft delete (Internal entries cannot be deleted) |

The OIDC **client secret** is not stored on the document but in a separate
`LoginProviderSecretStore` (Marten document, separate table). This keeps the
secret out of event streams and audit logs. SAML trust is established through
IdP metadata and its signing certificates rather than an OIDC client secret.

## Runtime provider registration

Login providers are realm-owned and editable at runtime, while ASP.NET
Core's normal authentication-scheme registration is static. Modgud therefore
maintains parallel protocol-specific runtime registries.

### OIDC

1. At boot, a **placeholder scheme** is registered that wires up the
   `OpenIdConnectHandler` type and the options plumbing. The placeholder
   scheme never receives real traffic.
2. `OidcSchemeBootstrap` (HostedService) loads all `LoginProvider` documents
   of every active realm at startup that are `Type == Oidc` and calls
   `DynamicOidcSchemeManager.Register(...)` for each.
3. `LoginProviderEventHandlers` (Wolverine handlers) react to
   create/update/delete events and call
   `DynamicOidcSchemeManager.Register/Reload/Unregister`.
4. The `DynamicOidcSchemeManager` registers a dedicated OIDC authentication
   scheme per OIDC `LoginProvider` (keyed off the provider's `Id`) with the
   options from the document; the callback path it listens on is keyed off
   the provider's `Slug` instead, so the callback URL survives a delete +
   recreate.

### SAML

1. `SamlSchemeBootstrap` loads enabled SAML providers from every active
   realm at cold start.
2. `SamlLoginProviderEventHandlers` update the runtime registration when a
   SAML provider changes.
3. `DynamicSamlSchemeManager` caches the realm, provider slug, IdP metadata,
   trust material and SP configuration.
4. `SamlEndpoints` expose SP metadata, SP-initiated login and ACS routes:
   `/saml/{slug}/sp-metadata`, `/saml/{slug}/login` and
   `/saml/{slug}/acs`.

Internal providers do not participate in either registry; the local
password/passkey/magic-link paths serve them directly. LDAP and Kerberos
remain unsupported.

## UserUpdateScript

Every IdP delivers different claim structures. OIDC claims and validated
SAML attributes are normalized into the same claim dictionary and mapped by
a JavaScript snippet executed in `Jint`.

The script gets the normalized external claims and returns a partial user
record:

```javascript
// claims: Dictionary<string, string[]> — validated OIDC claims or SAML attributes

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
| `UserId` | Linked Modgud user |
| `LoginProviderId` | Which `LoginProvider` minted the link |
| `LinkedAt` | First link |
| `LastLoginAt` | Most recent login through this link |
| `LastScriptOutput` | Patch the last script run produced |
| `LastScriptError` | Exception message of the last script run |
| `LastRawClaims` | Raw claim dict of the last login (only when `StoreRawClaims` is true) |

`LastScriptOutput`, `LastScriptError`, and `LastRawClaims` are **debug
artefacts** — overwritten on every login, not historised.

The user-record claim that pins the originating provider on every issued
session is `modgud.external.loginProviderId`.

## Email conflict handling

If an external login brings an email that already belongs to another user
(or to the same UserId but a different identity), the processor throws
`Idp.EmailConflict` and the login fails. Never merge accounts
implicitly — that is an account-takeover vector. The admin must
manually resolve the link (remove the link from the old user or attach
the new provider as an additional login).

## JIT user creation

If an OIDC or SAML login finds no existing `ExternalIdentityLink`:

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

SAML self-service linking has a known v1 limitation: the assertion returns
through a cross-site POST to the ACS endpoint, so the `SameSite=Lax`
Modgud application cookie is not sent. The ACS therefore cannot reliably
identify the already signed-in user who started the link flow. A SAML
identity must currently resolve through normal SAML sign-in, trusted-email
linking or JIT provisioning. Once linked, its stable `(issuer, subject)`
resolves normally on every later login.

Unlink:

```http
DELETE /api/account/external-links/{linkId}
```

The public `/api/account/external-logins` list includes enabled OIDC and
SAML providers and returns a `Kind` discriminator:

- OIDC starts at
  `/api/account/external-login/{loginProviderId}/start`.
- SAML starts at `/saml/{slug}/login`.

The OIDC `/start` and callback routes accept only OIDC providers. SAML has
its own ACS surface; Internal, LDAP and Kerberos never appear in the public
provider list.

## SAML v1 support boundary

- Modgud acts as a SAML **Service Provider**, never as a SAML IdP.
- Login is **SP-initiated**. Every accepted response must match a one-time
  AuthnRequest correlation record.
- IdP-initiated/unsolicited responses are rejected.
- SAML Single Logout (SLO) is not implemented. Logging out ends the local
  Modgud session; no SAML logout request is sent upstream.
- HTTP-Redirect and HTTP-POST bindings are supported; Artifact Binding is
  not.

See [SAML federation](/admin/saml-federation) for setup, security behavior
and troubleshooting.
