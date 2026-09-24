# Client ID Metadata Documents (CIMD)

**Client ID Metadata Documents** ([`draft-ietf-oauth-client-id-metadata-document`](https://datatracker.ietf.org/doc/draft-ietf-oauth-client-id-metadata-document/), adopted by the IETF OAuth WG) let a piece of software identify itself as an OAuth client by **publishing a metadata document at an HTTPS URL** — and using that URL *as* its `client_id`. The authorization server fetches and validates the document on demand. There is no registration request, no client secret, and no stored client record: the client's **metadata and display identity** are anchored to the HTTPS origin hosting the document. Most clients are public PKCE clients (`token_endpoint_auth_method: none`); a client that can hold a private key authenticates with `private_key_jwt` against the public keys its document points to — see [Confidential CIMD clients](#confidential-cimd-clients-private-key-jwt).

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

For the full end-to-end walkthrough — registering the MCP server, wiring discovery, connecting a real agent, and revoking access — see [Secure an MCP server with Modgud](/integrate/mcp-server).

## How a CIMD `client_id` is resolved

When a client presents an HTTPS-URL `client_id` that isn't a known stored client, and the realm has CIMD enabled, Modgud:

1. Validates the URL shape — `https` scheme, has a path, no fragment, no userinfo, no dot-segments.
2. Fetches it over an **SSRF-hardened** transport (see below), capped at **5 KB**, `Accept: application/json`, ~5 s timeout, **no redirects**.
3. Validates the document (table below).
4. Synthesizes an in-memory public PKCE client with a deterministic id derived from the URL, JWT access tokens, the redirect URIs the document declares, and the realm's **dynamic-client scopes** (see below) — narrowed to the document's `scope` when it has one.
5. Caches the validated document per URL, respecting `Cache-Control` (clamped to between 5 minutes and 24 hours; failures are never cached).

No database row is created. A refresh after the cache expires re-fetches and re-validates the live document — if the URL becomes unreachable, refresh fails and the client must re-authenticate.

::: tip Metadata refresh trade-offs
This is fail-closed by design: once a cache entry expires, an unreachable metadata URL fails the operation rather than serving the stale, previously-validated document (no stale-if-error fallback). A brief outage of the metadata host can therefore surface as failed authorizations or refreshes for that client until the document is reachable again. For an unauthenticated, self-asserted client, that's the intended trade-off — integrity over availability.
:::

## Opt-in design

CIMD shares DCR's resource/scope opt-in surface — flipping the master toggle does not, by itself, expose anything.

| Layer | Where | Default |
| --- | --- | --- |
| Realm master toggle | [Realm Settings → Client ID Metadata Documents](./realm-settings) tab | Off |
| Per-API allow-list | [OAuth APIs](./oauth-apis) → **Allow DCR** checkbox per row | Off |
| Per-Scope allow-list | [OAuth Scopes](./oauth-scopes) → **Allow DCR Clients** checkbox per row | Off |

A CIMD client must send a `resource=` parameter, and the target resource server must have **Allow DCR** enabled — otherwise the token endpoint rejects with `invalid_target`. The scopes it can hold and request are the realm's **dynamic-client scopes**: every scope with **Allow DCR Clients** ticked (app-scoped or not — a CIMD client is realm-wide and has no App link of its own) plus the standard scopes other than `modgud.management` (`openid`, `profile`, `email`, `phone`, `address`, `offline_access`, `roles`, `permissions`). Same rule as for [DCR clients](./dynamic-client-registration#how-the-per-scope-flag-interacts-with-app-scoped-scopes).

A document **without** `scope` — the normal case: Claude Code's document is one static file for every MCP server in the world and cannot know one server's scopes, the client reads them from the server's protected-resource metadata at run time — holds that whole set. A document **with** `scope` holds the intersection; it can narrow what its client may ask for, never widen it. The set is read live on every resolve, so ticking **Allow DCR Clients** on a scope takes effect at once, even for an already-cached document. Requesting a scope outside the set fails at `/connect/authorize` with `invalid_scope` and a description naming the scope and the flag.

## Enabling CIMD for a realm

1. **Realm Settings → Client ID Metadata Documents** → enable.
2. Set:
    - **Access-token lifetime** (default 15 min) — shorter than admin-created clients on purpose; a leaked token from an unverified, domain-bound client has a smaller blast radius.
    - **Refresh-token lifetime** (default 7 d).
3. **OAuth APIs → your MCP-server API** → tick **Allow DCR**.
4. **OAuth Scopes → the scope(s) the MCP server gates** → tick **Allow DCR Clients**.

## What's accepted in the metadata document

| Field | Rule |
| --- | --- |
| `client_id` | Required. Must string-equal the URL the server dereferenced (RFC 3986 §6.2.1 exact match). |
| `redirect_uris` | At least one usable. Usable means HTTPS, OR `http://localhost`, `http://127.0.0.1`, `http://[::1]`, without a fragment; other forms (private-use schemes such as `com.example.app:/cb`) are dropped from the client, not fatal. Exact-match at `/connect/authorize` — except the **port of a loopback URI**: a native client takes an ephemeral port at request time, so any port matches ([RFC 8252 §7.3](https://www.rfc-editor.org/rfc/rfc8252#section-7.3)), whether the document lists `http://localhost/callback` or `http://127.0.0.1:33418/` (as VS Code does). Scheme, host and path still have to match. |
| `application_type` | Optional, `web` or `native`. A document with a loopback `http` redirect URI is treated as `native` whatever it says — only a native app can have such a URI. Claude Code, Zed and goose omit the field and rely on this. |
| `token_endpoint_auth_method` | `none` (or omitted) — a public PKCE client; or `private_key_jwt` with exactly one of `jwks_uri` (https) or `jwks` — a confidential client. Shared-secret methods (`client_secret_basic`, `client_secret_post`, `client_secret_jwt`) and any `client_secret` field are rejected: a published document cannot keep a secret. |
| `grant_types` | Must include `authorization_code`. The document describes the client for every server, so grants Modgud does not offer (claude.ai lists `urn:ietf:params:oauth:grant-type:jwt-bearer`) are ignored; the client holds the intersection with `{authorization_code, refresh_token}`. |
| `response_types` | Must include `code`; other values are ignored. |
| `scope` | Optional, space-delimited. An **upper bound**: the client holds these scopes intersected with the realm's dynamic-client scopes. Omitted (as every static MCP-client document does), the client holds the whole set. |
| `client_name` | Optional. Used as the display name; the consent screen also shows the URL hostname regardless. |

A document that fails any rule is rejected and never cached; the authorize request fails as "unknown client". The rules reject only what Modgud cannot honour at all — values it merely does not offer are narrowed away, because the document describes the client for every authorization server. The test suite carries the live documents of claude.ai, Claude Code, VS Code, Zed and goose.

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

Like a DCR client, whether a CIMD client skips this screen on a remembered authorization follows [RFC 8252 §8.6](https://www.rfc-editor.org/rfc/rfc8252#section-8.6): assured identity (an `https` redirect on a non-loopback host, or a confidential client such as the `private_key_jwt` one below) may skip it, a public client redirecting to loopback or a private-use scheme sees it on every authorize. The authorization itself is reused for the same user, client and scope set either way, so the token's `oi_au_id` stays stable across re-consents.

## Confidential CIMD clients (`private_key_jwt`)

ChatGPT's connector document, for one, declares `token_endpoint_auth_method: private_key_jwt` and a `jwks_uri`. Such a client is **confidential**: the code exchange and every refresh must carry a client assertion ([RFC 7523](https://www.rfc-editor.org/rfc/rfc7523)) signed with a key from its published set, or the token endpoint answers `invalid_client`. PKCE still applies.

- **Where the keys come from.** An inline `jwks` is read from the document. A `jwks_uri` is fetched with the same SSRF protection as the document (below), up to 64 KB, and cached per its `Cache-Control` (5 minutes to 24 hours).
- **Rotation.** When an assertion names a `kid` the cached set lacks, Modgud fetches the set again right away — at most once a minute, so made-up key ids cannot turn it into a load generator against the client's host. Tokens already issued stay valid; the next refresh is checked against the new set.
- **What counts as a usable key.** Public RSA or EC keys for signing (`use` absent or `sig`). Other keys in the set — encryption keys, key types Modgud does not verify with — are skipped. A set that contains **private key material** is refused outright.
- **Audience.** The assertion's `aud` must be the realm's **issuer** (as in discovery), not the token endpoint — [draft-ietf-oauth-rfc7523bis §4](https://datatracker.ietf.org/doc/draft-ietf-oauth-rfc7523bis/), enforced since OpenIddict 7.

## Accepted risks

- **Targeted phishing via HTTPS redirect** — a domain owner can publish a document with `redirect_uri=https://attacker.example/grab`. The opt-in gates constrain which resources/scopes the client can reach; the consent hostname + `[unverified]` marker are the user-facing defence.
- **Availability coupling** — if the client's metadata URL is unreachable when a cache entry expires, refresh fails until it's back. This is inherent to fetch-on-demand registration.
- **Resource + scope targeting is the actual safety primitive** — like DCR, a CIMD client's token is audience-bound to a specific opted-in API and can only request opted-in scopes, so a grabbed code can't be replayed against unrelated APIs.
