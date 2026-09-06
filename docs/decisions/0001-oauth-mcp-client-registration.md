# OAuth / MCP client registration: DCR (public + confidential) now, CIMD next

**Status:** Accepted — DCR (public + confidential) shipped (PR #68, `348a8d0`); CIMD adopted as the preferred future direction, implementation pending (design phase) · **Decided:** 2026-06-13

## Context

Modgud is an OAuth 2.1 / OIDC IdP whose primary near-term clients are **MCP connectors** (claude.ai, ChatGPT / OpenAI Apps SDK, likely Microsoft Copilot). These clients **self-onboard** — nobody pre-registers each one by hand. Two mechanisms exist:

- **RFC 7591 Dynamic Client Registration (DCR):** the client POSTs to `/connect/register`; the server mints a `client_id` (and optionally a secret) and stores a client record.
- **Client ID Metadata Document (CIMD):** the `client_id` *is* an HTTPS URL pointing to a JSON metadata document the server fetches on demand — no stored record, no open registration endpoint. `draft-ietf-oauth-client-id-metadata-document` (Parecki/Smith, **adopted by the IETF OAuth WG Oct 2025**); **MCP made CIMD the preferred default over DCR**. Both claude.ai and ChatGPT support it.

DCR v1 accepted **only public PKCE clients** (`token_endpoint_auth_method = none`). But claude.ai's connector registers **confidential** (`client_secret_post` / `client_secret_basic`) and was rejected with `InvalidTokenAuthMethod` — observed directly in the production audit log. MCP clients fall back to DCR when CIMD isn't advertised, so DCR must accept confidential.

## Decision

1. **DCR accepts public AND confidential clients** (`none`, `client_secret_basic`, `client_secret_post`). A secret-based method → `Confidential` client with a server-generated, hashed secret returned **once** per RFC 7591 §3.2.1. `private_key_jwt` is **not** accepted via DCR (no JWKS intake in the request shape) → rejected with a hint to pre-register. *(Shipped, PR #68.)*
2. **Discovery advertises `none`** in `token_endpoint_auth_methods_supported` so the metadata reflects the public clients the server actually supports (OpenIddict emits only confidential methods by default). *(Shipped, PR #68.)*
3. **CIMD is the chosen primary path going forward**, with DCR (now public + confidential) kept as the **fallback** — mirroring what Anthropic and OpenAI do ("prefer CIMD, fall back to DCR"). *(Accepted direction; not yet implemented.)*

## Rationale

- DCR-confidential is the immediate unblocker for the real, observed client (claude.ai) and is low-risk — it reuses the existing admin secret-mint + hash path.
- CIMD is **more secure and more future-proof**: no open registration endpoint (removes the "any stranger can mint client records" surface), no client secrets at rest, and identity bound to **domain ownership** (the metadata-URL origin must match the `client_id`). It is IETF-standardised, MCP-preferred and multi-vendor — so investing in it serves *every* future MCP client, not just one.
- Keeping DCR as the fallback means clients / AS setups without CIMD still work.

## Alternatives considered (and rejected)

- **Public-PKCE-only DCR (the v1 profile):** rejected — claude.ai (and the MCP confidential pattern) can't register; discovery also advertised confidential methods, contradicting the policy.
- **Confidential-only / drop public:** rejected — public PKCE clients are legitimate and must keep working.
- **Drop DCR, CIMD-only:** rejected — DCR is still needed as a fallback for clients/servers without CIMD; the vendors themselves keep DCR as fallback.
- **CIMD first (before confidential DCR):** rejected for sequencing — CIMD is a substantial build (custom OpenIddict integration for URL `client_id`s + an SSRF-hardened fetcher + caching); confidential-DCR unblocks the real client *now* while CIMD is designed.

## Consequences

- **Positive:** claude.ai / ChatGPT can self-register today; the forward path (CIMD) is the standardised, more secure one.
- **Security to watch:** DCR is an **open registration endpoint** (rate-limited + reserved-name-guarded, but still mints records on demand) — CIMD removes this surface. CIMD introduces a **new** surface: the server fetches a client-controlled URL → **SSRF must be hardened** (HTTPS-only; block private/loopback/link-local; no internal redirects; size + timeout limits; caching with TTL; origin-match validation).
- **Follow-up:** a dedicated ADR + design will cover the CIMD implementation (OpenIddict integration approach, the hardened fetcher, discovery field `client_id_metadata_document_supported: true`, caching/revocation, tests).

## References

- PR #68 `348a8d0` — confidential DCR + discovery `none`.
- Code: `Modgud.Application/Dcr/` (validator), `Modgud.Api/Features/Auth/OAuth/DcrRegistrationEndpoints.cs`, `Modgud.Infrastructure/OpenIddict/TokenEndpointAuthMethodsSupportedHandler.cs`.
- `docs/admin/dynamic-client-registration.md` — DCR v1 (2026-05).
- IETF `draft-ietf-oauth-client-id-metadata-document` (OAuth WG, adopted Oct 2025); MCP authorization spec (modelcontextprotocol.io).
