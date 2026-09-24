# Modgud.Provisioning.TestKit

Spin up isolated [Modgud](https://github.com/cocoar-dev/modgud) realms from a declarative
manifest in your integration tests. A thin client over Modgud's control-plane provisioning
API (create realm / `apply` / hard-delete) that gives each test a real, throwaway realm
with automatic teardown.

## Usage

Point an `HttpClient` at a running Modgud instance, authenticated as a control-plane admin
(cookie or bearer), then:

```csharp
var client = new ModgudProvisioningClient(httpClient);

// The realm SHELL is a separate argument: a manifest carries no realm identity, so the
// same manifest provisions any realm you name here.
var spec = new RealmSpec { Slug = "acme-test", Domains = ["acme-test.localhost"] };

var manifest = new RealmManifest
{
    Apps = [ new RealmManifestApp { Slug = "acme", DisplayName = "Acme",
        Permissions = [ new("acme", "read") ] } ],
    Clients = [ new RealmManifestClient {
        ClientId = "acme-web", ClientType = "confidential",
        RedirectUris = ["https://acme-test.localhost/callback"],
        Scopes = ["openid"], AllowedGrantTypes = ["authorization_code", "refresh_token"],
        Apps = ["acme"] } ],
    Roles = [ new RealmManifestRole { Name = "acme-admin", App = "acme",
        Permissions = [ new("acme", "read") ] } ],
    Users = [ new RealmManifestUser { Key = "alice", Email = "alice@acme.test",
        UserName = "alice", Password = "Passw0rd!23" } ],
    // Members and roles are listed by key, the way a test wants to read them.
    Groups = [ new RealmManifestGroup { Name = "Admins",
        Members = ["alice"], Roles = ["acme-admin"] } ],
};

await using var realm = await client.ImportRealmAsync(spec, manifest);

// Point the app-under-test at the realm:
var authority    = realm.Authority;                 // https://acme-test.localhost
var clientSecret = realm.SecretFor("acme-web");
var aliceId      = realm.AssignedIds["#user:alice"];

// Disposing hard-deletes the realm (drops the tenant database).
```

Run tests in parallel by giving each a unique slug — every realm is a physically isolated
database.

## Identity: names in, ids out

Modgud identifies a manifest entity by its `Id`, never by its name
([ADR 0024](https://cocoar-dev.github.io/modgud/decisions/0024-a-manifest-identifies-by-id-never-by-name)):
the "alice" in a target realm need not be the alice a file was written against, so an
entry without an id **creates**, and a name already taken there fails loudly.

The kit keeps authoring name-based and does the translation for you. Every entity you
declare without an explicit `Id` gets a document-local **`#handle`** on the wire, and every
reference to a declared entity is rewritten to point at it. The result means exactly what
the test wrote — *the alice in this file* — and can never adopt a stranger.

The handles come back as real ids in `AssignedIds`, keyed `#user:alice`,
`#role:acme/acme-admin`, `#client:acme-web`, `#app:acme`, `#group:Admins`. That is how a
test learns an id it never chose:

```csharp
var roleId = realm.AssignedIds["#role:acme/acme-admin"];
```

### Applying a second time

Because a fresh manifest declares fresh handles, applying the same one twice **creates
twice** — and the second apply fails on the duplicate name. To update in place, carry the
ids back:

```csharp
var v2 = manifest with
{
    Apps = [ manifest.Apps[0] with
        { Id = realm.AssignedIds["#app:acme"], DisplayName = "Acme v2" } ],
};
var result = await realm.ApplyAsync(v2);
```

Set `Id` explicitly on any entity to pin a stable id across environments, or to address one
the target realm already has.

## Notes

- The realm is provisioned through the same canonical operations the Modgud admin UI uses,
  so the manifest path and the manual path can't drift.
- Client secrets are returned only at create (`ClientSecrets` / `SecretFor`). Existing
  clients keep their secret across `ApplyAsync`.
- References the target realm cannot resolve are **skipped, not fatal** — the apply
  reports them in `SkippedReferences`, and applies the rest.
- An apply never deletes — entities absent from a manifest applied with
  `ApplyAsync` are left untouched.

Apache-2.0.
