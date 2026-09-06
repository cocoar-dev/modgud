# CIMD (Client ID Metadata Documents): authorization-server design

**Status:** Accepted — shipped 2026-06-14 (branch `feat/cimd-client-id-metadata-documents`) · **Decided:** 2026-06-13

Complements ADR-0001 (CIMD = preferred MCP client-registration path; DCR = fallback).

**Sources:** `draft-ietf-oauth-client-id-metadata-document-00`; MCP authorization spec; OpenIddict 7 `OpenIddictServerHandlers.Exchange` (spike).

## Context

ADR-0001 chose CIMD as the preferred MCP client-registration path. With CIMD the `client_id` **is** an HTTPS URL; the AS fetches a JSON metadata document from it on demand and treats it as the client registration — no open registration endpoint, no client secret, identity bound to domain ownership. This ADR is the AS-side implementation design.

## Normative AS obligations (draft-00)

- **client_id** = `https` URL: path required; no dot-segments; **no fragment**; no userinfo; SHOULD have no query; MAY have port.
- AS **fetches** (GET) → JSON. Document **MUST contain `client_id` == the URL** (RFC 3986 §6.2.1 exact string compare).
- `token_endpoint_auth_method` **MUST NOT** be a shared-secret method; `client_secret*` forbidden → **public (`none`)** or `private_key_jwt` (+`jwks_uri`) only.
- `redirect_uris` in the doc are the registered set → **exact-match** at authorize (RFC 9700). AS MAY require same-origin to client_id.
- **SSRF:** avoid private/loopback addresses. **Max 5 KB.** Never cache error/invalid docs; otherwise respect HTTP cache headers.
- **Consent:** SHOULD display the client_id **hostname** (phishing mitigation).
- **Discovery:** advertise `client_id_metadata_document_supported: true`.
- MCP: PKCE-S256 (already global), `resource` (RFC 8707 — handler exists).

## Decision

1. **Integrate via the application store.** `MartenApplicationStore.FindByClientIdAsync` detects a CIMD URL → fetch+validate+cache → returns a **synthesized, non-persisted `OAuthApplicationState`** (Public, RequireClientSecret=false; RedirectUris/Grants/Scopes from the doc; `AccessTokenType=Jwt`). Normal client_ids take the existing stored path; DCR untouched (fallback).
2. **No persisted client record (Option A).** The synthesized app uses a **deterministic Id = stable hash of the client_id URL** (SHA256→Guid), so all its authorizations/tokens share a consistent ApplicationId without any DB write.
3. **v1 = public only (`none` + PKCE).** Covers claude.ai / ChatGPT CIMD. `private_key_jwt` deferred to v2; advertise only `none` for CIMD.
4. **SSRF-hardened resolver (`CimdClientResolver`):** https-only; resolve DNS and **block by resolved IP** (private/loopback/link-local/unique-local/CGNAT/multicast/documentation) at connect time to defend DNS-rebinding; no redirects; ~5 s timeout; **5 KB** body cap; `Accept: application/json`. Validation: `client_id`==URL exact; auth_method==`none`; `redirect_uris` present + each https-or-loopback.
5. **Cache** (per fetched URL): respect `Cache-Control` with own min/max clamp (5 min–24 h); **never** cache error/invalid; re-fetch on expiry (refresh re-validates the live doc).
6. **Discovery handler** adds `client_id_metadata_document_supported: true` (analogous to `TokenEndpointAuthMethodsSupportedHandler`), gated on the realm toggle.
7. **Consent** always shown on first authorize; display the client_id **hostname**.
8. **Opt-in per realm** (RealmSetting toggle, like DCR), default off.

## Spike result (recorded, 2026-06-13)

OpenIddict 7 `OpenIddictServerHandlers.Exchange` — the authorization_code + refresh_token grants resolve the application **via `FindByClientIdAsync` only**; **none** call `FindByIdAsync`/`GetApplicationIdAsync`. A public client always sends `client_id` at authorize/token/refresh → a synthesized app survives the full flow with **no DB record**. → **Option A viable.**

## Implementation (shipped 2026-06-14)

Confirmed end-to-end against Testcontainers: `CimdFullFlowTests` (6 cases incl. refresh round-trip — proving Option A: refresh re-resolves via `FindByClientIdAsync`) + 71 unit tests (`CimdIpGuard`, `CimdClientId`, `CimdMetadataParser`). 1174 unit + 21 DCR/consent integration tests stayed green (no regression).

Spike gap — Option A needed more than the OpenIddict core path. The spike only covered OpenIddict's own `Exchange` resolution. Several **custom** Modgud sites resolve the client by **direct Marten query by ClientId**, which MISSES a non-persisted CIMD client. Each had to fall back to the resolver (`app ??= await cimdResolver.ResolveAsync(...)`):
- `AccessTokenTypeHandler` (else CIMD got reference, not JWT tokens),
- `DcrAudienceContainmentHandler` (else CIMD escaped audience containment — security),
- `AuthorizationEndpoints.ValidateScopeRestrictionAsync` (else CIMD couldn't reach app-scoped opted-in scopes),
- `ConsentEndpoints.GetConsentInfoAsync` (the [unverified] flag + hostname),
- `TokenMintMetricHandler` (client_type tag). `DcrLastUsedTrackerHandler` is intentionally left direct-query-only → it no-ops for non-persisted CIMD clients.

The synthesized state carries `DcrIsDynamicallyRegistered=true` (+ a `CimdIsResolvedClient` marker), so DCR audience containment + the "unverified" consent treatment apply for free — matching the decision to **reuse the DCR resource/scope opt-in surface** (per-OAuthApi `AllowDynamicRegistration` + per-scope `AllowDynamicRegistrationClients`).

"Consent always shown" was walked back to "shown on first authorize." Forcing consent on *every* authorize breaks the flow: the post-consent re-entry to `/authorize` relies on the very `authorizations.Count != 0` shortcut to complete the round-trip and emit the code — skipping it loops the user back to `/consent` forever. DCR doesn't re-prompt either. The real phishing mitigation is the **hostname + [unverified] marker on the consent screen** (shown the first time, ConsentType=explicit), which is implemented.

Per-client lifetime uses OpenIddict-native keys, not the `modgud:` ones. The token pipeline enforces lifetimes from `tkn_lft:act` / `tkn_lft:reft` (`OpenIddictConstants.Settings.TokenLifetimes.*`, TimeSpan `"c"` format). The `modgud:access_token_lifetime` keys are only for admin display of *persisted* clients (which CIMD never is), so the resolver writes the native keys.

**Touch-points (code):** `Modgud.Infrastructure/OpenIddict/Cimd/` (`CimdClientId`, `CimdIpGuard`, `CimdMetadata`+parser, `CimdHttpMessageHandlerFactory` [SocketsHttpHandler.ConnectCallback SSRF guard], `CimdClientResolver`); `MartenApplicationStore.FindByClientIdAsync` (stored-first, then resolver); `CimdMetadataDocumentSupportedHandler` + registration in `OpenIddictExtensions`; the 5 CIMD-aware handler/endpoint edits above; `CimdSettings` on `RealmSettings` + DTOs + `RealmSettingsService` + the SPA realm-settings CIMD tab + consent-hostname display.

## Alternatives considered (and rejected)

- **Option B — thin persisted pointer minted on first use:** rejected after the spike; kept as the documented fallback if an impl flow ever needs `FindByIdAsync`-without-client_id. (Not needed.)
- **private_key_jwt in v1:** deferred.
- **Always-on (no opt-in):** rejected — new SSRF surface; gate per realm like DCR.

## Consequences

- **Security win preserved:** no open registration endpoint, no client secret at rest, identity bound to domain ownership; no DB record minted by strangers.
- **New surface:** server-side fetch of a client-controlled URL → SSRF, mitigated by the resolver hardening (connect-time IP block closes DNS-rebind). SSRF negative tests are unit-level.
- **Availability coupling:** refresh after cache expiry / restart re-fetches the client's document; if the URL is unreachable, refresh fails and the client must re-auth.
- **Disable latency:** a cache hit serves without re-reading the realm toggle, so disabling CIMD stops NEW resolutions and takes full effect at cache expiry — same "disable doesn't retroactively revoke" stance as DCR.

## Follow-up

- **v2:** `private_key_jwt` + `jwks_uri`; on jwks change → revoke.
- **MCP `iss` (RFC 9207)** — fixed in PR #70 (`RealmAuthorizationResponseIssuerHandler`); prerequisite for clean MCP interop. See ADR-0002.

## References

- `draft-ietf-oauth-client-id-metadata-document-00`; MCP authorization spec (modelcontextprotocol.io).
- Spike: OpenIddict 7 `OpenIddictServerHandlers.Exchange.cs`.
- Builds on ADR-0001 (CIMD-vs-DCR), ADR-0002 (issuer derivation, incl. the RFC 9207 fix), ADR-0007 (JWT access tokens).
- Docs: `docs/admin/client-id-metadata-documents.md`.
