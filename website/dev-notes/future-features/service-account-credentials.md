# Service Account Credentials — Linking to OAuth Clients

> **Status: design captured 2026-05-23.** Phase 1 (User-Email-required + Soft-Gating + Sensitive-Actions-Gating) shipped. Phase 2B (ServiceAccount CRUD UI) shipped. **Phase 2C (Credentials-Linkage to OAuth Clients) is the open piece.**

## TL;DR

Service Accounts are machine identities — first-class principals in the Authorization model, but they don't have a password/email/MFA. The standard way for a machine to authenticate is OAuth's **`client_credentials`** grant: caller sends `client_id` + `client_secret` to `/connect/token`, gets back an access token. Cocoar.Auth already speaks `client_credentials` (OpenIddict 7); what's missing is the binding from **OAuth Client (the credential carrier)** to **ServiceAccount (the principal in the authorization model)**.

This document captures the design space — why both layers exist, the binding model, the token-issue logic change, and the UI flow.

## Why both ServiceAccount AND OAuth Client?

They sit at different layers and answer different questions:

| Layer | Object | Answers |
|---|---|---|
| Credential / wire | **OAuth Client** | "WIE authentisiert sich das Ding" — `client_id`, `client_secret`, scopes, lifetimes, rotation |
| Authorization / identity | **ServiceAccount** | "WER ist das Ding" — stable identity for audit logs, membership in groups, role/permission grants |

### Why not OAuth-Client-only?

Cocoar.Auth is a centralised IdP with a **unified permission model** (`Principal → Group → Role → Permission`). The model has to work identically for humans and machines:

- Person `bwi` is in group `data-engineers` → has role `data-read` → has permission `timetodo:data:read`
- ServiceAccount `ci.build-agent` is in group `data-engineers` → has role `data-read` → has permission `timetodo:data:read`

If the OAuth Client alone carried machine identities, the permission model would have to be duplicated on the OAuth-Client side. Plus the audit-log story is materially worse: `ci.build-agent did X` reads better than `client_id=4f7a9b...e3 did X`.

### Industry parallel

- **Keycloak:** OAuth clients with "Service Accounts Enabled" get an auto-created hidden user. That user sits in realm-roles, groups, etc. Same pattern as ours, just hidden.
- **AWS IAM:** Service Accounts (IAM Roles) are first-class identities; access keys are just the wire-level credentials attached to them.
- **GCP IAM:** Service Accounts are first-class principals; JSON keys are credentials.

The Authorization slice in Cocoar.Auth already has `ServiceAccount` as a polymorphic `Principal` sub-class (`Cocoar.Auth.Authorization/Principals/ServiceAccount.cs`) — the design intent was always to mirror this pattern.

## Authentication-mode separation

A given OAuth Client should serve **exactly one** identity model:

| Client kind | Allowed grants | Bound to | Token `sub` |
|---|---|---|---|
| Standard user-facing client | `authorization_code` (+ `refresh_token`) | nothing (user logs in via browser redirect) | `Person.Id` of the logged-in user |
| Service-account client | `client_credentials` only | 1:N to a ServiceAccount | `ServiceAccount.Id` |

**No mixing.** A client with both `authorization_code` AND `client_credentials` enabled makes `sub` ambiguous: at the user flow it's the logged-in person; at the M2M flow it's… what? The client itself? A linked SA? Configurable per grant? Industry audits regularly flag mixed-grant clients as findings.

Enforcement at validation time:

- `LinkedServiceAccountId` set → `AllowedGrantTypes` must equal `["client_credentials"]` exactly
- `client_credentials` in grants → `LinkedServiceAccountId` MUST be set (no ownerless M2M clients)
- User-flow grants (`authorization_code`, `implicit`, `device_code`, …) → `LinkedServiceAccountId` must be null

## How the SA actually gets a token

Concrete flow end-to-end:

### 1. Admin creates the SA + issues credentials

In the SA admin UI:

```
Service Accounts → Create → AccountName: "ci.build-agent" → Save
SA-Detail → "Issue Credentials" button → Server creates behind the scenes:
  OAuth Client {
    client_id: "ci.build-agent.k7f2x9n3"   ← auto-generated
    client_secret: "s3cret-only-shown-once"  ← hashed in DB, plaintext shown once in UI
    AllowedGrantTypes: ["client_credentials"]
    ClientType: "confidential"
    RequireClientSecret: true
    LinkedServiceAccountId: <ci.build-agent SA Id>
  }
UI shows the secret ONCE with a "copy now, won't be shown again" warning.
```

### 2. Admin copies credentials into the caller system

E.g. GitHub Action Secrets:

```
COCOAR_CLIENT_ID=ci.build-agent.k7f2x9n3
COCOAR_CLIENT_SECRET=s3cret-only-shown-once
```

### 3. Caller exchanges credentials for a token

Standard OAuth `client_credentials` request — no SA-specific protocol:

```bash
curl -X POST https://idp.cocoar.local/connect/token \
  -d "grant_type=client_credentials" \
  -d "client_id=ci.build-agent.k7f2x9n3" \
  -d "client_secret=s3cret-only-shown-once" \
  -d "scope=builds.write"
```

### 4. Server resolves the SA at token-issue time

In `AuthorizationEndpoints.cs:305-334` (the `IsClientCredentialsGrantType()` branch):

```csharp
// HEUTE:
identity.SetClaim(Claims.Subject, await applicationManager.GetClientIdAsync(application));
identity.SetClaim(Claims.Name, await applicationManager.GetDisplayNameAsync(application));

// MIT 2C:
var saId = oAuthClient.LinkedServiceAccountId;
if (saId is null)
    return ForbidInvalidGrant("client_credentials clients must be bound to a service account");

var sa = await session.LoadAsync<ServiceAccount>(saId.Value);
if (sa is null || sa.IsDeleted || !sa.IsActive)
    return ForbidInvalidGrant("linked service account is not available");

identity.SetClaim(Claims.Subject, new ShortGuid(sa.Id).ToString());
identity.SetClaim(Claims.Name, sa.AccountName);
// Plus claims from SA's Group → Role → Permission chain
// (Permission resolver doesn't care if the principal is Person or ServiceAccount)
```

### 5. Caller uses the token

```bash
curl -H "Authorization: Bearer <access_token>" https://timetodo/api/builds
```

Downstream app validates the token, sees `sub = SA.Id`, decides what's allowed. Audit log: `"ci.build-agent triggered build"`.

## 1:1 vs 1:N

Three relationship topologies:

- **1:1** (one SA = one OAuth Client) — simplest mental model. Matches Keycloak's "Service Accounts Enabled" feature exactly. Covers 95% of cases.
- **1:N** (one SA, multiple OAuth Clients) — adds flexibility:
  - **Key rotation without downtime:** issue new client, switch caller to new credentials, decommission old client
  - **Multiple consumers share SA identity:** dev-CI + staging-CI + prod-CI all log as `ci.build-agent` in audit, but each has its own credentials
  - **Per-caller scope narrowing:** one client has `builds.read`, another has `builds.write`, both log as same SA
- **N:1** (multiple SAs share one OAuth Client) — **forbidden**. `sub` would be ambiguous.

**Recommendation: 1:N.** The default UI path is 1:1 ("Issue Credentials" creates a single client) — if the admin later needs a second client for rotation, the button is there again. SA modal shows a list of all linked clients; admin can rotate the secret per client or delete a client without affecting the SA identity.

## UX direction — SA owns its credentials (locked 2026-05-23)

Two configuration UX options were on the table:

- **A. Two-step.** Admin creates the SA in one place, then goes to OAuth-Clients to create a `client_credentials` client and pick the SA from a dropdown. Two stable entities, two UIs, FK-link between them.
- **B. SA-managed credentials (chosen).** The SA *owns* its OAuth Clients as child resources. Editing happens exclusively inside the SA detail modal. The OAuth-Clients-Grid still lists them (discoverability), but opening one shows a read-only view with a banner "Managed by service account `ci.build-agent` [→ Edit]" that deep-links back into the SA modal.

**Reasoning:** split configuration across two surfaces that only work together is confusing — admins shouldn't have to keep two views in sync mentally. Option B mirrors how Keycloak does it (service-account user is hidden inside the client config) but inverts the ownership: the SA is the user-facing concept; the OAuth Clients are an implementation detail of "how does this SA authenticate".

### Concrete UI surfaces

**ServiceAccount detail modal** grows a "Credentials" section/tab:

```
SA: ci.build-agent
┌─ General  Credentials  Groups  ─────────────────────────────┐
│ Credentials                                                  │
│ ┌──────────────────────────────────────────────────────────┐ │
│ │ [1] ci.build-agent.k7f2x9n3                              │ │
│ │     Scopes: builds.read, builds.write                    │ │
│ │     Active · created 2026-05-23                          │ │
│ │     [Rotate secret]  [Edit]  [Delete]                    │ │
│ ├──────────────────────────────────────────────────────────┤ │
│ │ [2] ci.build-agent.q8m4z2p1                              │ │
│ │     Scopes: builds.read                                  │ │
│ │     Active · created 2026-05-25 · prod-CI read-only      │ │
│ │     [Rotate secret]  [Edit]  [Delete]                    │ │
│ ├──────────────────────────────────────────────────────────┤ │
│ │ [+ Issue new credential]                                 │ │
│ └──────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
```

Each row drills into a sub-modal (or expandable section) that exposes the full OAuth-Client config — scopes, AppIds, lifetimes, enabled/disabled. Grant-types are system-pinned to `["client_credentials"]` and not editable here either.

**Global OAuth-Clients-Grid** still shows SA-managed clients alongside user-facing ones:

- Distinguished by a "M2M • ci.build-agent" badge in the AccountType/Owner column
- Double-click opens a **read-only modal** with a yellow banner at the top:
  > ⚠ This client is managed by service account `ci.build-agent`. To edit, go to the service account. [→ Open service account]
- The deep-link button navigates to the SA detail modal and scrolls/opens the Credentials section
- Optional grid filter "User-facing / Service Account / All" for triage

**Outcome:** there's only one place to *edit* credentials (SA modal). The OAuth-Clients-Grid is for *discovery* and inspection. Admin can't accidentally unlink a credential or change its grant-types from the wrong place.

### Validation consequences (with SA-managed model)

- Grant-types of an SA-managed client are system-pinned to `["client_credentials"]` — not editable
- Deleting an SA cascade-deletes its credentials (with confirmation: "This will revoke N credentials"). Tokens issued before the delete remain valid until their natural expiry — Cocoar.Auth doesn't currently support revocation propagation, and adding it is a separate concern.
- "Unlink credential from SA" is not an operation — the only way to detach is Delete-and-recreate

## OAuth-Client model — current state (as of 2026-05-23)

For reference: what the OAuth Client looks like today, before this design ships.

- `Cocoar.Auth.Application/DTOs/OAuth/OAuthClientDtos.cs`:
  - `ClientType`: "public" / "confidential" (orthogonal to grant-types)
  - `AllowedGrantTypes`: free list of strings — `authorization_code`, `client_credentials`, `refresh_token`, `implicit`, `device_code` etc.; any combination
  - `AppIds`: 0..N apps the client serves (orthogonal axis)
  - `RequireClientSecret`: forces secret for `confidential` clients
- `AuthorizationEndpoints.cs:305-334` — the `client_credentials` branch currently sets `sub = client_id` itself. No principal backing, no SA lookup.
- `OAuthAdminMapping.BuildClientPermissions` — maps grant-type strings to OpenIddict's internal permission constants. `client_credentials` is fully supported at the wire level.

## Phase 2C scope (concrete work)

1. **Domain + DTOs:**
   - `OAuthClient.LinkedServiceAccountId` (optional `Guid`) — pointer to the owning SA
   - Set by the SA "Issue credential" flow, never user-editable
   - Persisted in the existing OAuth-Client doc; mapped in/out via existing event sourcing

2. **Validation rules** at the OAuth-Client endpoints (enforce the SA-managed invariants):
   - SA-managed clients (`LinkedServiceAccountId != null`) MUST have `AllowedGrantTypes == ["client_credentials"]` — system-pinned, not user-editable
   - Standard endpoint `PUT /api/oauth/client/{id}` rejects changes to `LinkedServiceAccountId` and `AllowedGrantTypes` on SA-managed clients (those go through SA-scoped endpoints below)
   - Standard endpoint refuses to set `client_credentials` grant on un-linked clients (no rogue M2M clients)
   - Non-SA clients with user-flow grants stay edit-able the way they are today

3. **SA-scoped credential endpoints** (the "owned children" pattern):
   - `POST /api/service-account/{id}/credentials` — issue new credential, returns one-time secret. Server creates the OAuth Client with `LinkedServiceAccountId={id}`, `AllowedGrantTypes=["client_credentials"]`, `ClientType="confidential"`, `RequireClientSecret=true`.
   - `GET /api/service-account/{id}/credentials` — list owned credentials
   - `PUT /api/service-account/{id}/credentials/{credId}` — update scopes/AppIds/lifetimes/Enabled (grant-types untouchable)
   - `POST /api/service-account/{id}/credentials/{credId}/rotate` — generate fresh secret, return one-time
   - `DELETE /api/service-account/{id}/credentials/{credId}` — delete the OAuth Client
   - `DELETE /api/service-account/{id}` — cascade-deletes all owned credentials with confirmation count

4. **Token endpoint** (`AuthorizationEndpoints.cs`):
   - In the `IsClientCredentialsGrantType()` branch: look up `LinkedServiceAccountId`, resolve SA, set `sub` + `name` accordingly
   - Resolve claims from SA's group/role/permission chain (Permission resolver already handles `ServiceAccount` since it's just a `Principal`)
   - If a `client_credentials` client has no linked SA (legacy data), refuse with a clear error — or fall back to current behaviour during a transition window (see Migration below)

5. **OAuth-Clients grid:**
   - SA-managed clients badged "M2M • {sa.AccountName}" in an Owner/Type column
   - Double-click on an SA-managed client opens a **read-only modal** with a banner: "Managed by service account `ci.build-agent` — to edit, open the service account. [→ Open service account]"
   - Banner button navigates to `/admin/service-accounts#{sa.id}` and opens the Credentials tab
   - Optional grid filter "User-facing / Service Account / All"

6. **ServiceAccount detail modal — new "Credentials" tab:**
   - "Issue new credential" button → calls `POST .../credentials` → opens a one-time-secret display modal
   - List of issued credentials with per-row "Edit", "Rotate secret", "Delete" actions
   - Sub-editor for credentials exposes scopes / AppIds / lifetimes / enabled — system-pinned grant-types hidden
   - Empty state when no credentials yet: "This service account has no credentials yet. [Issue first credential]"

## Migration concern — existing `client_credentials` clients

If there are seeded or pre-existing OAuth clients today with `client_credentials` enabled but no SA link, the new validation would invalidate them. With the SA-managed UX direction locked, the migration is straightforward:

- For each existing un-linked `client_credentials` client, **auto-provision a placeholder SA** named `legacy.{clientId}` (or derived from `DisplayName`) and link the client to it
- Convert the client into an SA-managed credential under that auto-SA
- Admin can later rename the SA, merge into another SA, or delete + re-issue under a properly-named SA

**Permissive transition window** stays the fallback if any production realm has user-named un-linked `client_credentials` clients that aren't safe to auto-migrate: keep the legacy `sub = client_id` behavior with a deprecation log warning until the admin explicitly migrates.

Recommendation: **check first whether any `client_credentials` clients exist in seed data or production.** If zero, ship strict directly; if any, ship the auto-provision-SA migration tool.

## Open questions / decisions to lock before shipping

- **Auto-naming for issued clients.** Proposed pattern: `{sa.AccountName}.{8-char-suffix}` (e.g. `ci.build-agent.k7f2x9n3`). Confirms uniqueness without admin typing.
- **Default lifetimes for M2M tokens.** Today's defaults (1h access, 14d refresh) — but refresh doesn't apply to `client_credentials`. Need to verify defaults are sensible for M2M (typically short access tokens, no refresh, re-auth via client_credentials).
- **Should the SA UI also support "rotate all credentials" as a bulk action?** Nice for emergency-rotation.
- **Telemetry:** record SA-token-issue separately from human-token-issue in OpenTelemetry meters (already shipped) — gives ops visibility into M2M usage.
- **Documentation:** new admin doc `service-accounts.md` covering create → issue → rotate → delete lifecycle once shipped.

## Out of scope for Phase 2C

- mTLS client authentication (RFC 8705) — alternative to `client_secret`; later.
- `client_assertion` (private_key_jwt) — alternative for signed JWT auth; later.
- Bulk import of service accounts from external IAM systems.
- Per-SA quotas / rate-limits at the token endpoint.
