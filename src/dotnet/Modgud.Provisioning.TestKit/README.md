# Modgud.Provisioning.TestKit

Spin up isolated [Modgud](https://github.com/cocoar-dev/modgud) realms from a declarative
manifest in your integration tests. A thin client over Modgud's control-plane provisioning
API (`import` / `apply` / hard-delete) that gives each test a real, throwaway realm with
automatic teardown.

## Usage

Point an `HttpClient` at a running Modgud instance, authenticated as a control-plane admin
(cookie or bearer), then:

```csharp
var client = new ModgudProvisioningClient(httpClient);

var manifest = new RealmManifest
{
    Realm = new RealmSpec { Slug = "acme-test", Domains = ["acme-test.localhost"] },
    Apps = [ new RealmManifestApp { Slug = "acme", DisplayName = "Acme",
        Permissions = [ new("acme", "read") ] } ],
    Clients = [ new RealmManifestClient {
        ClientId = "acme-web", ClientType = "confidential",
        RedirectUris = ["https://acme-test.localhost/callback"],
        Scopes = ["openid"], AllowedGrantTypes = ["authorization_code", "refresh_token"],
        Apps = ["acme"] } ],
    Users = [ new RealmManifestUser { Email = "alice@acme.test", UserName = "alice",
        Password = "Passw0rd!23" } ],
};

await using var realm = await client.ImportRealmAsync(manifest);

// Point the app-under-test at the realm:
var authority    = realm.Authority;                 // https://acme-test.localhost
var clientSecret = realm.SecretFor("acme-web");

// In-place updates (merge/upsert):
await realm.ApplyAsync(updatedManifest);

// Disposing hard-deletes the realm (drops the tenant database).
```

Run tests in parallel by giving each a unique slug — every realm is a physically isolated
database.

## Notes

- The realm is provisioned through the same canonical operations the Modgud admin UI uses,
  so the manifest path and the manual path can't drift.
- Client secrets are returned only at import (`ClientSecrets` / `SecretFor`). Existing
  clients keep their secret across `ApplyAsync`.
- Entity-level prune is not performed — entities absent from a manifest applied with
  `ApplyAsync` are left untouched.

Apache-2.0.
