# Declarative Realm Provisioning

Provision a **complete realm from a single JSON document** — apps, OAuth
APIs/scopes/clients, roles, users, groups and realm settings — in one call,
at runtime. Think *realm-as-code*: instead of clicking (or scripting dozens of)
admin API calls, you `POST` a **manifest** and Modgud materialises the whole
realm by running the same operations the admin UI uses.

::: tip Prefer clicking over JSON?
The interactive counterpart is [Configuration Drafts](configuration-drafts): every save in the admin UI stages onto a draft — which *is* one of these manifests — and you review the exact change plan before applying. Uploading a manifest as a draft is the reviewed import path.
:::

It's built for three jobs:

- **Bootstrap a realm reproducibly** — keep a realm's shape in version control and
  re-apply it.
- **Per-test realms** — an app's integration suite spins up a fresh, isolated realm
  per run (every realm is a physically separate database, so tests run in parallel),
  then tears it down.
- **Agents / automation** — a machine can fetch the [contract schema](#discover-the-schema)
  and author a valid manifest without reading any source.

## Two surfaces — pick by who's calling

The same manifest format is applied through **two** surfaces, differing in scope and
who's allowed:

| | Control-plane provisioning | Per-realm self-service |
|---|---|---|
| **For** | Operators managing the deployment | A realm's own admin (delegate this) |
| **Path** | `/api/admin/realms/*` | `/api/admin/realm-config/*` |
| **Runs on** | Control-Plane realm only (404 elsewhere) | The realm's own host (any realm) |
| **Permission** | `realm:write` on the `control-plane` app | `realm:admin` **in that realm** |
| **Can** | Create / update / export / **delete any** realm | Update + export **its own** realm (incl. prune) |
| **Cannot** | — | Create or delete realms; touch another realm |

If you run a **shared** Modgud and want to hand one realm to an app team (or an
agent) so they manage *only* that realm without operator powers, use
[per-realm self-service](#per-realm-self-service). For full lifecycle control
(creating/removing realms), use the control-plane surface below.

## Control-plane endpoints

All under `/api/admin/realms`, all requiring **`realm:write`** on the
`control-plane` app (the `realm:admin` bypass also grants it), and only on the
Control-Plane host ([404 elsewhere](../concepts/control-plane)):

| Method | Path | What it does |
|---|---|---|
| `POST` | `/` | Create the **realm shell** — slug, domains, first admin. A manifest carries no realm identity, so this is its own call. |
| `POST` | `/{slug}/apply` | **Merge** a manifest into an existing realm (upsert per entity). Never drops the database. |
| `POST` | `/{slug}/apply?prune=true` | **Full sync** — like apply, then delete entities present in the realm but absent from the manifest. The first call answers with the plan and a token instead of deleting — see [a pruning apply asks first](#a-pruning-apply-asks-first). |
| `GET` | `/{slug}/export` | Export the realm as a manifest (structure-only — never secrets or password hashes). |
| `GET` | `/manifest-schema` | The JSON Schema for the manifest (see [below](#discover-the-schema)). |
| `DELETE` | `/{slug}?hard=true` | **Hard-delete** — drop the tenant database. Without `?hard=true` it's the reversible soft-delete. |

Authenticate as a Control-Plane admin (cookie or bearer) before calling these —
e.g. `POST /api/account/login` for a cookie session.

## Discover the schema

You don't have to guess property names. The full, machine-readable **JSON Schema**
of the manifest — every field, its type, what's required, a description per field,
and a worked example — is served live:

```http
GET /api/admin/realms/manifest-schema      (realm:write)
```

The schema is **generated from the live manifest type** using the API's own JSON
settings, so it can never drift from what `import`/`apply` actually accept. It's
gated with the same permission as import/apply: only a caller who could apply a
manifest may fetch its schema.

```bash
curl -b cookies.txt https://<control-plane-host>/api/admin/realms/manifest-schema
```

Point any JSON-Schema-aware tool (or an agent) at the result and it can validate
and author manifests directly.

## The manifest at a glance

A manifest is one object holding optional entity lists. It carries **no realm shell** —
the target is named by the route, which is what lets the same file apply to any realm.
Create the realm itself with `POST /api/admin/realms`, then apply.

**Identity is the `Id`** ([ADR 0024](/decisions/0024-a-manifest-identifies-by-id-never-by-name)).
An entry updates an existing entity only when its `Id` names one; otherwise it creates —
and a name already taken there fails loudly rather than quietly rewriting a stranger that
happens to share it. Two forms of `Id`, and nothing else:

- a **real id** (ShortGuid or Guid), which is what an export writes on every entity;
- a **`#handle`** (`"#billing"`), a document-local name for an entity *this same file*
  creates. It is never stored: the server assigns a fresh id and resolves every `#`
  reference in the same run. The apply returns the mapping in `AssignedIds`, so a
  hand-written file can be made idempotent after one run without exporting first.

`Id` stays optional — omit it for an entity nothing in the file refers to.

**Cross-references between entities follow the same rule:**

- Groups list **`Members`** (users) and **`Roles`**; positions list **`Grants`** (users).
  Each entry is a real id (`{ "Key": "acme/Author", "Id": "…" }`, where the `Key` is a
  *verified hint* — never followed, and reported when it disagrees), or a `"#handle"`.
  A **bare name is an error**: the "acme/Author" in the target realm need not be the one
  the file was written against. Exports write the object form. Group membership is the
  *only* way users get roles.
- A real id the target realm does not have is **skipped and reported**; a `#handle` the
  file never declares is an **error**. A missing target is a fact about the target; a
  dangling handle is the file contradicting itself.

**Names that are vocabulary, not identity**, stay names — they are what tokens and
permission strings actually carry:

- APIs / scopes / clients / roles reference an app by its **`Slug`**.
- Permissions are addressed as **`resource:action`** (e.g. `invoice:read`).
- Scope names and API audiences are what clients request on the wire.
- Roles read as **`<app slug>/<name>`** (a realm-admin role as its bare `Name`) and login
  providers by their **`Slug`** — readable keys, never the thing an apply matches on.

```jsonc
// No realm shell: the target comes from the route. Ids below are '#handles' —
// this file CREATES everything, and the handles are how its entities point at
// each other. An export of the result carries real ids in the same places.
{
  "Settings": { /* optional realm-settings patch (self-reg, sessions, native grants, …) */ },
  "Apps":    [ { "Slug": "acme", "DisplayName": "Acme",
                 "Permissions": [ { "Resource": "invoice", "Action": "read" } ],
                 "Settings": { /* optional per-App override: Origin (host routing), branding, … */ } } ],
  "Apis":    [ { "Name": "acme-api", "App": "acme",
                 "Permissions": [ { "Resource": "invoice", "Action": "read" } ] } ],
  "Scopes":  [ { "Name": "invoice.read", "App": "acme", "Resources": ["acme-api"] } ],
  "Clients": [ { "ClientId": "acme-web", "ClientType": "confidential",
                 "RedirectUris": ["https://acme.example.com/cb"],
                 "Scopes": ["openid", "invoice.read"],
                 "AllowedGrantTypes": ["authorization_code", "refresh_token"],
                 "Apps": ["acme"] } ],
  "Roles":   [ { "Id": "#acme-admin", "Name": "acme-admin", "App": "acme",
                 "Permissions": [ { "Resource": "invoice", "Action": "read" } ] } ],
  "Users":   [ { "Id": "#alice", "Email": "alice@acme.example.com", "UserName": "alice" } ],
  "Groups":  [ { "Name": "Admins", "Members": ["#alice"], "Roles": ["#acme-admin"] } ],
  "ServiceAccounts": [ { "AccountName": "ci.deploy", "Purpose": "Deploy pipeline",
                         "Credentials": [ { "ClientId": "ci.deploy.main",
                                            "Scopes": ["invoice.read"], "Apps": ["acme"] } ] } ],
  "LoginProviders": [ { "Slug": "corp-idp", "Flavor": "GenericOidc", "DisplayName": "Corp IdP",
                        "ClientId": "modgud", "ClientSecret": "<from the upstream IdP>",
                        "FlavorData": { "MetadataUri": "https://idp.example.com/.well-known/openid-configuration" } } ],
  "Positions": [ { "AccountName": "gate.porter", "Grants": ["#alice"],
                   "TerminalPolicy": { "Enabled": true,
                                       "AllowedActivationProofs": ["personal-passkey"],
                                       "AllowedDeviceBindings": ["dpop"],
                                       "StaffingSessionLifetimeMinutes": 60,
                                       "MaximumStaffingSessionLifetimeMinutes": 480 } } ]
}
```

Positions require the `PositionTerminals` feature flag; terminal **slots** (device
enrollments and their one-time-secret clients) are credential material, not config —
provision them through the position/terminal admin APIs after import.

See the [schema](#discover-the-schema) for every field and its meaning.

## Quickstart

```bash
AUTH=https://<control-plane-host>

# 1) Log in as a Control-Plane admin (cookie)
curl -c cookies.txt -X POST "$AUTH/api/account/login" \
  -H 'Content-Type: application/json' \
  -d '{"UserName":"admin","Password":"<password>"}'

# 2) Create the realm SHELL — slug, domains, first admin. A manifest carries
#    no realm identity, so where it lands is decided here, not in the file.
curl -b cookies.txt -X POST "$AUTH/api/admin/realms" \
  -H 'Content-Type: application/json' \
  -d '{"Slug":"acme","DisplayName":"Acme","Domains":["acme.example.com"],
       "InitialAdmin":{"UserName":"admin","Email":"admin@acme.example.com"}}'

# 3) Fill it from the manifest → generated client secrets + the ids the
#    '#handles' were given
curl -b cookies.txt -X POST "$AUTH/api/admin/realms/acme/apply" \
  -H 'Content-Type: application/json' -d @manifest.json
# → {"Slug":"acme","PrimaryDomain":"acme.example.com",
#    "ClientSecrets":{"acme-web":"…"},"AssignedIds":{"#alice":"…","#acme-admin":"…"}}

# 4) Tear it down
curl -b cookies.txt -X DELETE "$AUTH/api/admin/realms/acme?hard=true"
```

::: tip Client secrets
Confidential clients — and service-account credentials — get a **generated secret
returned only at create** (in `ClientSecrets`, keyed by client id). Store it then —
there's no way to read it back later. Existing clients keep their secret across a
later `apply`.
:::

## Apply: merge vs. prune

`apply` is a **merge-patch** (in the spirit of [RFC 7386](https://www.rfc-editor.org/rfc/rfc7386)) —
the same [write semantics](/reference/#write-semantics) as the admin API:
a field **absent** from the manifest is left unchanged (it takes the shipped default
only on create), while every **present** field is applied — an explicit `null` **clears**
the stored value, and `[]` clears a list. Concretely:

- Boolean flags have no clear — omit or `null` both mean "unchanged"; `true`/`false` sets.
- Optional scalars (display names, descriptions, token lifetimes, branding fields, …):
  omitted = unchanged, `null` = clear back to the default, value = set.
- App links (`App` on an API or scope): omitted = unchanged, `null` = detach.
- Lists (redirect URIs, scopes, group members, position grants, …): omitted = unchanged,
  `[]` = clear, non-empty = replace the full list.
- App-catalog permission ids are preserved across updates, so unchanged permissions
  keep their grants.

Add **`?prune=true`** to make it a full sync: after the merge, entities in the realm
that are *absent* from the manifest are deleted (in dependency order). To prevent a
manifest from locking a realm out, prune **never deletes** the system app, auto-seeded
standard scopes, terminal-managed clients, the built-in Internal login provider, a
service account itself or the credentials of a service account the manifest does not
declare, or anything conferring `realm:admin` (a realm-admin role, any current admin
user, or an admin-conferring group).

### A pruning apply asks first

The admin UI has always shown a plan with deletions in red before an apply. The API
does the same, in two steps. A `POST …/apply?prune=true` that **would delete
something** does not delete it — it answers **`409`** with the plan, a confirmation
token, and a review link:

```jsonc
{
  "Error": "Manifest.ConfirmationRequired",
  "Message": "This apply would DELETE 3 entit(ies) from realm 'acme'. Repeat the call with ?confirm=<ConfirmationToken> to go ahead, or send ReviewUrl to someone who should decide — it opens this exact change as a draft.",
  "ConfirmationToken": "CfDJ8…",
  "DraftId": "3ce3d9a3-…",
  "ReviewUrl": "https://acme.example.com/admin/realm-config?draft=3ce3d9a3-…",
  "Plan": { "Sections": [ { "Name": "clients", "Entries": [ { "Key": "old-web", "Action": "delete", … } ] }, … ] }
}
```

Two ways forward, and a script can pick either:

- **Confirm** — repeat the *same* call with `&confirm=<ConfirmationToken>`. The token is
  valid for **15 minutes** and bound to the realm, to the manifest as sent, and to the
  exact set of deletions shown. If the manifest or the realm changed in between so that
  a *different* set would now be deleted, the token is refused
  (`Manifest.ConfirmationStale` / `Manifest.ConfirmationPayloadMismatch`) and the caller
  has to look again; an expired or foreign token answers `Manifest.ConfirmationExpired` /
  `Manifest.ConfirmationRealmMismatch`.
- **Hand it to a human** — the pending apply is also **parked as a shared draft** in the
  target realm, named *Pending apply (prune) — {time}, by {caller}*. Mail the `ReviewUrl`
  to whoever should decide: it opens the ordinary [draft workspace](configuration-drafts)
  with the deletions in red, and they apply or discard it there. A parked pruning draft
  prunes however it is applied — that is what the caller asked for.

A pruning apply that would delete **nothing** is not destructive and runs on the first
call; an apply without `?prune=true` never deletes and is never gated. None of this stops
a script from confirming blindly — nothing can, any more than the UI can stop someone
clicking through without reading. It makes the information unavoidable, which is the
part that can be controlled.

## Export

`GET /{slug}/export` returns the realm as a manifest — the inverse of import. It is
**structure-only**: it never emits client secrets, login-provider secrets, or password
hashes (those are one-way or encrypted), and it omits auto-seeded standard scopes /
system apps / the built-in Internal login provider / terminal-managed clients / terminal
slots. Service-account credentials are not in `Clients` either — they travel under the
account that owns them (see [service accounts](#service-accounts-and-their-credentials)).

It also leaves behind the settings fields that name entities by **raw id** — the
self-registration `DefaultGroupIds`, an App's `LoginProviderIds`, and the branding
`LogoAssetId` / `FaviconAssetId`. Those are realm-local *wiring*, not portable
configuration: the id means nothing in another realm, so carrying it either fails the
whole apply (asset and provider ids are validated against the realm) or stores a dangling
reference in silence (group ids are not). Omitted means **unchanged** under merge-patch,
so re-applying an export into its own realm leaves that wiring exactly as it was — set it
there, in the realm's own settings. Manifests carry a realm's *entities*. This is deliberate — it is *not* a backup (a real backup
needs the whole tenant database). Its purpose is **get-config → edit → re-apply**:
export a realm, add a user password or a provider secret, tweak a setting, and `POST`
it back to `/{slug}/apply`. Because confidential clients regenerate a secret on import
and users can be created passwordless, a structure-only manifest still re-applies into
a fully working realm.

### Stable ids across environments

Every exported entity carries its **`Id`** (ShortGuid), and the apply **pins that id
at create** — importing an export into another instance recreates every app, API,
scope, client, role, user, group, service account, login provider and position with
the *exact same id*. A stage → prod transfer therefore keeps every id consuming
applications persist as their foreign key — nothing has to be re-linked. The rules:

**The `Id` names the entity**, and the natural key (slug, name, client id, account name)
is ordinary data. It is also the *only* thing an entry is matched by — see
[ADR 0024](/decisions/0024-a-manifest-identifies-by-id-never-by-name) for why a name
cannot carry identity across two realms that grew independently:

- The id names a **live** entity → that entity is **updated** to the entry's values. If
  its natural key differs and the type can be renamed (roles, groups, users, service
  accounts, positions), the apply **renames** it — an export → edit-the-name → import
  round trip is a rename, not a duplicate. The plan shows it as `Name: old → new` plus a
  note, so you always see *which* entity is being renamed before you apply.
- Some natural keys can't change, because other systems address the entity by them: an
  **app slug**, a **client id**, a **scope or API name** (the API name is the `aud`
  claim), a **login-provider slug** (it owns the callback URLs). If the id names one of
  those and the entry's key differs, the entry fails with both ways out spelled out —
  fix the key to match, or drop the `Id` to create a separate entity.
- The id names a **deleted** entity → the apply **revives** it, under the entry's values
  (name included). Deleting is a soft delete, so the id and its history are still there.
  This is what makes "transfer to prod → something broke → delete → fix → re-import the
  same config" end where it started.
- The id names an entity of a **different kind** → conflict (`*.PinnedIdTaken`), and the
  plan flags that entry as an error. Appending one kind's events onto another's stream
  would corrupt it, so this is the one collision with no automatic resolution.
- **Users are the exception to reviving**: deleting a user runs the account lifecycle
  (recycle bin, grace period, GDPR purge), so a manifest never revives one. Re-importing
  a binned user's id fails with a message naming the way out — restore the user from the
  bin, then re-apply (the apply then updates it, id intact).
- Omit `Id`, or use a `#handle`, and the entry **creates**. If its natural key is already
  taken in the target, the apply fails with that type's own error (`App.DuplicateSlug`,
  `Group.NameTaken`, `Role.NameTaken`, …) instead of updating whatever holds the name. The
  plan shows this as an `error` entry beforehand, so a collision is seen in review rather
  than at deploy time.

::: warning A partial export can add to a foreign realm, not update it
Applying a manifest to a realm it was **not** exported from can only create. That is the
point of the rule — a name collision there is a *different* entity, and updating it would
be silent and, because reference lists replace, potentially a privilege change. To update
across environments, carry the ids: export the target, edit, re-apply.
:::

### Service accounts and their credentials

A service account exports with its **credentials** — `ServiceAccounts[].Credentials`,
each one the shape of a `client_credentials` client: `ClientId`, `Id`, `DisplayName`,
`Scopes`, `Apps`, `Enabled`, `AccessTokenType`, `AccessTokenLifetime`. They are nested
under the account rather than listed in `Clients` because a credential has no meaning
apart from its account, and the account has to exist before a credential can be bound
to it — under the account, that ordering holds by construction.

**Secrets still do not travel, and there is deliberately no field for one.** A manifest
gets committed, copied and mailed around. On apply, a credential the target lacks is
issued through the same operation the service-account admin uses, with a **fresh
secret returned once** in `ClientSecrets` (keyed by client id) — exactly as an ordinary
confidential client's is. A credential whose `Id` names a live one is updated in place;
one without an `Id` creates, and a taken client id fails loudly rather than adopting
another client.

The account itself is **never pruned** — deleting one kills every credential it owns,
so that stays a deliberate action in the service-account admin. Its credentials *are*
pruned, but only when the manifest **declares the account**: an account the file never
mentions keeps every credential it has, because otherwise forgetting to list an account
would quietly cut off whatever authenticates as it. The plan shows a credential deletion
in red like any other, and a [pruning apply asks first](#a-pruning-apply-asks-first).

One thing a diff cannot show is an absence, so the plan states it outright: an account
that arrives with **no credentials** carries a note saying a machine pointed at it cannot
authenticate until one is issued, and each new credential notes that its secret is
returned once.

## Per-realm self-service

On a **shared** deployment you often want to delegate one realm to its owner — an app
team or an agent — so they can fully manage *that* realm's config and entities, but
**not** create or delete realms and **not** see any other realm. That is exactly what a
**`realm:admin` in that realm** can do, through `/api/admin/realm-config/*`:

| Method | Path | What it does |
|---|---|---|
| `GET`  | `/api/admin/realm-config/manifest-schema` | The manifest JSON Schema (identical to the control-plane one). |
| `GET`  | `/api/admin/realm-config/export` | Export **this** realm as a manifest. |
| `POST` | `/api/admin/realm-config/apply` | Apply a manifest to **this** realm (merge; `?prune=true` = full sync within the realm, [asks first](#a-pruning-apply-asks-first) when it would delete). |

- **Scope is the calling realm** — resolved from the request host, never from a slug in
  the body. A manifest whose `Realm.Slug` names a *different* realm is rejected
  There is no realm-create and no realm-delete here — realm
  lifecycle stays control-plane-only.
- **Permission**: `realm:admin` in the realm being called. Nothing control-plane.
- **Same engine, same protections** as the control-plane path: prune is bounded to the
  realm, asks first when it would delete, and never removes the system app, standard
  scopes, the credentials of an undeclared service account, or any `realm:admin` path — so
  a manifest can't lock the realm out.

### Delegating a realm

To grant someone management of exactly one realm:

1. **Create the realm** (control-plane: `POST /api/admin/realms`, or the admin UI).
2. **In that realm, give the principal `realm:admin`** — either a **user** (interactive)
   or a **service account** (machine / agent, `client_credentials`). Both work. For a
   bearer caller, Modgud evaluates `realm:admin` live from the principal's current groups
   and roles; the permission is not copied into the token.
3. For machine access, allow the protected `modgud.management` scope on the linked
   Service Account credential and request a token for resource
   `urn:modgud:management-api`.
4. Call `/api/admin/realm-config/*` against the realm's host with that cookie or bearer.

That credential can do everything to *its* realm's config and **nothing** to any other
realm — and cannot create or delete realms.

```bash
REALM=https://acme.example.com   # the realm's own host

curl -c cookies.txt -X POST "$REALM/api/account/login" \
  -H 'Content-Type: application/json' -d '{"UserName":"realm-admin","Password":"<password>"}'

curl -b cookies.txt "$REALM/api/admin/realm-config/export"             # current config
curl -b cookies.txt -X POST "$REALM/api/admin/realm-config/apply" \
  -H 'Content-Type: application/json' -d @manifest.json                # apply edits (+ ?prune=true)
```

For unattended provisioning, use the fixed [Management API contract](../integrate/management-api)
instead of an admin cookie:

```bash
TOKEN=$(curl -sS -X POST "$REALM/connect/token" \
  -d 'grant_type=client_credentials' \
  -d 'client_id=<linked-service-account-client>' \
  -d 'client_secret=<secret>' \
  -d 'scope=modgud.management' \
  -d 'resource=urn:modgud:management-api' | jq -r '.access_token')

curl -H "Authorization: Bearer $TOKEN" \
  "$REALM/api/admin/realm-config/export"
curl -X POST -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d @manifest.json \
  "$REALM/api/admin/realm-config/apply"
```

## Provisioning from a .NET test suite

For .NET apps, the **`Modgud.Provisioning.TestKit`** package wraps these endpoints
with automatic teardown — give each test a unique slug and dispose to hard-delete:

```csharp
var http = new HttpClient(new HttpClientHandler { CookieContainer = new() })
    { BaseAddress = new Uri("https://<control-plane-host>") };
await http.PostAsJsonAsync("/api/account/login",
    new { UserName = "admin", Password = "<password>" });

var kit = new ModgudProvisioningClient(http);
await using var realm = await kit.ImportRealmAsync(spec, manifest);  // dispose → hard-delete
var secret = realm.SecretFor("acme-web");
```

## Caveat — using a provisioned realm for OAuth flows

Creating, updating and deleting realms uses the current Control-Plane host. But
**driving OAuth flows _against_ a provisioned realm is
host-routed**: Modgud resolves the tenant from the request's `Host` header
(`Realm.Domains`), and each realm's issuer is `https://{PrimaryDomain}`. So a token
request for a realm must arrive with that realm's host. For machine flows
(`client_credentials`, native grants, introspection) that's just a `Host` header; for
browser authorization-code flows the realm host must be reachable and match the issuer
you configure in the client.
