# Public origin is derived, not configured (per-realm host, request-derived issuer, fail-closed proxy trust)

**Status:** Superseded · **Decided:** 2026-06-13

> Superseded 2026-09-03 by the declared-public-origin decision recorded as ADR 0023 (Modgud now declares the public origin per realm via `Realm.PublicBaseUrl` instead of deriving it). Kept here as the historical record.

## Context

The codebase inherited from an earlier reference template a single instance-wide **`PublicUrl`** setting. It conflated three distinct concerns into one value: the Kestrel **bind address**, the **public-facing origin** (used for outbound email links + the WebAuthn relying-party ID), and the **OIDC issuer**. One global value cannot be correct in a multi-realm / multi-domain deployment, and as a static config it silently drifts. Separately, a configurable `OpenIddict__Issuer` existed but **never took effect** (the issuer is request-derived), which gave false assurance.

## Decision

1. **`AppUrl` = Kestrel bind address only** — explicitly documented as *not* the public origin. Defaults to plain HTTP behind a TLS-terminating proxy.
2. **Public origin (outbound links + WebAuthn RP) is derived per-realm** from `Realm.PrimaryDomain` via the single `RealmPublicUrl` helper; it throws on an empty host rather than emit a host-less link.
3. **OIDC issuer is derived per-request** from the validated forwarded host (`BaseUri`). There is **no configurable issuer** (removed; OpenIddict is given a deliberately-unroutable placeholder `https://issuer.invalid/` which is overridden on every issuer-emitting path). The override sites are: **discovery** (`RealmIssuerHandler`), the **token `iss` claim** (`RealmSigningKeyHandler`), **token validation** (`RealmTokenValidationHandler`), and the **RFC 9207 authorization-response `iss`** (`RealmAuthorizationResponseIssuerHandler`). The last was **missing until PR #70** — the placeholder leaked into the authorize redirect; see Consequences.
4. **Forwarded-header trust fails closed.** `ProxyAllowedNetworks` (CIDR allow-list) pins the trusted proxy range; when unset in Production an unroutable **RFC 5737 sentinel** keeps ASP.NET Core's known-IP check active so *all* `X-Forwarded-*` are rejected — because empty known-lists make the middleware **trust-all**, not reject-all (the PROD-03 inversion bug).

## Alternatives considered (and rejected)

- **Global `PublicUrl`** (inherited from the earlier reference template): rejected — conflates bind vs. origin, impossible for >1 host, drifts.
- **Configurable issuer**: rejected — never emitted (issuer is per-realm/request); a knob that does nothing is worse than none (false assurance; audit L1).
- **Leaving the proxy known-lists empty**: rejected — ASP.NET Core treats that as trust-all, enabling `X-Forwarded-Host`/`-Proto` spoofing.

## Consequences

- Correct across multiple realms/domains; the issuer and outbound links always match the host the client reached.
- **The placeholder-issuer approach has a cost:** every path that emits the issuer must override the placeholder, and **missing one is a silent, latent bug**. This bit the **authorization-response `iss`** (RFC 9207): discovery advertised the real issuer + `authorization_response_iss_parameter_supported=true`, but the redirect carried `iss=https://issuer.invalid/` → strict clients (rising MCP cohort) reject. Fixed in PR #70 (`RealmAuthorizationResponseIssuerHandler`). When adding any new issuer-emitting surface, override the placeholder there too (or reconsider whether the placeholder is worth the whack-a-mole vs. letting `Options.Issuer` be null so OpenIddict's `Options.Issuer ?? BaseUri` falls back to `BaseUri` everywhere — needs verifying OpenIddict tolerates a null issuer at startup).
- Operational requirement: the reverse proxy MUST forward `Host`/`Proto`, and `ProxyAllowedNetworks` MUST be set in Production.
- **Cross-project:** the same `PublicUrl` anti-pattern exists in the shared template codebase Modgud was originally based on; a feature-request/lesson was filed there.

## References

- Code: `Modgud.Authentication/RealmPublicUrl.cs`, `Modgud.Api/ForwardedHeadersTrust.cs`, and the issuer handlers `RealmIssuerHandler` / `RealmSigningKeyHandler` / `RealmTokenValidationHandler` / `RealmAuthorizationResponseIssuerHandler`.
- See ADR-0004 (tenancy) for the per-realm model `Realm.PrimaryDomain` belongs to; ADR-0008 (CIMD) surfaced the RFC 9207 gap.
