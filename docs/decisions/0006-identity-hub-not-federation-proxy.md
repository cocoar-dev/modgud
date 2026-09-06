# Identity Hub, not Federation Proxy

**Status:** Accepted design stance — the implementation is verified against current code 2026-06-13 (`ExternalLoginProcessor.cs`). Revisitable (it has come up as an open strategic question). · **Decided:** 2026-04-29

## Context

Modgud can authenticate users against external IdPs (OIDC, SAML). Two fundamentally different ways to be that "in the middle" service:
- **Identity Hub:** treat the external login as *one way to prove who the user is*, then represent that user as a **local** principal and issue Modgud's own tokens.
- **Federation Proxy:** a thin pass-through that forwards the upstream IdP's tokens/claims (largely verbatim) to the consuming app.

## Decision

**Modgud is strictly an Identity Hub.** Verified in `ExternalLoginProcessor`: on an external login it **maps the external identity to an existing Modgud user (by link or, carefully, by email) or creates one JIT**, runs the user-update script to patch local properties, emits the events that keep the principal directory + link aggregates in sync, and returns the **local** claims principal the finish-endpoint signs in (`SignInAsync(ApplicationScheme, principal)`). The consuming app always receives **Modgud-issued** tokens with **Modgud's** claims/groups/roles/permissions — upstream claims are *not* passed through verbatim.

## Rationale

- **One issuer for the consuming app** — integrate with Modgud only, not N upstreams' differing claim shapes.
- **Uniform authorization** — Modgud's groups/roles/permissions (ADR-0005) apply regardless of login method.
- **Stable identity** — one local principal even as external providers are linked/unlinked.

## Alternatives considered (and rejected)

- **Thin federation proxy (pass-through upstream tokens/claims):** rejected — couples every consuming app to each upstream's claim format, bypasses Modgud's authorization, and makes identity unstable across providers.

## Consequences

- External-auth work centres on **mapping** external claims → local principal (+ optional auto-membership scripts), not relaying tokens.
- "Pass the upstream IdP's claims straight through" is a deliberate non-feature.

## References

- Code (verified 2026-06-13): `Modgud.Authentication/Api/ExternalAuth/ExternalLoginProcessor.cs`. ADR-0005 (the local authorization model applied post-mapping).
