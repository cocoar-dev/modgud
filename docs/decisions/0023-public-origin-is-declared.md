# The public origin is declared, not derived

**Status:** Accepted — shipped 2026-09-03 (PR #214) · **Decided:** 2026-09-03 · **Supersedes:** [ADR 0002](./0002-public-origin-derived)

## Context

[ADR 0002](./0002-public-origin-derived) decided the opposite: a realm's public origin should never be configured, only derived from the incoming request. The reasoning was sound at the time — a configured origin is a value that can be wrong, and a derived one cannot drift from reality.

Deriving it turned out to be the thing that drifts. `Realm.PrimaryDomain` is a **host name**, and it has to stay one: it doubles as the WebAuthn RP ID and as the cookie domain, and neither permits a scheme or a port. So the origin had to be reconstructed as `https://{PrimaryDomain}` on every use. That reconstruction is a guess, and it is wrong in every deployment that is not a plain HTTPS reverse proxy on the default port — a development host on `http://localhost:4300`, an installation reached on a non-standard port, anything where the scheme is not `https`.

The failure mode is quiet and unpleasant: magic links, verification mails and installation links are all built against the origin, and a wrong origin produces a link that looks right and lands nowhere. Passkeys make it worse, because the accepted WebAuthn origin has to match the one the browser actually presents.

## Decision

The public origin is **declared** as its own field.

- `Realm.PublicBaseUrl` holds the absolute base URL users actually reach the realm at (`https://auth.example.com`, `http://localhost:4300`), without a trailing slash. Every outbound user-facing link is built against it, and it is an accepted WebAuthn origin.
- `PrimaryDomain` stays a bare host name and keeps its two jobs, RP ID and cookie domain. The two values are no longer derived from one another.
- First installation records the very origin its installation link was issued for, so the common case needs no configuration at all.
- `recover realm-set-public-url` changes it afterwards, which is what a deployment that moves behind a different proxy needs.
- `PublicBaseUrl` is nullable. Null — every realm created before the field existed — falls back to `https://{PrimaryDomain}`, the behaviour ADR 0002 specified. Nothing had to be migrated.

## Consequences

- The value can now be wrong, which was ADR 0002's objection. It is mitigated rather than dismissed: the value is captured automatically at installation from the origin that demonstrably worked, and there is one explicit command to correct it.
- Non-HTTPS and non-default-port deployments are supported without special cases in link building.
- The invariant "the WebAuthn RP ID is a registrable suffix of the origin's host" is now checkable, because both halves exist as separate, explicit values.
- ADR 0002 is kept as the historical record rather than deleted. It documents an argument that was reasonable and that the deployment reality overturned; that is worth reading, not erasing.
