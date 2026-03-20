# OAuth / OpenID Connect

Cocoar.Auth is a full OAuth 2.0 + OpenID Connect server powered by OpenIddict.

## Key Features

- **Authorization Code Flow** with PKCE (recommended for SPAs and native apps)
- **Client Credentials Flow** (machine-to-machine)
- **Reference Tokens** (mandatory — tokens are opaque, stored server-side for instant revocation)
- **Per-Realm Isolation**: Each realm has its own clients, scopes, and discovery endpoint
- **Consent Flow**: User consent screen for third-party applications

## Reference Tokens

All access tokens are **reference tokens**, not JWTs. This is a core architectural decision:

- Tokens are opaque strings that resolve to server-side data
- Immediate revocation — no need to wait for JWT expiry
- No sensitive claims exposed in the token itself
- Resource servers validate tokens via introspection endpoint

## Per-Realm OIDC

Each realm has its own OpenID Connect endpoints. The realm slug is always the first path segment:

| Endpoint | URL |
|----------|-----|
| Discovery | `/{slug}/.well-known/openid-configuration` |
| Authorize | `/{slug}/connect/authorize` |
| Token | `/{slug}/connect/token` |
| Introspect | `/{slug}/connect/introspect` |
| Revoke | `/{slug}/connect/revocation` |

For example, the system realm uses `/system/connect/token` and the Acme realm uses `/acme/connect/token`.

The issuer URL includes the realm prefix, ensuring tokens from one realm cannot be used in another.

## Admin Management

System and realm admins can manage OAuth entities through the admin UI:
- **Clients**: Application registrations with secrets, redirect URIs, grant types
- **Scopes**: Permission definitions (openid, email, profile, roles, custom)
- **APIs**: Protected APIs with their own secrets and associated scopes
