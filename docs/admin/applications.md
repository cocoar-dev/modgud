# Applications

An **Application** in Modgud is the organisational clamp around a SaaS
app — it owns its own permission catalog, its own roles, and its own
OAuth bindings. When a realm is created the system app `modgud` (=
Modgud itself) is provisioned automatically; every other app you
register here.

An Application is **not** an isolation boundary — that is the realm
(tenant): own database, signing keys, OIDC issuer, user pool. An
Application is a **soft facet** *within* a realm: it shares the realm's
user pool (one account, one `sub`, across all of a realm's apps — no
shadow users), and on top of the permission clamp it can carry its own
**login-experience** — an optional subdomain, branding, email branding,
and per-app overrides of self-registration / native-grant / DCR / CIMD
policy. Those live in [Application settings](#application-settings) below.
See [Concepts → Apps & resource access](../concepts/apps-and-resource-access)
for the model.

::: tip First time?
If this is your first integration, the [SaaS App Integration Walkthrough](../integrate/saas-walkthrough)
is the better entry point — it walks through all five stations (App,
Client, Resource Server, Roles, backend code).
:::

## What is an Application for?

Modgud manages permissions as **two-segment** `<resource>:<action>`
strings inside an App's catalog — the app slug is the implicit context,
not part of the string. The same string `invoice:write` in the `billing`
app's catalog and in the `shipping` app's catalog are different
permissions, distinguished by the audience the gate is running for.

An app therefore bundles:

- **Permission catalog** — the `<resource>:<action>` entries that the
  app's resources understand
- **Roles** with `AppId` — bundles of `PermissionIds` from this app's
  catalog
- **Groups** via `BoundTo` — which organisational unit is active in
  which app
- **OAuth Clients** via their `AppIds` list — which token requesters
  serve the app
- **OAuth APIs (Resource Servers)** via their `AppId` — which backend
  identities belong to it
- **OAuth Scopes** via their `AppId` — which scopes a client of the
  app may request

## Application fields

| Field | Meaning |
| --- | --- |
| Slug | URL- and permission-safe identifier. Lowercase, 3-63 characters, letters/digits/hyphens. **Immutable after creation.** |
| Display Name | What appears in lists and consent screens |
| Description | Optional, one-liner |
| Permission catalog | `<resource>:<action>` entries this app's resources can be gated by |
| IsSystem | True only for `modgud` and `control-plane`; cannot be deleted |

## Reserved slugs

These slugs are forbidden — they collide with the permission grammar
or with system invariants:

- `realm` — would clash with `realm:admin` (realm-wide bypass)
- `*` — wildcard in `Group.BoundTo`
- `modgud` — system app, seeded automatically into every realm
- `control-plane` — control-plane system app, seeded only on the
  Control-Plane realm

## Creating an app

![Create application dialog](/screenshots/admin-anwendung-modal.png)

Click **Create** in the list view.

1. Pick a slug — kebab-case, memorable: `acme`, `billing`,
   `inventory`. Not changeable.
2. Fill in display name and description.
3. Add catalog entries: `<resource>:<action>`, kebab-case both sides
   (`invoice:read`, `invoice:write`, `invoice:admin`).
4. **Create**.

The app appears in the list. **It still has no effect** on its own —
you also need to:

- link at least one OAuth client to it ([OAuth Clients](./oauth-clients))
- (for an authenticated server-to-server callback into Modgud)
  provision a resource server
- create at least one role + group that connects users to the app

## Cloning an app

The slug is immutable, so the way to stand up a near-identical app — or to
effectively **rename** one — is to clone it. In the list, right-click a row →
**Clone**. The Create modal opens pre-filled from the source:

- **Slug** is left blank — give the copy its own (the source's can't be reused).
- **Display name, description and the whole permission catalog** are copied. The
  catalog entries are copied as *new* entries (fresh ids), so the source app's
  role grants and resource-server subsets are left untouched.
- **Settings** are copied too — branding, registration, client-session, native-grant / DCR / CIMD
  overrides — **except the Origin subdomain**, which is globally unique and would
  collide. Set a new subdomain on the copy if it needs one.

To rename: clone the app, give the copy the new slug, re-point the dependent
clients / scopes / APIs / roles / groups, then delete the original.

## Provisioning the resource server

Under [OAuth → APIs](./oauth-apis), create an OAuth API named after
the app's slug and link it to the app. Its `PermissionIds` declare
which subset of the catalog this resource server gates on (full
catalog is the typical default; tighten for microservices that only
need a slice). This is the identity Modgud uses to compute the
audience-keyed `resource_access` block at the token boundary. The key
is the OAuth API Audience, not this App's slug.

## Extending or changing the catalog

Catalog entries can be edited any time, but:

- **Adding** is harmless. Existing role assignments remain valid; new
  permissions become assignable.
- **Removing** is dangerous. Roles that reference the removed entry
  silently lose the grant. The admin UI shows a "rename" indicator and
  a delete-block prompt when something downstream is still referencing
  a catalog entry. Audit roles before dropping.

## Application settings

Beyond the permission catalog, an Application can override a slice of the
realm's configuration and carry its own login experience. Open the app
from the list and switch to the **Settings** tab (disabled for the system
apps). Everything here is **optional and
sparse** — an App overrides only what you switch on; anything left off
**inherits the realm** value, field by field. Clearing an override
re-inherits the realm.

| Section | What it does |
| --- | --- |
| **Origin** | The App's own subdomain (e.g. `acme.cocoar.app`), which must be a child of the realm's primary domain. Setting it routes that host to this App and serves the branded login there; clearing it falls back to the tenant URL. The OIDC issuer stays the tenant's (anchored to the realm primary domain) — a subdomain is not its own issuer. |
| **Branding** | Product name, primary colour, logo/favicon — the look of the login + consent UI when reached via this App. |
| **Email branding** | Product name, sender display name, sender address, validated reply-to address, optional subject prefix, preheader and footer used in this App's outbound emails (OTP, magic link, reset, verification, ...). The sender address falls back to the realm's, then the deployment's; a custom address must be deliverable from your mail provider (SPF/DKIM). Logo and button colour follow effective App branding. See [Transactional email](../platform/email-customization). |
| **Login methods** | Enable/disable internal password/passkey and magic-link entry points, and select/order the external OIDC/SAML providers exposed to this App. The allow-list is enforced by the public list and protocol start endpoints, not only hidden in the SPA. An explicit empty provider list disables all external providers. |
| **Self-registration** | Per-app override of the realm self-registration policy (allowed email domains, admin approval, default groups, ToS/privacy URLs) plus the **posture** (see below). Captcha stays realm-level. |
| **Registration fields** | Per-app override of which identity fields (username / first / last name) are required when an account is created — each one inheriting the realm by default. See [Registration fields](#registration-fields) below. |
| **Client sessions** | Idle and absolute lifetime defaults for refresh-token-backed native/OAuth sessions belonging to this App. Each field inherits the realm unless overridden; an individual OAuth client can override the App again. |
| **Native grants** | Per-app toggle + token lifetimes for the cookieless [native passwordless grants](../integrate/native-apps). |
| **DCR** | Per-app override of [Dynamic Client Registration](./dynamic-client-registration) (enable, token lifetimes, rate limits, reserved-name blocklist). |
| **CIMD** | Per-app override of [Client-ID Metadata Documents](./client-id-metadata-documents) (enable, token lifetimes). |
| **Rate limits** | Sparse override of the realm's [auth rate limits](../platform/rate-limits): only the overridden policy/dimension cells win, plus an optional own source allowlist and enforcement mode. |

### Self-registration posture

The self-registration section carries a **posture** that decides how a
passwordless sign-up is triggered for the App:

| Posture | Behaviour |
| --- | --- |
| `JitOnOtp` *(default)* | Sign-in-or-sign-up: an unknown email at the native OTP endpoint gets a pending registration and a one-time code — no user yet; redeeming the code proves the mailbox, creates the confirmed passwordless account and signs in. The low-friction consumer default. |
| `Off` | No self-registration — an unknown email gets the uniform anti-enumeration response, no user is created. |
| `ExplicitEndpoint` | Registration is a deliberate, separate step via `POST /api/account/native/register` (room for an app's own ToS / profile UI); sign-in stays strict — the OTP-request endpoint serves only known users. An unknown email at the register endpoint gets a pending registration and the same registration code; the account is created when the code is redeemed. |
| `InviteCode` | Invite-only: an unknown email enters the registration pipeline **only** when the native sign-up request carries a valid, unused, unexpired [invite code](#invite-codes-the-invitecode-posture) (the account is created when the code is redeemed). Existing confirmed users still sign in normally (no code needed). Code failures are indistinguishable from `Off` (anti-enumeration). |

See [Integrate → Native apps](../integrate/native-apps#native-passwordless-registration-jit-on-otp)
for the end-to-end flow.

### Invite codes (the `InviteCode` posture)

Set the posture to `InviteCode` to run an app **invite-only** — a new person
gets an account only by presenting a single-use code. The code is app-bound,
optionally email-bound (bearer by default), hashed at rest, and expires
(default **14 days**). Two ways to mint, and you only need to set up the second
one if a backend should mint automatically:

- **Admin UI (works immediately, no setup).** Open **Invite Codes** in the
  admin sidebar (OAuth & Federation), pick the app, and bulk-mint. The plaintext
  codes are shown **once** — copy them then; only their hashes are stored.
  Gated by the `invite-code:read` / `invite-code:write` permissions.

- **Machine-to-machine (the consuming app's backend).** For a backend that mints
  codes itself (e.g. when a user invites someone), there is a one-time setup:
  1. Create an OAuth scope named **`invite:write`** that is **bound to this app**
     (set its App-ID) — see [OAuth scopes](../reference/admin-api). Scope names
     are unique per realm, so in a multi-app realm name the scope per app.
  2. Give a **ServiceAccount** a credential (a confidential `client_credentials`
     client) carrying that scope.
  3. The backend then calls
     `POST /api/app/{appId}/invite-codes` `{ "Count": N, "BoundEmail": null, "ExpiresInDays": 7 }`
     with its `client_credentials` access token. The response carries the
     plaintext codes once. The `{appId}` must match the scope's app — a
     cross-app or cross-tenant caller is rejected.

The public mobile client never mints — it only **redeems** a code it was handed,
by passing it on the native sign-up request (`InviteCode` field on
`POST /api/account/native/otp/request`). Redemption confirms the mailbox via the
usual OTP step. See [Integrate → Native apps](../integrate/native-apps) for the
redemption flow.

> **Identity vs. authorization.** Modgud's invite code only governs *who may
> exist*. What the invite is *for* (join list L, beta access, …) stays in the
> consuming app — Modgud only ever learns `(email, code, appId)`.

### Registration fields

Overrides the realm's [Registration Fields](./realm-settings#registration-fields)
policy for this App — which identity fields (username / first / last name) are
required when an account is created here. Each field is a tri-state
(`Off` / `Optional` / `Required`) that **inherits the realm** when left unset,
so a Consumer App can stay email-only inside the same tenant where an Enterprise
App requires a real name.

The resolved (App ⊕ realm) policy is published at `GET /api/app-info`, so the
App's clients render exactly the inputs it requires. When an App requires a
field, **its native clients must collect and send it** (`FirstName` / `LastName`
on the native OTP / register calls) — otherwise registration fails. Email is
always required and is never configurable.

### Cleanup and reset semantics

Turning off the Origin override sends an explicit clear and removes the global host→Application route. Deleting an unreferenced Application also removes its settings document and every hostname pointing at it before the App is tombstoned. Existing permission-reference delete blocks still apply.

### What stays realm-only

Captcha (needs a per-app secret store), account deletion / audit / custom
pages (operational / GDPR), and the DCR garbage-collection interval (the
GC job iterates per realm) are **not** per-app overridable — set them in
[Realm settings](./realm-settings).

An App is one resource, so these overrides ride **inline** on the app itself —
`GET /api/app/{id}` returns them, and `POST`/`PUT /api/app` write them in the
same call that creates or updates the app (`app:read` / `app:write`), in one
tenant transaction. There is no separate settings endpoint. See the
[Admin API reference](../reference/admin-api#application-settings).

## Relationships to other areas

| Linked with | Where | How |
| --- | --- | --- |
| OAuth Clients | [OAuth Clients](./oauth-clients) | n:m via the client's `AppIds` list |
| OAuth Scopes | [OAuth Scopes](./oauth-scopes) | 1:n via the scope's `AppId` (or null = global) |
| OAuth APIs (Resource Servers) | [OAuth APIs](./oauth-apis) | 1:n via the API's `AppId` |
| Roles | [Roles](./roles) | n:1 via the role's `AppId` |
| Groups | [Groups](./groups) | n:m via the group's `BoundTo` list |

## The system apps

### `modgud`
The app `modgud` represents the Modgud admin surface itself.
Permissions like `user:read` or `oauth-client:write` (in this app's
catalog) gate the admin UI sidebar.

- **Auto-seeded** on first realm setup
- **Not deletable** (`IsSystem = true`)
- **Slug not renameable**
- Catalog matches the built-in admin surface — edit cautiously

### `control-plane`
Seeded **only** into the realm flagged `IsControlPlane = true`. Owns
the `realm:read` / `realm:write` permissions that gate
`/api/admin/realms/*`. See
[Concepts: Control Plane / Data Plane](../concepts/control-plane).

If you change a system app's catalog, the admin sidebar may hide items
because the corresponding permission is no longer registered. When in
doubt, restore the default catalog (see `AppRealmSeeder` in source).

## Deleting an app

System apps cannot be deleted. Regular apps can — but:

- OAuth clients with the app in their `AppIds` list keep the entry
  (UI shows it as "unknown app")
- OAuth scopes with this AppId become orphaned
- Roles with this AppId stay — but their `PermissionIds` no longer
  resolve to anything
- Groups with the app in BoundTo keep the entry, but it no longer has
  effect

So before deleting: re-link or delete the dependent clients, scopes,
and roles first.
