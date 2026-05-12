# Dynamic Client Registration

**Dynamic Client Registration** (DCR, [RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)) lets a piece of software register itself as an OAuth client without an administrator pre-provisioning it. Cocoar.Auth ships an MCP-flavoured subset focused on **AI agents and MCP servers**: public PKCE clients only, no client secrets, audience-bound tokens.

::: warning Off by default
Every realm starts with DCR disabled. The `POST /connect/register` endpoint refuses requests, and the discovery document omits `registration_endpoint` — visitors can't tell whether the feature exists per realm.
:::

::: info Different from User Self-Registration
DCR registers **software** (an OAuth client). [Self-Registration](./realm-settings#self-registration) registers **people** (user accounts). Two unrelated concepts sharing the word "register".
:::

## When to enable it

Enable DCR when **you want AI agents you don't pre-trust** to be able to attach to your MCP server (or other OAuth-protected API) without an administrator walking each one through client creation.

Typical example: a user pastes your MCP server's URL into Claude Code, Cursor, Continue, or claude.ai. The MCP-spec authorization flow goes:

1. Agent hits the MCP server with no token → 401 with `WWW-Authenticate: resource_metadata="…"`.
2. Agent fetches the protected-resource metadata, learns this realm is the auth server.
3. Agent fetches `/.well-known/oauth-authorization-server` → sees `registration_endpoint`.
4. Agent `POST`s its name + redirect URI to `/connect/register` → gets back a `client_id`.
5. Agent runs Authorization-Code + PKCE with `resource=<mcp-server-url>` → audience-bound access token.

Without DCR, step 4 isn't possible and every agent has to be onboarded manually. With one pre-registered client an admin can pilot the integration, but "anyone with an agent attaches" needs DCR.

## Triple opt-in design

Anonymous registration is gated **three times**. All three must be on for a DCR-registered client to be able to mint usable tokens.

| Layer | Where | Default |
| --- | --- | --- |
| Realm master toggle | [Realm Settings → Dynamic Client Registration](./realm-settings) tab | Off |
| Per-API allow-list | [OAuth APIs](./oauth-apis) → **Allow DCR** checkbox per row | Off |
| Per-Scope allow-list | [OAuth Scopes](./oauth-scopes) → **Allow DCR Clients** checkbox per row | Off |

The master toggle just turns the registration endpoint on. The per-API flag controls which resource servers a DCR client can target with `resource=`. The per-scope flag controls which scopes a DCR client can ever request. A DCR-registered client that asks for `tenant:admin:*` and a non-opted-in API is rejected at the token endpoint with `invalid_target`.

## Enabling DCR for a realm

1. **Realm Settings → Dynamic Client Registration** → enable.
2. Set:
    - **Access-token lifetime** (default 15 min) — shorter than admin-created clients on purpose; a leaked token has a smaller blast radius.
    - **Refresh-token lifetime** (default 7 d). Rotation is global-on at the server level.
    - **GC TTL** (default 90 d) — unused DCR clients get soft-deleted after this.
    - **Per-IP rate-limit** (default 5/h), **Per-realm rate-limit** (default 100/d) — caps spray.
    - **Reserved names** — substring blocklist for `client_name`. NFKC-normalised + case-insensitive. Use it for your own trademark plus anything you don't want impersonated ("Cocoar", "Anthropic", …).
3. **OAuth APIs → your MCP-server API** → tick **Allow DCR**.
4. **OAuth Scopes → the scope(s) the MCP server gates** → tick **Allow DCR Clients**.

After these four steps, an agent that POSTs to `/connect/register` with a valid payload gets a `client_id` back and can complete the full auth-code + PKCE flow against your opted-in API.

## What's accepted at `/connect/register`

| Field | Rule |
| --- | --- |
| `redirect_uris` | At least one. Each must be HTTPS, OR `http://localhost`, `http://127.0.0.1`, `http://[::1]`. No custom URI schemes (`com.example.app://`). No fragments. |
| `client_name` | Required. ≤ 80 chars. ASCII / Latin-1 only after NFKC normalisation. Must not match a substring on the realm's reserved-names list (case-insensitive). |
| `token_endpoint_auth_method` | Must be `none` (or omitted). Public PKCE only — no secret-storage. |
| `grant_types` | Subset of `{authorization_code, refresh_token}`. |
| `response_types` | Subset of `{code}`. No implicit / hybrid flows. |

On success the endpoint returns `201 Created` with the assigned `client_id` per RFC 7591 §3.2.1. On rejection it returns `400 Bad Request` with `{ error, error_description }` per §3.2.2. Hitting the rate-limit returns `429`.

## Consent screen for DCR clients

DCR-registered clients always go through the explicit consent screen, with two extra cues:

- **`[unverified]`** marker next to the client name.
- Warning callout: *"This app registered itself — verify the name carefully before authorizing."*

`AllowRememberConsent` is forced off for DCR clients, so the consent prompt appears on every authorize hit until the user actively trusts the app at the client end (typical pattern: the agent caches its own consent decision).

## Audit log

Every DCR-related event lands in the auth log with a `DCR ` prefix; the [Auth Log](./auth-log) grid has a **"DCR events only"** filter chip.

| Event | When |
| --- | --- |
| `DCR client registered` | Successful registration. Fields: IP, Realm, ClientId, ClientName. |
| `DCR registration rejected` | Validation rejected. Fields: IP, Reason (`MissingRedirectUri`, `InvalidRedirectUri`, `ClientNameReservedName`, …), ClientName. |
| `DCR rate-limit triggered` | Per-IP or per-realm cap hit. |
| `DCR client first used` | First successful token-issue for the new `client_id`. Cleanest signal that the registration was real, not bot noise. |
| `DCR client garbage collected` | GC sweep soft-deleted a stale client. Fields: ClientId, RegisteredAt, LastUsedAt, TtlDays. |

## Managing DCR-registered clients

The standard [OAuth Clients](./oauth-clients) grid carries a **DCR** column (●) and a **"DCR only"** filter chip. Clicking a DCR client opens the regular detail modal with an additional **Registration Info** tab showing:

- Registration timestamp (UTC)
- Source IP at registration time
- Last successful token-issue timestamp

You can delete a DCR client like any other — useful if a name slipped past the reserved-names list. The garbage collector also sweeps inactive ones automatically (default 90 days since last token issue).

## What's NOT in v1

Deferred features with clearly-defined add-on paths:

- **`software_statement` (RFC 7591 §2.3)** — vendor-signed JWT that replaces `[unverified]` with `[verified by Anthropic]` etc. Add when a real vendor publishes a stable signing key.
- **Initial Access Token (RFC 7591 §3.1)** — admin-issued token required to register. Useful for paranoid realms; defeats the "agent attaches without admin involvement" use case.
- **Approval workflow** — DCR clients land in `pending` until admin reviews.
- **RFC 7592 management endpoints** — `GET/PUT/DELETE /connect/register/{id}` for updating already-registered clients. Re-registration is the v1 strategy.
- **Custom URI schemes** — `com.example.app://callback` for native apps.

## Accepted risks

- **Brand impersonation via creative `client_name`** — the reserved-names list catches direct hits, NFKC + Latin-1 catches lookalikes within Latin-1, but a sophisticated lookalike that doesn't match a configured term still passes. The `[unverified]` marker is the final defence, and it relies on the user actually pausing at consent.
- **Targeted phishing via HTTPS redirect** — attacker registers a client with `redirect_uri=https://attacker.example/grab`, then social-engineers a specific user to click through. The triple-opt-in constrains *which* resources/capabilities they can reach; the consent marker warns the user; no further filtering in v1.
- **Resource + scope targeting is the actual safety primitive** — a DCR client's token is audience-bound to a specific opted-in API AND can only request opted-in scopes. Even if a code is grabbed, the resulting token can't be replayed against unrelated APIs and can't carry high-trust scopes.
