# Access tokens: reference by default, per-client JWT opt-in, signed-not-encrypted

**Status:** Accepted — verified against current code 2026-06-13 (`Modgud.Infrastructure/OpenIddict/OpenIddictExtensions.cs`, `AccessTokenTypeHandler`) · **Decided:** 2026-05-29

## Context

OpenIddict can issue either opaque **reference** tokens (validated by introspection against the IdP) or self-contained **JWT** tokens (validated by the resource server itself via the published JWKS). Each has a place: reference = revocable + small + leaks nothing if logged; JWT = no per-call round-trip to the IdP, ideal for MCP/agent resource servers.

## Decision

- **Reference (opaque) access AND refresh tokens by default** (`UseReferenceAccessTokens()` / `UseReferenceRefreshTokens()`) — revocable and introspected through OpenIddict.
- **Per-client opt-in to JWT access tokens** (`AccessTokenType.Jwt`, applied by `AccessTokenTypeHandler`). **DCR / MCP clients default to JWT** — idiomatic for agent flows where the RS self-validates via JWKS rather than calling back to introspect (see ADR-0001).
- **Access-token encryption is disabled globally** (`DisableAccessTokenEncryption()`): JWT access tokens are **signed JWS, not JWE**, so any standard `JwtBearer` + discovery JWKS validates them without sharing an encryption key. Tokens remain **signed** (integrity/authenticity intact).

## Rationale

- A revocable, opaque token is the **safer default**; JWT is an explicit per-client opt-in for the cases that benefit (self-validation, no introspection latency).
- Signed-not-encrypted is required for the JWT case to be useful — an encrypted access token can't be validated by a generic RS without a shared key, defeating the purpose.

## Alternatives considered (and rejected)

- **JWT by default:** rejected — not revocable, larger, and leaks claims if logged; a poor default for the general client population.
- **Encrypted access tokens (JWE):** rejected — resource servers couldn't validate via the public JWKS.

## Consequences

- Resource servers built on `Modgud.Client.AspNetCore` / standard JwtBearer validate JWT-opted clients via JWKS; reference-token clients are introspected.
- Token revocation is immediate for reference tokens; JWT-opted tokens live until expiry (the DCR access-token lifetime is kept short to bound that window).

## References

- Code: `OpenIddictExtensions.cs` (`UseReferenceAccessTokens`, `DisableAccessTokenEncryption`), `AccessTokenTypeHandler`.
- `docs/concepts/tokens.md`; ADR-0001 (DCR/MCP clients default to JWT).
