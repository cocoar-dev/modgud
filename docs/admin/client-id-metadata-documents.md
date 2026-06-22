# Client ID Metadata Documents (CIMD)

**Client ID Metadata Documents** ([`draft-ietf-oauth-client-id-metadata-document`](https://datatracker.ietf.org/doc/draft-ietf-oauth-client-id-metadata-document/), adopted by the IETF OAuth WG) let a piece of software identify itself as an OAuth client by **publishing a metadata document at an HTTPS URL** — and using that URL *as* its `client_id`. The authorization server fetches and validates the document on demand. There is no registration request, no client secret, and no stored client record: the client's identity is bound to ownership of the domain hosting the document.

CIMD is the **MCP-preferred** client-onboarding path; both claude.ai and ChatGPT support it and fall back to [Dynamic Client Registration](./dynamic-client-registration) when a server doesn't advertise CIMD.

::: warning Off by default
Every realm starts with CIMD disabled. A CIMD `client_id` URL is not resolved (the authorize request fails as "unknown client"), and the discovery document omits `client_id_metadata_document_supported` — visitors can't tell whether the feature exists per realm.
:::

::: info CIMD vs DCR
[DCR](./dynamic-client-registration) mints and **stores** a client record from a `POST /connect/register`. CIMD stores **nothing**: the `client_id` URL is fetched, validated, and turned into an in-memory client for the duration of the flow. CIMD removes the open registration endpoint entirely; in exchange the server makes an outbound HTTPS request to a client-controlled URL, which is hardened against SSRF (see below).
:::

::: tip Per-Application override
The CIMD policy below is the **realm default**. An individual
[Application](./applications#application-settings) can override it per-app
(enable/disable, token lifetimes).
:::

## When to enable it

Enable CIMD when you want **AI agents you don't pre-trust** to attach to your MCP server using the modern, standardised path — without an administrator onboarding each one and without minting a stored record for every stranger.

Typical example: a user adds your MCP server to claude.ai. The MCP authorization flow goes:

1. Agent hits the MCP server with no token → 401 with `WWW-Authenticate: resource_metadata="…"`.
2. Agent fetches the protected-resource metadata, learns this realm is the auth server.
3. Agent fetches `/.well-known/oauth-authorization-server` → sees `client_id_metadata_document_supported: true`.
4. Agent uses its own published metadata URL (e.g. `https://claude.ai/.well-known/oauth-client`) **as the `client_id`** and runs Authorization-Code + PKCE with `resource=<mcp-server-url>`.
5. Modgud fetches that URL, validates the document, and issues an audience-bound access token.

## How a CIMD `client_id` is resolved

When a client presents an HTTPS-URL `client_id` that isn't a known stored client, and the realm has CIMD enabled, Modgud:

1. Validates the URL shape — `https` scheme, has a path, no fragment, no userinfo, no dot-segments.
2. Fetches it over an **SSRF-hardened** transport (see below), capped at **5 KB**, `Accept: application/json`, ~5 s timeout, **no redirects**.
3. Validates the document (table below).
4. Synthesizes an in-memory public PKCE client with a deterministic id derived from the URL, JWT access tokens, and the redirect URIs / scopes the document declares.
5. Caches the validated document per URL, respecting `Cache-Control` (clamped to between 5 minutes and 24 hours; failures are never cached).

No database row is created. A refresh after the cache expires re-fetches and re-validates the live document — if the URL becomes unreachable, refresh fails and the client must re-authenticate.

## Opt-in design

CIMD shares DCR's resource/scope opt-in surface — flipping the master toggle does not, by itself, expose anything.

| Layer | Where | Default |
| --- | --- | --- |
| Realm master toggle | [Realm Settings → Client ID Metadata Documents](./realm-settings) tab | Off |
| Per-API allow-list | [OAuth APIs](./oauth-apis) → **Allow DCR** checkbox per row | Off |
| Per-Scope allow-list | [OAuth Scopes](./oauth-scopes) → **Allow DCR Clients** checkbox per row | Off |

A CIMD client must send a `resource=` parameter, and the target resource server must have **Allow DCR** enabled — otherwise the token endpoint rejects with `invalid_target`. App-scoped scopes are reachable only when **Allow DCR Clients** is ticked, exactly as for DCR clients (a CIMD client is realm-wide and has no App link of its own). Global scopes (`openid`, `email`, `profile`, …) are always reachable.

## Enabling CIMD for a realm

1. **Realm Settings → Client ID Metadata Documents** → enable.
2. Set:
    - **Access-token lifetime** (default 15 min) — shorter than admin-created clients on purpose; a leaked token from an unverified, domain-bound client has a smaller blast radius.
    - **Refresh-token lifetime** (default 7 d).
3. **OAuth APIs → your MCP-server API** → tick **Allow DCR**.
4. **OAuth Scopes → the scope(s) the MCP server gates** → tick **Allow DCR Clients** (for app-scoped scopes).

## What's accepted in the metadata document

| Field | Rule |
| --- | --- |
| `client_id` | Required. Must string-equal the URL the server dereferenced (RFC 3986 §6.2.1 exact match). |
| `redirect_uris` | At least one. Each must be HTTPS, OR `http://localhost`, `http://127.0.0.1`, `http://[::1]`. No fragments. Exact-match at `/connect/authorize`. |
| `token_endpoint_auth_method` | `none` or omitted. **v1 is public-only** — a `client_secret*` method or any `client_secret` field is rejected. |
| `grant_types` | Subset of `{authorization_code, refresh_token}`; must include `authorization_code`. |
| `response_types` | Subset of `{code}`. |
| `scope` | Optional, space-delimited. The scopes the client may request (still subject to the opt-in gates above). |
| `client_name` | Optional. Used as the display name; the consent screen also shows the URL hostname regardless. |

A document that fails any rule is rejected and never cached; the authorize request fails as "unknown client".

## SSRF hardening

The server fetches a **client-controlled URL**, so the fetcher is locked down:

- **HTTPS only**, no redirects (a 30x to an internal host can't be followed).
- DNS is resolved by the fetcher, and the connection is pinned to the resolved IP — any **private, loopback, link-local, unique-local, CGNAT, multicast, or documentation** address is refused **at connect time**, closing the DNS-rebinding window.
- **5 KB** body cap, ~5 s timeout, `Accept: application/json`.

This is why CIMD is opt-in per realm: only enable it if you're comfortable with the realm making outbound HTTPS requests to client-supplied domains.

## Consent screen for CIMD clients

A CIMD client always reaches the explicit consent screen on first authorize, with two extra cues:

- The **`client_id` hostname** (e.g. `claude.ai`) shown prominently — the domain that owns the document. Verify it matches the app you intended, not just the self-asserted display name.
- An **`[unverified]`** marker + warning callout, the same treatment self-registered clients get.

## What's NOT in v1

- **`private_key_jwt`** — confidential CIMD clients (asymmetric client auth via a `jwks_uri` in the document). v1 is public PKCE only. Deferred to v2, which will also revoke on `jwks_uri` change.

## Accepted risks

- **Targeted phishing via HTTPS redirect** — a domain owner can publish a document with `redirect_uri=https://attacker.example/grab`. The opt-in gates constrain which resources/scopes the client can reach; the consent hostname + `[unverified]` marker are the user-facing defence.
- **Availability coupling** — if the client's metadata URL is unreachable when a cache entry expires, refresh fails until it's back. This is inherent to fetch-on-demand registration.
- **Resource + scope targeting is the actual safety primitive** — like DCR, a CIMD client's token is audience-bound to a specific opted-in API and can only request opted-in scopes, so a grabbed code can't be replayed against unrelated APIs.
