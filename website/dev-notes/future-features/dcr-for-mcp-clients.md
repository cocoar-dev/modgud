# Dynamic Client Registration (RFC 7591) for MCP clients

> **Status:** Idea — not started. Captured 2026-05-07.
> **Why:** The MCP authorization spec (revision 2025-06-18) expects
> a generic AI agent (Claude Code, Cursor, Continue, Zed, …) to be
> able to attach to any cocoar-internal MCP server with no out-of-
> band setup beyond the user pasting the MCP URL. Without DCR,
> every agent instance has to be pre-registered as an OAuth client
> by an admin — practical for one internal pilot, painful for
> "anyone in the org with an agent". The first concrete trigger is
> `cocoar-policy` wanting to gate its `/mcp` endpoint behind
> cocoar.auth (see Trigger below).

## What DCR is — and what it isn't

Sharing the word "register" with two unrelated concepts. Be careful.

| | **User Registration** | **Dynamic Client Registration (DCR)** |
|---|---|---|
| What gets registered | A **person** (account in the IdP) | A piece of **software** (OAuth client) |
| Who triggers | End-user on a sign-up form | Anonymous program via JSON POST |
| Standardised | Not standardised — every IdP rolls its own | RFC 7591 |
| Trust model | "new user is allowed to self-onboard" — typical SaaS | "any software is allowed to register itself as a client" — DoS / spam vector |
| Use-case | "Acme Corp employee creates a Cocoar account" | "Claude Code attaches to the cocoar-policy MCP server" |
| Cocoar.auth status | Future feature, separate page | This page |

User Registration is its own future-features entry whenever we
get there; this page is exclusively about DCR.

## What MCP clients require

From the MCP authorization spec, revision 2025-06-18:

1. Client hits the MCP server without a token.
2. Server returns 401 with
   `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource"`.
3. Client fetches that JSON, learns the **authorization server URL**
   (= our IdP).
4. Client fetches our `/.well-known/oauth-authorization-server`
   (RFC 8414).
5. **If `registration_endpoint` is in there**: client `POST`s to it
   and gets back a `client_id` (and optionally a `client_secret`).
6. Client kicks off Authorization Code + PKCE with
   `resource=<mcp-server-url>` (RFC 8707).
7. User authenticates at our IdP, redirected back with code.
8. Client exchanges code for an access token (audience-bound to the
   MCP server) and retries the original MCP request.

The client never had to talk to a human admin in step 5. That's
the value DCR brings — and the trust shift it creates.

## Why we don't have it yet

OpenIddict 5+ (we're on 7.x) has all the plumbing for the
`/connect/register` endpoint, but it's **off by default**. Turning
the bit on without thought would expose a permanent open registration
endpoint to the public internet. The IdP team's call to leave it off
until the policy layer around it is designed is the right one.

## What we'd build

### Core (RFC 7591)

- Enable OpenIddict's DCR endpoint at `/connect/register`.
- Add `registration_endpoint` to the discovery document.
- Accept the standard registration request body
  (`client_name`, `redirect_uris`, `grant_types`,
  `token_endpoint_auth_method`, `scope`).
- Return the standard response with the issued `client_id` (and
  `client_secret` if confidential).

### Policy layer (custom — required for safety)

This is **the actual work**. The endpoint is plumbing; the policy
is what makes it production-safe.

1. **Rate-limiting on `/connect/register`** — without this, the
   endpoint is a DoS and storage-exhaustion vector. Per-IP limit
   probably 5 / hour to start. Per-realm limit too.

2. **Audit log** of every registration: timestamp, source IP,
   resolved realm, requested `redirect_uris`, requested scopes,
   issued `client_id`, the user-agent / accept headers (lightweight
   fingerprinting). Forensics gold dust when a registration is
   later flagged.

3. **Realm resolution** — incoming registrations land in the realm
   matching the `Host` header (same as the rest of the IdP). A
   registration on `auth.acme.com` produces a client in the
   `acme` realm.

4. **Redirect-URI validation** — beyond the OAuth-spec basics
   (no fragment, no wildcards on host, no `localhost` masquerading
   as production), enforce a per-realm allowlist of host patterns
   so a hostile registrant can't claim `https://attacker.example/`
   as a redirect.

5. **Optional: approval workflow** — `pending_admin_review`
   state on the issued client until a realm admin approves.
   Default off (latency would defeat the point), opt-in per realm
   for paranoid deployments. Audit-log entry surfaces in the admin
   UI as "X clients pending approval".

6. **Optional: software-statement validation** (RFC 7591 §2.3) —
   accept a JWT-signed `software_statement` from a known issuer
   (e.g. Anthropic, Continue.dev) instead of treating every
   registration as anonymous. Lets us vendor-allowlist trusted
   agents while still letting unknown clients register at lower
   trust. Probably not v1.

7. **Auto-cleanup of stale DCR clients** — clients registered via
   DCR that haven't authenticated in N days get GC'd. Keeps the
   client table from being a permanent dump of one-shot agent
   sessions. Configurable per realm, default 90 days.

8. **Public clients only by default** — DCR clients should be
   PKCE-public (no client_secret to share). Confidential DCR
   clients are a different beast (where does the secret go?
   client-side storage is dangerous). Allow opt-in via the
   `software_statement` mechanism only.

## Risks if we ship without the policy layer

- **Storage exhaustion:** anonymous bot registers a million
  clients. Mitigation: rate-limit + auto-cleanup.
- **Phishing redirect_uris:** attacker registers a client with
  `redirect_uri=https://attacker.example/grab-code`, then social-
  engineers a victim to authorise it. Mitigation: redirect-URI
  policy / per-realm allowlist.
- **Brand impersonation:** attacker registers a client named
  "Cocoar Auth Official" hoping the consent screen shows that to a
  victim. Mitigation: `client_name` displayed with `[unverified]`
  marker unless `software_statement` validates.
- **Cross-realm leak:** registration on `auth.acme.com` somehow
  ends up in another realm. Mitigation: tenant-scoped session +
  the existing realm middleware (already in place for other
  endpoints).

## Effort estimate

- OpenIddict endpoint enablement: half a day
- Rate-limit + audit-log + realm-scoping: 1 day
- Redirect-URI policy + per-realm allowlist: 1 day
- Admin UI ("DCR clients" tab + approval queue if opt-in): 1-2 days
- Tests + docs: 1 day

**Total realistic: 3-5 days for a v1** that's safe for production.

`software_statement` and approval-workflow are explicitly NOT in
the v1 — those are escalations once the basic shape ships and the
threat model from real usage starts informing.

## Trigger

`cocoar-policy` (sibling SaaS, MCP server for policy evaluation
+ knowledge-document storage) wants `auth.cocoar.dev` as its IdP.
The DCR ask was raised in `.local/idp-requirements-for-mcp-auth.md`
(2026-05-07), where the team identified DCR as a "blocker for
'any agent attaches'" — they accept that the v1 integration can
work with one pre-registered client (Bernhard's Claude Code), but
beyond that demands DCR.

## Sequencing

DCR is parked **until everything else from the cocoar-policy
requirements doc is done**:

1. ~~Drop `plain` from PKCE list~~
2. ~~Verify refresh-token rotation~~
3. ~~Verify access-token = signed JWT with standard claims~~
4. ~~Implement Resource Indicators (RFC 8707)~~ — bigger blocker than DCR
5. (Realm admin adds `mcp:read` + `mcp:write` scopes — pure config)
6. → re-evaluate this DCR page; either ship the v1 or reprioritise

The `Resource Indicators` work is a prerequisite anyway — no point
having DCR if the issued tokens don't bind to a resource.

## Related

- The first MCP integration (cocoar-policy with one pre-registered
  client) can ship without DCR. We test the rest of the flow end-
  to-end with that pilot, then decide whether DCR is actually the
  next bottleneck or if user-self-registration (a different feature)
  is more urgent.
- User self-registration is a **separate** future-feature page.
  Worth opening when we have the first paying customer asking for
  it; not before.
