# Local Modgud for app integration tests

Run a real Modgud locally so another app's integration tests can spin up a
**throwaway realm per test run** (clients, scopes, APIs, users, roles, groups,
settings) in seconds and tear it down after — using the declarative
realm-provisioning API (`import` / `apply` / `?prune=true` / hard-delete) and the
`Modgud.Provisioning.TestKit` client.

> This uses the **locally-built `modgud:local`** image, not `ghcr.io/cocoar-dev/modgud:beta`
> — the provisioning feature isn't on `:beta` yet (branch `feat/realm-declarative-provisioning`).

## At a glance

1. **Get an instance running** — build the `modgud:local` image, start the stack, and create
   the first control-plane admin ([§1 below](#1-one-time-setup)). The compose maps Modgud to
   `http://localhost:18080` (admin UI + `/api`; in-app docs at `/docs/`).
2. **Per test run** — using the admin you created, log in, fetch the manifest schema, import a
   realm with a **unique slug**, run your app's tests against it, then hard-delete it
   ([§2](#2-smoke-test-the-loop-curl) for curl, [§3](#3-app-side-recipe-the-modgudprovisioningtestkit)
   for the .NET `Modgud.Provisioning.TestKit`).

Realms are physically separate databases → fully isolated and parallel-safe. The host-routing
caveat for driving real OAuth flows against a provisioned realm is in
[§4](#4-caveat--using-the-provisioned-realm-for-oauth-flows).

## The manifest contract — fetch the schema

The import/apply body is a **realm manifest**. Its full JSON Schema — every field, its
type, what's required, and a per-field description + a worked example — is served live:

```
GET /api/admin/realms/manifest-schema      (control-plane auth, realm:write — same as import/apply)
```

So an agent doesn't need to guess property names: log in, `GET` the schema, and author a
valid manifest from it. The schema is generated from the server's own type, so it can never
drift from what the endpoint actually accepts. The shape in short:

- `Realm` (**required**) — slug, display name, routing `Domains[]`, `InitialAdmin`.
- `Settings` (optional) — realm-settings patch (self-reg, native grants, branding, …).
- `Apps[]` — permission namespaces (each a catalog of `resource:action` permissions).
- `Apis[]`, `Scopes[]`, `Clients[]`, `Roles[]`, `Users[]`, `Groups[]`.

Cross-references use **keys, not ids**: APIs/scopes/clients/roles point at an app by its
`Slug`; groups list `Members` (user keys) and `Roles` (role keys); permissions are addressed
as `resource:action`. Confidential clients get a generated secret back in the import result.

## 1. One-time setup

```powershell
# From the repo root — build the image (multi-stage: .NET + Vue admin + docs)
docker build -f docker/Dockerfile -t modgud:local .

# Start Postgres + Modgud (host port 18080; Postgres internal-only)
docker compose -f dev/app-testing/docker-compose.yml up -d

# Create the first control-plane admin. The system realm IS the control plane,
# so a realm:admin there can call the provisioning endpoints. This is the standard
# recovery-CLI bootstrap (see ../../docs/getting-started/first-time-setup.md for the
# concept). Pick any credentials you like — these are just an example.
docker compose -f dev/app-testing/docker-compose.yml exec modgud `
  dotnet Modgud.Api.dll recover bootstrap-admin `
    --email admin@local --username admin --password 'ABC12abc!'
```

Modgud is now at `http://localhost:18080` (admin UI + `/api`), control-plane admin
`admin` / `ABC12abc!`.

## 2. Smoke-test the loop (curl)

```bash
# Log in as control-plane admin, keep the cookie
curl -sS -c cookies.txt -X POST http://localhost:18080/api/account/login \
  -H 'Content-Type: application/json' \
  -d '{"UserName":"admin","Password":"ABC12abc!"}'

# Import a realm from a manifest → 201 + the minted client secret(s)
curl -sS -b cookies.txt -X POST http://localhost:18080/api/admin/realms/import \
  -H 'Content-Type: application/json' \
  -d '{
        "Realm": { "Slug": "acme-test", "DisplayName": "Acme Test",
                   "Domains": ["acme-test.localhost"],
                   "InitialAdmin": { "UserName": "admin", "Email": "admin@acme-test.local" } },
        "Apps":    [ { "Slug": "acme", "DisplayName": "Acme",
                       "Permissions": [ { "Resource": "acme", "Action": "read" } ] } ],
        "Clients": [ { "ClientId": "acme-web", "ClientType": "confidential",
                       "RedirectUris": ["https://acme-test.localhost/cb"],
                       "Scopes": ["openid"], "AllowedGrantTypes": ["authorization_code","refresh_token"],
                       "Apps": ["acme"] } ],
        "Users":   [ { "Key": "alice", "Email": "alice@acme.test", "UserName": "alice", "Password": "Passw0rd!23" } ]
      }'

# Tear it down (drops the tenant database)
curl -sS -b cookies.txt -X DELETE "http://localhost:18080/api/admin/realms/acme-test?hard=true"
```

## 3. App-side recipe (the `Modgud.Provisioning.TestKit`)

In the app-under-test's integration suite, point an authenticated `HttpClient` at
the container and let the kit manage the realm lifecycle. Give each test run a
**unique slug** — every realm is a physically isolated Postgres DB, so they run in
parallel.

```csharp
using Modgud.Provisioning.TestKit;

// 1) An HttpClient authenticated as control-plane admin (cookie auth).
var handler = new HttpClientHandler { CookieContainer = new(), UseCookies = true };
var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18080") };
await http.PostAsJsonAsync("/api/account/login",
    new { UserName = "admin", Password = "ABC12abc!" });

// 2) Provision a throwaway realm (dispose hard-deletes it).
var kit = new ModgudProvisioningClient(http);
await using var realm = await kit.ImportRealmAsync(new RealmManifest
{
    Realm   = new RealmSpec { Slug = $"acme-{Guid.NewGuid():N}", Domains = ["acme.localhost"] },
    Apps    = [ new RealmManifestApp { Slug = "acme", DisplayName = "Acme",
                Permissions = [ new("acme", "read") ] } ],
    Clients = [ new RealmManifestClient { ClientId = "acme-web", ClientType = "confidential",
                RedirectUris = ["https://acme.localhost/cb"], Scopes = ["openid"],
                AllowedGrantTypes = ["authorization_code", "refresh_token"], Apps = ["acme"] } ],
    Users   = [ new RealmManifestUser { Email = "alice@acme.test", UserName = "alice",
                Password = "Passw0rd!23" } ],
});

var clientSecret = realm.SecretFor("acme-web");   // returned only at import
// realm.ApplyAsync(updated)  → in-place merge/upsert
// (disposal at end of test → hard-delete, tenant DB dropped)
```

Reference the kit either as a NuGet package or a project reference to
`src/dotnet/Modgud.Provisioning.TestKit` — it has **zero** Modgud server deps
(ships its own manifest POCOs).

## 4. Caveat — using the provisioned realm for real OAuth flows

Creating / updating / deleting realms is fully turnkey: the control-plane
endpoints live on the system realm (Host `localhost`), so the cookie above is all
you need.

**Driving OAuth flows _against_ a provisioned realm is host-routed.** Modgud
resolves the tenant from the `Host` header (`Realm.Domains`), and each realm's
issuer is `https://{PrimaryDomain}`. So a token request for the `acme-test` realm
must arrive with `Host: acme-test.localhost`, and the issuer won't match
`http://localhost:18080`. For headless integration tests that's usually fine:

- **`client_credentials` / native grants / introspection** — point the request at
  `http://localhost:18080` with `Host: acme-test.localhost` (`*.localhost` resolves
  to 127.0.0.1 on Windows/macOS/most Linux). Works without a browser.
- **Authorization-code (browser) flows** — need the realm host reachable + an
  issuer scheme/port that matches what you configure in the client; doable but
  more setup. Out of scope for this convenience stack.

If your app needs the realm reachable on a clean host, add its domain to the
manifest (`Realm.Domains`) and map that host to `localhost` in your test runner's
hosts resolution.

## 5. Teardown

```powershell
docker compose -f dev/app-testing/docker-compose.yml down        # keep the volume
docker compose -f dev/app-testing/docker-compose.yml down -v     # nuke the data too
```
