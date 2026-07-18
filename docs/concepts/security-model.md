# Security model

## Overview

This page is one aggregated, honest view of Modgud's OAuth 2.0 / OpenID Connect and tenant-isolation security posture: what's supported, what's deliberately rejected, and what's tracked but not yet built. It's written for security reviewers and integrators evaluating Modgud before connecting a client or resource server. Every row and pointer below links back to the page that documents the mechanism in depth — treat this page as the index, not the primary source.

## Mechanism matrix

| Mechanism | Status | Default | Note |
|---|---|---|---|
| [Authorization Code + PKCE](./oauth#authorization-code-pkce-for-user-apps) | Supported | `S256` required, `plain` removed | Enforced for every client, public or confidential — there is no opt-out. |
| Implicit flow | Not supported | — | Deliberately rejected; OAuth 2.1 deprecates it. |
| ROPC (Resource Owner Password Credentials) | Not supported | — | Deliberately rejected; OAuth 2.1 deprecates it. |
| [Client Credentials](./oauth#client-credentials-for-services) | Supported | Via Service Accounts only | Structurally tied to [Service Accounts](/admin/service-accounts) — a standard client can never carry `client_credentials`, and a Service-Account-linked client can carry nothing else. |
| [Device flow (RFC 8628)](./oauth#device-code-rfc-8628) | Supported | Opt-in per client | Hosted user-verification page at `/connect/verify`. |
| Native cookieless grants (`urn:cocoar:*`) | Supported | Off | `otp`, `magic`, and `passkey` variants; each must be opted in on both the realm and the individual client before it's usable. See [Native apps](/integrate/native-apps). |
| [Refresh tokens](./tokens#refresh-token) | Supported | On when `offline_access` is granted | Reference tokens, single-use rolling rotation, zero reuse leeway. A replayed token revokes the whole family — every sibling token plus the parent authorization — not just itself. |
| RFC 9207 `iss` in authorization responses | Supported | Always on | Present on the success path and on the consent-deny error path alike. |
| Redirect URI validation | Supported | Exact string match | Applies to `redirect_uri` and `post_logout_redirect_uri` alike; no prefix or wildcard matching. |
| RP-initiated logout | Supported | `id_token_hint` mandatory | Stricter than the OIDC baseline, where the hint is optional. Logout also fully revokes the session's tokens, not just the cookie. |
| Back-channel / front-channel logout | Not supported | — | No server-initiated logout propagation to relying parties yet. Tracked in [#118](https://github.com/cocoar-dev/modgud/issues/118). |
| PAR (RFC 9126) | Not supported | — | Pushed authorization requests. Tracked in [#118](https://github.com/cocoar-dev/modgud/issues/118). |
| DPoP (RFC 9449) | Not supported | — | Sender-constrained tokens. Tracked in [#118](https://github.com/cocoar-dev/modgud/issues/118) as the intended complement to rejecting mTLS client auth. |
| mTLS client authentication | Not supported | — | Deliberately rejected, not merely unimplemented. |
| Token Exchange (RFC 8693) | Not supported | — | No delegation or impersonation grant today. |
| [Dynamic Client Registration](/admin/dynamic-client-registration) | Supported | Off | Triple opt-in (realm + per-API + per-scope). Public PKCE clients only, no secrets. The compatibility fallback for agents that don't support CIMD. |
| [Client ID Metadata Documents](/admin/client-id-metadata-documents) | Supported | Off | The spec-preferred onboarding path for MCP clients; no stored client record, SSRF-hardened fetch of the client-supplied metadata document. |
| [Access token formats](./tokens#access-token) | Supported | Reference tokens | JWT is a per-client opt-in — signed only, never encrypted. A resource server can validate a JWT locally via JWKS, but JWT revocation only takes effect at expiry, unlike a reference token's instant revocation. |

## Trust boundaries

- **Realm = hard boundary.** Every realm has its own physical PostgreSQL database, its own RSA signing key for access and ID tokens, and its own `/.well-known/openid-configuration` + JWKS — a token from one realm cannot validate against another realm's discovery document, structurally, not just by convention. See [Realms](./realms) and [Key material](/operate/key-material).
- **App = soft facet.** An Application can carry its own subdomain, branding, and login / native-grant / DCR / CIMD policy, but it shares its realm's user pool, signing keys, and OIDC issuer. Promoting an App to its own realm is how you get independent key rotation or breach containment.
- **One instance-global exception.** The OpenIddict signing and encryption certificate pair (`signing.pfx` / `encryption.pfx`) is shared by every realm on the instance. It only ever protects artifacts redeemed at the IdP itself — authorization codes, device codes, refresh tokens — never bearer material handed to a third party, so giving each realm its own certificate here would add validation surface without buying additional isolation. See [Key material](/operate/key-material) for the full rotation and blast-radius picture.

## Threat-model pointers

| Surface | Mitigation | Details |
|---|---|---|
| Token theft | Refresh-token rotation with family revocation on replay bounds a stolen refresh token's blast radius; pair JWT access tokens with short lifetimes since they can't be revoked before expiry. | [Sessions & Tokens](./tokens) |
| DCR / CIMD abuse | Both off by default, gated per realm + API + scope; CIMD's outbound fetch is SSRF-hardened; accepted residual risks (brand impersonation, targeted phishing via redirect URI) are documented rather than hidden. | [Dynamic Client Registration](/admin/dynamic-client-registration), [Client ID Metadata Documents](/admin/client-id-metadata-documents) |
| Membership scripts | Auto-membership predicates run through a sandboxed TypeScript-to-LINQ translator, tested against an adversarial suite covering resource exhaustion, native-host escape, type confusion, cross-tenant probing, injection, and information disclosure. | [Automated tests](/contribute/testing/automated-tests) |
| Tenant isolation | Realm boundaries are physical database separation, not a query filter. | [Realms](./realms) |
| Operational security | Per-realm rate-limit ceilings on auth endpoints, a 7-day security log for threat signals, and a separate GDPR-aware audit trail for admin/config changes. | [Auth Log](/admin/auth-log) |

## Verification

Claims on this page aren't just prose — they're pinned by CI-gated tests that fail the build if the behavior regresses.

- **OWASP Top 10 (2021) subset** — `OwaspTop10Tests`, 12 tests under the `OWASP=Top10` xUnit trait, part of the integration suite.
- **Security-audit regression suite** — the `SecurityAuditWave*Tests` classes, one per remediation wave, guarding against re-introducing a previously-fixed finding.
- **PKCE enforcement pin** — `PkceRequirementPinTests` asserts `S256` is present and `plain` is absent from the discovery document's supported code-challenge methods.
- **CodeQL** — C# and JavaScript, `security-extended` query pack, runs on every pull request plus a weekly scheduled scan. The configuration is public.
- **Dependency gates** — every pull request blocks on `dotnet list package --vulnerable` and `pnpm audit`; GitHub Dependabot alerts catch new CVEs in between.

Found something this page doesn't cover, or a gap in the above? See the [security policy](https://github.com/cocoar-dev/modgud/blob/develop/SECURITY.md) for how to report it.
