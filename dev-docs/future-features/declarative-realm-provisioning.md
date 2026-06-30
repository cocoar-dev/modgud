# Declarative Realm Provisioning

**Status:** Shipped (Stage 1 — import / in-place update / hard-delete /
structure-only export; Stage 2 — prune). This page is the design-of-record;
promote it to a public `/admin/` or `/integrate/` page when the feature gets
user-facing docs.

**Why:** Bernhard builds .NET apps that use Modgud as their OAuth/OIDC server.
Standing up a realm to test an app — clients, scopes, APIs, users, roles,
groups, settings — by hand through the admin API is slow and unrepeatable. The
goal is **declarative realm provisioning at runtime**: hand Modgud one JSON
document and have it materialise (or update, or tear down) a complete realm
in-process, fast enough to do per-test, in parallel. The risk gate — and the
piece the owner valued most — was a **prod-safe hard-delete that actually drops
the tenant database**.

## The shape

Everything hangs off the existing control-plane realms group
(`/api/admin/realms`, gated by `RequireControlPlaneFilter` +
`RequiresPermission("realm:*", AppSlugs.ControlPlane)` — there is no anonymous
provisioning):

| Verb | Route | Does |
|------|-------|------|
| `POST` | `/import` | Create a brand-new realm from a manifest. Slug must NOT exist. All-or-nothing: a failed import hard-deletes the partial realm. → `201` + `RealmImportResult` (incl. minted client secrets). |
| `POST` | `/{slug}/apply` | In-place merge/upsert into an EXISTING realm. Never drops the DB. Route slug must equal the manifest slug (`Manifest.SlugMismatch` → `400`). |
| `POST` | `/{slug}/apply?prune=true` | As above, then a **full sync**: delete entities present in the realm but absent from the manifest (see [Prune](#prune-full-sync)). |
| `GET` | `/{slug}/export` | Structure-only manifest of the realm (the inverse of the applier). Never emits secrets / password hashes. `realm:read`. |
| `DELETE` | `/{slug}?hard=true` | Hard-delete: drop the tenant database. Default (`hard=false`) is the existing soft-delete. |

`Import` vs `Apply` is deliberate: import creates (rolls back on failure), apply
merges (each op commits its own unit; safe to re-apply after fixing the
manifest). **`UpdateRealm` is an in-place merge, NEVER `Remove + Import`** —
dropping the tenant DB would discard the realm's signing keys, wipe the
OpenIddict token store, and change every user's `sub`, invalidating all issued
tokens.

## The governing invariant

> **Exactly one canonical write path per mutation.** The applier reimplements
> nothing — for each entity change it calls the *same* application operation the
> admin UI/API uses, so the manifest path and the manual path can never drift.

Modgud is a hybrid (events for state/projections, but **imperative
orchestration** for side-effects like token revocation and SignalR dispatch), so
"just fire the raw events" would skip those side-effects — never do it. Reuse
the operation:

| Section | Canonical op |
|---------|--------------|
| Realm shell | `IRealmProvisioningService.CreateRealmAsync` (global store, no tenant ctx) |
| Settings | `IRealmSettingsService.PatchAsync` |
| Apps (+catalog) | `AppAdminService.Create/Update/DeleteAppAsync` |
| APIs / scopes / clients | `OAuthAdminService.Create/Update/Delete{Api,Scope,Client}Async` |
| Roles | `RoleAdminService.Create/Update/DeleteRoleAsync` |
| Users | `CreateUserCommand` / `UpdateUserHandler` / `SetUserPasswordHandler` / `DeleteUsersCommand` |
| Groups | `Create/Update/DeleteGroupHandler` |

Stage 2 (prune) added the `Delete*` ops; several lived inline in their HTTP
endpoints and were consolidated onto the services/commands first so the applier
could reuse them (see the Atlas note
`engineering/realm-provisioning-write-path-divergences`).

## The manifest schema

Cross-references use stable **keys**, never server-generated ids — apps by
`slug`, roles/users by `key`, permissions by `resource:action` — mirroring the
`demo-seed.json` contract. The applier resolves keys → ids in dependency order:
**apps → apis/scopes/clients → roles → users → groups**.

```jsonc
{
  "Realm":    { /* CreateRealmDto: Slug, DisplayName, Domains[], InitialAdmin{} */ },
  "Settings": { /* UpdateRealmSettingsDto — optional; all 9 sections */ },

  "Apps": [
    { "Slug": "acme-app", "DisplayName": "Acme",
      "Permissions": [ { "Resource": "acme", "Action": "read" } ] }
  ],
  "Apis": [
    { "Name": "acme-api", "App": "acme-app",          // App is a slug
      "Permissions": [ { "Resource": "acme", "Action": "read" } ],  // resolve into the app's catalog
      "Scopes": [], "UserClaims": [],
      "Enabled": null, "AllowDynamicRegistration": null }           // bools nullable — see merge semantics
  ],
  "Scopes": [
    { "Name": "acme.read", "App": "acme-app", "Resources": ["acme-api"],
      "Enabled": null, "Required": null, "Emphasize": null, "ShowInDiscoveryDocument": null }
  ],
  "Clients": [
    { "ClientId": "acme-web", "ClientType": "confidential",
      "RedirectUris": ["https://acme.test/cb"], "Scopes": ["openid", "acme.read"],
      "AllowedGrantTypes": ["authorization_code", "refresh_token"],
      "Apps": ["acme-app"], "Roles": [], "WebAuthnRpId": null,
      "Enabled": null, "RequireConsent": null }                     // ClientSecret minted at create only
  ],
  "Roles": [
    { "Key": "acme-admin", "Name": "acme-admin", "App": "acme-app",
      "IsRealmAdmin": false,
      "Permissions": [ { "Resource": "acme", "Action": "read" } ] }
  ],
  "Users": [
    { "Key": "alice", "Email": "alice@acme.test", "UserName": "alice",
      "Password": null, "EmailConfirmed": false }                   // created passwordless if Password null
  ],
  "Groups": [
    { "Name": "Admins", "Members": ["alice"], "Roles": ["acme-admin"],
      "MembershipMode": "Manual", "BoundTo": null,                  // BoundTo null → defaults to [modgud]
      "ExternallyDrivable": false }
  ]
}
```

The C# record is `RealmManifest` in
`Modgud.Api/Features/Admin/Provisioning/RealmManifest.cs` — the authoritative
schema. The TestKit ships a client-side mirror (`Modgud.Provisioning.TestKit`).

## Field-merge semantics (apply = patch)

`apply` (without prune) is the desired state **for the fields it carries**:

- **Boolean flags are nullable** (`bool?`): omitted = no change on update, the
  shipped default on create (`Enabled` / `ShowInDiscoveryDocument` → `true`, the
  rest → `false`). This is the surgical-patch wire form — identical to
  `Optional<bool>` for value types but without forcing the global JSON resolver
  onto `AddOptionalAware`. (`Optional<T>` infra exists but is for internal
  Optional-typed DTOs; the manifest IS the HTTP body, so `bool?` is the
  consistent shape — same call the `ProfileEndpoints` partial-update makes.)
- **Scalar strings** replace when present; **null = no change** (never clears).
- **Non-empty lists** replace; an **omitted/empty list = no change** (apply
  sets and changes lists, but never clears one to empty — that stays an admin-API
  operation, or use prune).
- **App-link** (`null`) = no change (never detaches).
- **App-catalog ids are preserved by `resource:action`** across an update, so an
  unchanged permission keeps its id and doesn't trip the catalog-delete block
  (which guards FK references from roles / resource servers).
- **Client secret** is minted only at create; an existing client keeps its secret
  (rotate via the dedicated endpoint).
- **Set-a-password on apply**: a `Password` on an EXISTING user IS applied (via
  the canonical `SetUserPasswordHandler`, which carries the kill-switch revoke) —
  this is what makes the *export (passwordless) → add a password → apply* flow
  work. New users get theirs at create.

## Prune (full sync)

`apply?prune=true` is the k8s `apply --prune` model: after the upsert, delete
every entity that exists in the realm but is **absent from the manifest**, via
its canonical delete op, in **reverse-dependency order** so a dependent is gone
before the app/role it points at:

```
clients → scopes → apis → groups → users → roles → apps
```

An app still referenced by a manifest-KEPT role / resource server correctly
errors (the App-delete reference block). Protection checks run AFTER the upsert,
so they see the realm's desired post-merge role graph.

### Never pruned — lockout + infrastructure protection

The chosen rule is the robust superset of "System + last admin": protect **all**
admins, so no manifest can lock the realm out.

- **System app** (`App.IsSystem`) — auto-seeded.
- **Standard scopes** (`StandardScopes.IsStandard`) — auto-seeded (`openid`, …).
- **Service-account-linked clients** (`OAuthApplicationState.LinkedServiceAccountId`)
  — auto-managed, not manifest-modelled.
- **Any realm-admin role** (`PermissionRole.IsRealmAdmin`).
- **Any user who currently holds `realm:admin`** (checked via
  `IPermissionService.HasPermissionAsync(..., "realm:admin")`, so an admin not
  listed in the manifest survives).
- **Any group that confers `realm:admin`** (via
  `GroupMembershipGuards.GroupConfersRealmAdminAsync`). This is the load-bearing
  refinement: the user-admin check is `BoundTo`-gated, so without it pruning an
  admin's group could silently strip the admin path even though the role + user
  survive.

User delete is the canonical **recycle-bin soft-delete** (deactivate + pending,
not a hard erase) — the same op the admin "delete user" uses.

## Structure-only export + the secrets stance

A real backup/restore needs the whole tenant DB (events + signing keys + token
store) and is explicitly **not** what this feature is. What's wanted is (1)
create-from-JSON and (2) get-config → edit → set-a-password → apply. So
**export is structure-only**:

- It NEVER emits client secrets or password hashes (one-way), and the write-only
  captcha secret is surfaced only as a `CaptchaSecretSet` flag, never the
  plaintext.
- It omits auto-seeded standard scopes, system apps, and SA-linked clients (which
  can't be cleanly re-applied).
- All 9 realm-settings sections ARE exported (reverse-mapped read → patch shape),
  so settings round-trip.

The key fact that makes structure-only clean: **no entity fails without a
credential.** Confidential clients auto-generate a secret (returned in
`RealmImportResult.ClientSecrets`), users are created passwordless. So a
structure-only import yields a fully working realm; the missing credentials are
exactly what you'd reissue on a clone.

## Implementation gotchas (load-bearing)

- **Tenant routing.** `TenantedSessionFactory` prefers the AsyncLocal
  `TenantContext` over the ambient (control-plane) `HttpContext`. The applier
  runs the per-tenant config inside `TenantContext.Enter(slug)` + a **fresh DI
  scope**, so direct-service writes land in the NEW realm even though the call
  runs on the control-plane host.
- **Wolverine commands resolve their session from the message-envelope tenant**,
  not `TenantContext` → users use `bus.InvokeForTenantAsync(slug, ...)`. A plain
  `InvokeAsync` inside `TenantContext.Enter` opens a tenant-less session ("Default
  tenant does not supported").
- **Groups + user-update + all prune deletes use a PLAIN (non-Wolverine) session,
  NOT the bus.** `GroupCreated/Updated/Deleted`, `UserUpdated`, and
  `UserDeactivated` have durable `ReferenceSync` forwarders (`UseFastEventForwarding`
  + `UseDurableInbox`) that, under `InvokeForTenantAsync`, would write
  `wolverine_*_envelopes` tables a fresh tenant DB lacks. A plain session skips
  the forwarding (auto-membership re-derives at login). `CreateUser` is the
  exception that works via the bus — `userManager.CreateAsync` persists on a
  separate, non-outbox session.
- **Group `BoundTo` default.** The create *endpoint* applies
  `dto.BoundTo ?? [modgud]` before calling the command (the handler itself
  defaults null → `[]` = dormant). The applier mirrors that on create, so a
  manifest-provisioned admin group actually confers its roles instead of silently
  granting nothing.
- **Hard-delete.** `RemoveTenantAsync` (evicts the tenancy cache + disposes the
  data source + deletes the `mt_tenant_databases` row) → `DROP DATABASE … WITH
  (FORCE)` → remove the global Realm record + invalidate the realm cache. Refuses
  the control-plane realm. Caveat: re-creating the **same slug in the same
  process** fails (Weasel caches `NpgsqlDataSource` by connection string with no
  per-key eviction) — use unique slugs, or a custom evictable factory if in-process
  reuse is ever needed. See Atlas `engineering/realm-hard-delete-drop-database`.

## Test kit

`Modgud.Provisioning.TestKit` is a standalone, NuGet-able project with **zero
server deps** (its own manifest POCOs, the client-side mirror of the server
contract): `new ModgudProvisioningClient(httpClient).ImportRealmAsync(manifest)`
→ `ProvisionedRealm` (`Authority` / `PrimaryDomain` / `SecretFor(clientId)` /
`ApplyAsync` / `DisposeAsync` → hard-delete). Server error codes surface as
`ModgudProvisioningException.Code`. A token-minting helper is deliberately out of
v1 (the manifest can't model SA / `client_credentials` clients), so the consumer
drives auth flows with the exposed Authority/ClientId/Secret.

## Not in v1

- Login providers (OIDC/SAML) — a bus command with `JsonDocument? FlavorData`;
  follow the plain-session pattern if its event forwards durably.
- Per-user enabled-state (activate/deactivate) in the applier — its op is still
  endpoint-inline (see the write-path-divergences note); add when needed.
- OAuth client-delete token revocation — a real bug recorded in the
  write-path-divergences note, independent of the applier (both UI and prune call
  the same `DeleteClientAsync`, so prune introduces no new divergence). Fix needs
  either a DIP move of `IOAuthGrantRevoker` into Application or an event handler.
