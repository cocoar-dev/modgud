# TimeToDo.TestIdP

A minimal OpenID Connect server for local TimeToDo development. Powered by
OpenIddict, driven by a single JSON config file. Mirrors the OIDC surface
your real IdP (Entra, Okta, Keycloak) exposes — but you control the claims.

## Running

```bash
dotnet run --project src/dotnet/TimeToDo.TestIdP
# → http://localhost:5005
```

Quick check:

- Homepage: <http://localhost:5005>
- Discovery: <http://localhost:5005/.well-known/openid-configuration>

## Configuration

Two files are consulted at startup, in this precedence:

1. `data/test-idp-config.local.json` (git-ignored, for personal overrides)
2. `data/test-idp-config.json` (tracked, sensible defaults)

Or set `TESTIDP_CONFIG=<path>` to point at any other file.

### Shape

```json
{
  "Clients": [
    {
      "ClientId": "timetodo-dev",
      "ClientSecret": "dev-secret-rotate-me",
      "RedirectUris": [
        "http://localhost:8081/signin-oidc/<YOUR_IDPCONFIG_ID>"
      ]
    }
  ],
  "Users": [
    {
      "UserName": "alice",
      "Password": "test123",
      "Subject": "user-alice-001",
      "Claims": {
        "email": "alice@acme.com",
        "name": "Alice Anderson",
        "preferred_username": "alice",
        "groups": ["Admins", "Engineering"],
        "roles": ["Contributor"],
        "department": "IT"
      }
    }
  ]
}
```

Claim values can be scalars (string, number, bool) or arrays. Arrays become
multi-valued claims on the issued ID-token, which mirrors how Entra and Okta
emit `groups` and `roles`.

## Connecting TimeToDo to the TestIdP

1. Start the TestIdP: `dotnet run --project src/dotnet/TimeToDo.TestIdP`
2. In TimeToDo → Admin → Identity Providers → **Add provider**
3. Choose **Generic OIDC**, give it a name (e.g. "TestIdP")
4. Open the new config → **Connection** tab:
   - Discovery URL: `http://localhost:5005/.well-known/openid-configuration`
   - Client ID: `timetodo-dev`
   - Secret: rotate with `dev-secret-rotate-me`
5. Copy the **Redirect URI** shown in the modal (it includes the new config's
   GUID, e.g. `http://localhost:8081/signin-oidc/abc123…`)
6. Paste it into `test-idp-config.local.json` under the matching client's
   `RedirectUris`, then **restart the TestIdP** (the redirect URI list is read
   once on startup)
7. Back in TimeToDo admin: **Enable** the provider
8. Log out, reload the login page → a "Sign in with TestIdP" button appears
9. Click → pick `alice` → password `test123` → you're logged in as the
   JIT-created TimeToDo user

> **Why the copy-paste step?** OpenIddict validates the redirect URI exactly.
> Since your IdpConfig GUID is generated fresh, the TestIdP can't know it
> up-front. Register it once per IdpConfig you create; it takes 5 seconds.

## Adding more users / scenarios

Create a `data/test-idp-config.local.json` (git-ignored) and add extra users
with whatever claim shapes you need to test. Typical experiments:

- **No-groups user** — omit the `groups` claim and watch your auto-membership
  predicates return no memberships
- **MFA-satisfied user** — include `"amr": ["mfa", "otp"]` and watch TimeToDo
  skip the SecureSetup modal
- **Custom attribute** — add `"extension_costCenter": "EU-001"` and write a
  claims-transform script that surfaces it into your normalized shape

Restart the TestIdP after editing the JSON.
