# Dynamic Client Registration (RFC 7591) for MCP clients

> **Status:** v1 design locked in, ready to implement. Captured
> 2026-05-07, design consolidated 2026-05-12, sharpened after
> external review 2026-05-12.
> **Why:** The MCP authorization spec (revision 2025-06-18) expects
> a generic AI agent (Claude Code, Cursor, Continue, claude.ai,
> hosted IDEs, …) to be able to attach to a public-internet MCP
> server with no out-of-band setup beyond the user pasting the MCP
> URL. Without DCR, every agent instance has to be pre-registered
> as an OAuth client by an admin — practical for one internal
> pilot, painful for "anyone with an agent connects". First
> concrete trigger is `cocoar-policy` wanting `auth.cocoar.dev` as
> its IdP.
> **Prereqs:** Resource Indicators (RFC 8707) ✅ shipped via
> `ResourceIndicatorHandler.cs`. PKCE + JWT-formatted access tokens
> + refresh-token rotation: all live. Self-Registration
> infrastructure (RealmSettings tab surface, per-realm settings
> doc) shipped 2026-05-12 — DCR plugs into the same shape.

## What DCR is — and what it isn't

Sharing the word "register" with two unrelated concepts. Be careful.

| | **User Registration** | **Dynamic Client Registration (DCR)** |
|---|---|---|
| What gets registered | A **person** (account in the IdP) | A piece of **software** (OAuth client) |
| Who triggers | End-user on a sign-up form | Anonymous program via JSON POST |
| Standardised | Not standardised | RFC 7591 |
| Trust model | "new user is allowed to self-onboard" | "any software is allowed to register itself as a client" |
| Use-case | "Acme Corp employee creates a Cocoar account" | "Claude Code attaches to a public MCP server" |
| Cocoar.auth status | Shipped 2026-05-12 (see `/admin/realm-settings`) | This page (v1 designed) |

## What MCP clients require

From the MCP authorization spec, revision 2025-06-18:

1. Client hits the MCP server without a token.
2. Server returns 401 with
   `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource"`.
3. Client fetches that JSON, learns the **authorization server URL** (= our IdP).
4. Client fetches our `/.well-known/oauth-authorization-server` (RFC 8414).
5. **If `registration_endpoint` is in there**: client `POST`s to it and gets back a `client_id`.
6. Client kicks off Authorization Code + PKCE with `resource=<mcp-server-url>` (RFC 8707).
7. User authenticates at our IdP, redirected back with code.
8. Client exchanges code for an access token (audience-bound to the MCP server).

Note: `/.well-known/oauth-protected-resource` (RFC 9728) lives on the **MCP server** side, not the IdP. MCP-server vendors are responsible for emitting it — our role is just to publish a discoverable `registration_endpoint` from the AS-side discovery doc.

## v1 design (locked 2026-05-12)

### Scope

- **MCP-flavoured DCR**, not full RFC 7591. Public PKCE clients only — no `client_secret` issued, no `client_credentials`/`implicit`/`password` grants.
- **Public-internet MCP** is the target. Redirect URIs are HTTPS or loopback HTTP only.
- **Per-realm and per-resource and per-scope opt-in** — anonymous DCR is gated three times: a realm-level master toggle, a per-`OAuthApi.AllowDynamicRegistration` flag (resource-target containment), and a per-`OAuthScope.AllowDynamicRegistrationClients` flag (capability containment).

### Toggle location

| Surface | Where | Behaviour |
| --- | --- | --- |
| Master toggle | `Realm Settings → Dynamic Client Registration` tab | Off by default. Holds TTL, rate-limits, token-lifetimes, reserved-names list. |
| Per-API allow-list | `OAuth APIs → AllowDynamicRegistration` checkbox per row | Off by default. Off APIs are not valid DCR resource targets. |
| Per-Scope allow-list | `OAuth Scopes → AllowDynamicRegistrationClients` checkbox per row | Off by default. DCR clients can only request scopes with this flag set. Prevents a registered client from asking for `tenant.admin.*` and relying on the user's habit-click consent. |

### Validation rules on `/connect/register`

The endpoint is plumbing from OpenIddict; the policy layer is custom and rejects with `invalid_client_metadata` (RFC 7591) when violated.

| Rule | Default | Rationale |
| --- | --- | --- |
| `token_endpoint_auth_method` must be `none` | enforced | Public PKCE-only — no secret storage problem |
| `grant_types ⊆ {authorization_code, refresh_token}` | enforced | Hostile registrants can't get client_credentials tokens |
| `response_types ⊆ {code}` | enforced | Implicit / hybrid flows are out |
| PKCE mandatory | already global | Code-grab without verifier doesn't reach the token |
| Refresh-token rotation hard-pinned on | enforced | OpenIddict's `UseRollingRefreshTokens` is non-overridable for DCR clients regardless of realm config |
| `redirect_uris` — every URI must be HTTPS, OR `http://localhost`, OR `http://127.0.0.1`, OR `http://[::1]` (IPv6 loopback) | enforced | HTTP only on loopback (RFC 8252 §7.3). HTTPS everywhere else. Custom URI schemes (`com.example.app://`) explicitly rejected in v1 with a clear `invalid_redirect_uri` error message pointing vendors to the allowed forms. |
| At least one redirect_uri | enforced | Empty list is silent breakage |
| `client_name` length ≤ 80 chars | enforced | Display-area limit + cuts a spoofing surface |
| `client_name` ASCII + Latin-1 only, after NFKC normalisation | enforced | Blocks zero-width-joiners, confusables, RTL-overrides. v1 is whitelist-based; ICU `uspoof` for a richer detection algorithm is a v2 add-on. |
| `client_name` does not match any realm-configured reserved-name pattern (case-insensitive substring after NFKC) | enforced | Realm-admin maintains the list in the DCR tab: "Cocoar", "Cocoar Auth Official", any tenant trademarks. Stops "Cl0ude Desktop"-style brand impersonation at the registration door. |
| Per-IP rate-limit | 5/h | In-memory limiter (same pattern as `RegistrationRateLimiter`) |
| Per-realm rate-limit | 100/d | Cap storage growth |

### Token-lifetime overrides for DCR clients

DCR-issued clients get tighter defaults than admin-registered clients, set in the realm DCR settings block:

| Token | Default for DCR | Configurable |
| --- | --- | --- |
| Access Token | 15 min | per-realm override |
| Refresh Token | 7 days, rotating | per-realm override |

Shorter blast radius if a token leaks. Admin can raise per-realm if a vendor's use case warrants it.

### Resource Indicator binding (RFC 8707)

- The `/connect/token` exchange MUST include `resource=<https-uri>`.
- The URI must match an `OAuthApi` in the realm with `AllowDynamicRegistration=true`.
- HTTPS-URI form mandatory (`resource=https://mcp.cocoar.dev`) — strict RFC 8707 §2 conformance, easier client tooling, fewer string-match foot-guns. Bare-name resources are reserved for non-DCR/internal flows.
- This is the core defence: **DCR-issued client → constrained audience**. A malicious DCR client can't get tokens targeted at unrelated realm APIs.

### Token persistence

`OpenIddictApplication` gets four new fields (Marten JSONB columns, no schema migration):

- `IsDynamicallyRegistered: bool` — flagged at creation
- `RegisteredAt: DateTimeOffset`
- `RegisteredFromIp: string?`
- `LastUsedAt: DateTimeOffset?` — updated on each token-issue

### Consent screen

For DCR clients (`IsDynamicallyRegistered=true`), the consent page renders:

- `[unverified]` marker next to the displayed `client_name`
- Short warning line: "This app registered itself — verify the name carefully before authorizing."
- All other content unchanged.

This is the user-facing safety net. DCR creates a stub; the actual code-grab attempt happens at `/connect/authorize`, and the consent decision is the gate. Brand-impersonation defense is the warning text PLUS the reserved-names rejection at registration time — neither alone is sufficient.

### Auto-cleanup

A background `BackgroundService` runs daily, soft-deletes DCR clients where `LastUsedAt < (now - TTL)`. TTL is 90 days default, configurable per realm.

Soft-delete keeps the audit-log entries intact and preserves the `client_id` history for forensics.

### Audit-log events

Five DCR-specific event types in the standard auth-log:

| Event | When |
| --- | --- |
| `DcrClientRegistered` | Successful registration. Fields: IP, Realm, redirect_uris, client_name, client_id, requested grants/scopes, UA |
| `DcrClientFirstUsed` | First `/connect/authorize` invocation for the new client_id. Cleanest signal for "registration was real, not just bot-noise". |
| `DcrClientGarbageCollected` | GC sweep soft-deleted it. Fields: client_id, registeredAt, lastUsedAt, TTL applied |
| `DcrRegistrationRejected` | Validation rejected the request. Fields: IP, Realm, attempted client_name, reason (`reserved-name-conflict`, `redirect-uri-invalid`, `rate-limit`, `client-name-spoof-pattern`, …). Gold for threat-hunting. |
| `DcrRateLimitTriggered` | Per-IP or per-realm limit hit. Fields: IP, Realm, current-window count |

Visible in the standard Auth-Log grid with new event-type filters. The OAuth-Clients grid additionally surfaces DCR clients with a `DCR` column + "Show DCR-only" filter chip; clicking a DCR client opens the regular detail modal with an additional "Registration Info" tab (IP, timestamp, last-used, source UA).

### Discovery document

Adds, when the realm-level master toggle is on:

```json
{
  "registration_endpoint": "https://auth.cocoar.dev/connect/register",
  "code_challenge_methods_supported": ["S256"]
}
```

The first wave of tests should verify both: the endpoint is reachable AND it shows up in the OAuth-Authorization-Server-Discovery document.

## What's NOT in v1 (with explicit migration anchors)

| Feature | Why deferred | Add-on path |
| --- | --- | --- |
| `software_statement` validation (RFC 7591 §2.3) | No concrete known agent-vendor with a stable signing key today. | New `RealmSettings.DcrSoftwareStatementIssuers` list. When set, valid `software_statement` JWTs replace `[unverified]` with `[verified by Anthropic]`/etc. Complements (doesn't replace) the reserved-names blocklist. |
| Initial Access Token (RFC 7591 §3.1) | Defeats anonymous-self-onboarding which is the entire point. Useful for paranoid realms. | New `RealmSettings.DcrRequiresInitialAccessToken` per-realm flag. Admin generates token in UI, hands it to the registrant out-of-band. |
| Approval workflow (`pending_admin_review`) | Latency defeats the "agent attaches without admin involvement" use case. | New per-realm toggle. DCR clients land in `pending` state; admin reviews in a queue UI. |
| Confidential DCR clients | Secret-storage anti-pattern for unknown software. | Only via `software_statement` path — never anonymous. |
| RFC 7592 Client Configuration Endpoint (`GET`/`PUT`/`DELETE /connect/register/{id}` with Registration Access Token) | Re-registration is a valid v1 strategy — vendors that need to update metadata just register a fresh client. | New endpoint + `RegistrationAccessToken` field on the client doc, issued at registration time. Mostly additive. |
| Custom URI schemes (`com.example.app://callback`) | RFC 8252 §7.1 lists them as legitimate for native apps, but they're hard to validate (no DNS anchor). Desktop MCP clients today predominantly use loopback HTTP. | Add a per-realm allow-list of reverse-domain schemes (regex `[a-z]+(\.[a-z0-9-]+){1,}://`). Trigger: first concrete vendor request. |
| ICU `uspoof`-based confusables detection | NFKC + Latin-1 whitelist covers the obvious attacks; full confusables-detection adds a heavier dependency. | Add when v1 telemetry shows real-world spoofing attempts that slip past the basic whitelist. |

## Effort

- OpenIddict endpoint enablement + custom validation handler (incl. client_name spoofing rules + reserved-names check + redirect-uri policy): 1d
- Realm-Settings tab + OAuthApi-flag + OAuthScope-flag + Resource-binding enforcement + Reserved-names admin UI: 1d
- Rate-limit + audit-log event types (5 new): 0.5d
- Client-doc fields + last-used tracking + token-lifetime overrides: 0.5d
- Consent-screen `[unverified]` marker + warning text: 0.5d
- GC background service: 0.5d
- Admin UI (OAuth-Clients-Grid filter + "Registration Info" tab + Auth-Log event filters): 1d
- Tests + docs: 1d

**Total: 6-7 days** for v1.

(Up from 5d after absorbing the external-review feedback: scope-containment + name-spoofing + reserved-names-list + IPv6-loopback + tighter token TTLs + expanded audit events.)

## Risks accepted in v1 design

- **Brand impersonation via creative `client_name`** — the realm-configured reserved-names blocklist catches direct hits ("Cocoar"), NFKC normalisation catches zero-width-joiner and homoglyph tricks within Latin-1. Sophisticated lookalikes that pass NFKC + don't match a configured term still slip through. Mitigated only by the consent-screen `[unverified]` marker. Strong defence if the user actually pauses at consent, near-zero if they habit-click. We accept this for v1.
- **Targeted phishing via HTTPS redirect** — attacker registers a client with `redirect_uri=https://attacker.example/grab`, then social-engineers a specific user to click through. The per-OAuthApi + per-OAuthScope flags constrain which resources/capabilities are reachable, the `[unverified]` marker warns at consent. No further filtering in v1.
- **Resource + scope targeting is the actual safety primitive** — a DCR client's token is audience-bound to a specific opted-in API AND can only request opted-in scopes. Even if a code is grabbed, the resulting token can't be replayed against unrelated APIs and can't carry high-trust scopes.
- **First-use latency for legitimate vendors** — when the realm-admin has the reserved-names list configured "Anthropic" but a real Anthropic agent shows up, it's rejected. The fix is the `software_statement` add-on path. Until then, real vendors with reserved names hit the same wall as impersonators — that's the cost of the blocklist living per-realm.

## Trigger

`cocoar-policy` (sibling SaaS, MCP server for policy evaluation + knowledge-document storage) wants `auth.cocoar.dev` as its IdP. The DCR ask was raised in `.local/idp-requirements-for-mcp-auth.md` (2026-05-07). The team identified DCR as a "blocker for 'any agent attaches'" — they accept that the v1 integration can work with one pre-registered client (Bernhard's Claude Code), but beyond that demands DCR.

## Related

- The first MCP integration (cocoar-policy with one pre-registered client) can ship without DCR. We test the rest of the flow end-to-end with that pilot, then decide whether DCR is actually the next bottleneck.
- Self-registration of **users** is a separate concept and shipped 2026-05-12 (`/admin/realm-settings`).
