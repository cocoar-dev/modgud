# Dynamic Client Registration (RFC 7591) for MCP clients

> **Status:** v1 design locked in, **ready to implement (7-8 days)**.
> The consent-UI prerequisite — flagged during the codebase-reality
> check — was built and shipped 2026-05-12 (commit `9090007`), so
> nothing is blocking anymore. Captured 2026-05-07, design
> consolidated 2026-05-12, sharpened after external review same
> day, codebase-reality-check + prereq-shipped 2026-05-12,
> OpenIddict-fact-check 2026-05-12.
>
> **OpenIddict has NO built-in `/connect/register` endpoint.** Earlier
> drafts of this note (and the reality-check pass) assumed OpenIddict 7
> shipped a DCR endpoint that just needed to be toggled on. False —
> OpenIddict's server endpoint list is Authorization, Configuration,
> DeviceAuthorization, EndSession, Introspection, JsonWebKeySet,
> PushedAuthorization, Revocation, Token, UserInfo, EndUserVerification
> — no ClientRegistration. By design: OpenIddict is policy-free and DCR
> is policy-heavy. **We build the `/connect/register` endpoint
> ourselves**, Minimal-API style, just like `OAuthApplicationEndpoints`
> and `RealmSettingsEndpoints`. See "Implementation approach" below.
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
> **Consent UI prereq:** ✅ shipped 2026-05-12. New
> `ConsentView.vue` at `/consent?ticket=X` renders the
> server-side-ticket flow's request, both approve and deny verified
> end-to-end against `demo-mobile` (the only existing
> `ConsentType=explicit` client). The `[unverified]`-marker work
> for DCR clients now just adds a conditional render branch + a
> small backend response-shape extension; see the Consent screen
> section below.

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

## Implementation approach (Minimal-API, not OpenIddict-built-in)

Since OpenIddict 7 has no `/connect/register` endpoint and no
`SetRegistrationEndpointUris()` option, we build the endpoint
ourselves — Minimal-API style, matching the existing OAuth admin
endpoints (`OAuthApplicationEndpoints`, `RealmSettingsEndpoints`).
This is not a workaround, it's the right approach: OpenIddict is
deliberately policy-free, and DCR is policy-heavy.

What the custom path looks like:

- **Endpoint:** `app.MapPost("/connect/register", …)` mounted in the
  tenant-scoped request pipeline. No need for a per-tenant 404 filter
  upstream — the Realm middleware already gates everything to the
  resolved realm. Per-realm enable/disable is just a check on the
  `RealmSettings.Dcr.Enabled` flag inside the handler.
- **Validation:** a normal `IDcrRegistrationValidator` service that
  takes the request body, the resolved realm, and the realm settings.
  Returns either `Validated(NormalizedRequest)` or
  `Invalid(errorCode, errorDescription)`. Easy to unit-test, no
  OpenIddict-event-handler-order gymnastics (see
  `feedback_openiddict_handler_order.md` — exactly the kind of issue
  this avoids).
- **Persistence:** call `OAuthAdminService.CreateAsync` (existing
  service that wraps the event-sourced `OAuthApplicationAggregate`)
  with the validated metadata. The DCR-specific Properties-dict keys
  (`cocoar:dcr:is_dynamically_registered` etc.) ride along in the
  same call. Same write path as admin-created clients.
- **Discovery:** a new custom handler analogous to
  `RealmScopesSupportedHandler` — hooks
  `HandleConfigurationRequestContext`, adds `registration_endpoint`
  to the response when the realm has DCR enabled. Tenant-scoped via
  `IDocumentSession`. Same pattern that's already serving
  `scopes_supported`.
- **Response:** RFC 7591 §3.2.1 — 201 Created with JSON body
  containing `client_id`, `client_id_issued_at`, plus the echoed
  client metadata (sanitized). No `registration_access_token` /
  `registration_client_uri` in v1 (RFC 7592 management is
  out-of-scope, see the deferred list).
- **Errors:** RFC 7591 §3.2.2 — 400 with `{ error, error_description }`.
  Same shape MCP clients expect.

What we explicitly skip from "what-OpenIddict-would-give-us":

- The pipeline-event model — we don't need ProcessRegistrationContext
  etc. because we own the endpoint. Validation runs once, deterministically.
- Built-in Discovery-doc auto-injection — replaced by our own custom
  handler (which we'd write anyway for tenant-scoped Discovery
  filtering, see existing `RealmScopesSupportedHandler`).

Effort impact: roughly net-zero. Building the endpoint by hand costs
~0.5d more than toggling an OpenIddict option would have, but we save
the same ~0.5d on the per-tenant 404 filter (which is no longer needed,
since gating is intrinsic to the tenant-scoped route). Total v1 stays
at 7-8 days.

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
| Refresh-token rotation is globally on (Cocoar default) | enforced server-wide | OpenIddict's `EnableRollingRefreshTokens()` is a server-config switch, not per-client. The plan ensures the admin UI does not offer a per-client opt-out for DCR clients — the global default applies. |
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

**Implementation caveat:** the existing `ResourceIndicatorHandler`
validates that the requested `resource` is one the principal would
otherwise have been granted via scope-binding — it does **not**
know about the `AllowDynamicRegistration` flag on `OAuthApi`. The
DCR work needs to extend it (or add a sibling handler running
after it) with a second check: if the issuing client carries
`cocoar:dcr:is_dynamically_registered=true`, the resolved
`OAuthApi` MUST have `AllowDynamicRegistration=true`, else
`invalid_target`. The lookup adds one tenant-DB read per
token-issue for DCR-issued tokens only.

### Token persistence

OAuth clients in Cocoar.Auth are stored as event-sourced
aggregates (`OAuthApplicationAggregate` → `OAuthApplicationState`),
not plain JSONB docs. The DCR metadata uses the existing
`OAuthApplicationState.Properties` dict — same pattern that
`ScopePropertyKeys` already uses on the scope side. No new
aggregate events, no schema migration, no projection rebuild.

New constants in a new `Cocoar.Auth.Domain/OAuth/Applications/ApplicationPropertyKeys.cs`:

- `cocoar:dcr:is_dynamically_registered` (bool)
- `cocoar:dcr:registered_at` (ISO-8601 string)
- `cocoar:dcr:registered_from_ip` (string)
- `cocoar:dcr:last_used_at` (ISO-8601 string, updated on each token-issue)

The registration handler writes these on creation; the
`/connect/token` handler reads + updates `last_used_at` on each
successful issue (cheap — already a write path).

### Consent screen

For DCR clients (`IsDynamicallyRegistered=true`), the consent page renders:

- `[unverified]` marker next to the displayed `client_name`
- Short warning line: "This app registered itself — verify the name carefully before authorizing."
- All other content unchanged.

This is the user-facing safety net. DCR creates a stub; the actual code-grab attempt happens at `/connect/authorize`, and the consent decision is the gate. Brand-impersonation defense is the warning text PLUS the reserved-names rejection at registration time — neither alone is sufficient.

The consent UI itself was built and shipped 2026-05-12 (commit `9090007`) — `ConsentView.vue` at `/consent?ticket=X` renders the existing server-side-ticket flow's request. The DCR-specific work here is just the `[unverified]` conditional + a small backend response-shape extension; see [DCR-specific backend touch-up for the consent screen](#dcr-specific-backend-touch-up-for-the-consent-screen) below.

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

## DCR-specific backend touch-up for the consent screen

The shipped consent flow surfaces `ClientName` + `RequestedScopes`
already. For DCR we add one field to `ConsentInfoResponse`:

- `IsDynamicallyRegistered: bool` — resolved from the
  Application-properties dict key
  `cocoar:dcr:is_dynamically_registered`

The SPA's `ConsentView.vue` then renders the `[unverified]` marker
+ warning text conditionally on that flag. Small change, included
in the DCR effort estimate below.

## Effort

- Custom Minimal-API `/connect/register` endpoint + `IDcrRegistrationValidator` service (client_name spoofing rules incl. NFKC + Latin-1 + reserved-names check, redirect-uri policy, grant/auth-method whitelist, rate-limit hook): 1.5d
- Custom Discovery handler for `registration_endpoint` (analog to existing `RealmScopesSupportedHandler`): 0.25d
- Realm-Settings tab + OAuthApi-flag + OAuthScope-flag + Reserved-names admin UI: 1d
- ResourceIndicatorHandler extension for AllowDcr-flag check (or sibling handler): 0.5d
- Audit-log event types (5 new): 0.25d
- Application-properties DCR keys + last-used tracking + token-lifetime overrides via Settings dict: 0.5d
- Consent-screen `[unverified]` marker (conditional render branch + small backend response-shape extension): 0.25d
- GC background service: 0.5d
- Admin UI (OAuth-Clients-Grid filter + "Registration Info" tab + Auth-Log event filters): 1d
- Tests + docs: 1.25d

**Total: 7-8 days** for DCR v1. Consent-UI prereq is shipped — no
extra time needed.

(Up from 5d original estimate after absorbing: external-review feedback (scope-containment, name-spoofing, reserved-names, IPv6-loopback, tighter TTLs, expanded audit events), codebase-reality-check (Properties-dict storage instead of new fields, ResourceIndicatorHandler extension), and the OpenIddict-fact-check (no built-in DCR endpoint → custom Minimal-API). The 1-2d consent-UI prereq was eliminated by shipping it ahead of DCR; the per-tenant 404 filter was eliminated by going Minimal-API.)

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
