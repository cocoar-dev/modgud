# First-time setup

A fresh Modgud deployment starts with **zero realms and zero users**. Startup
creates only the master database, the tenant registry and the Global Store.
The first installation then creates:

- the first ordinary realm;
- that realm's tenant database and standard seed data;
- the first user and its `realm:admin` membership; and
- the `Realm.IsControlPlane` flag on that first realm.

There is no special runtime `system` realm. Every realm has the same data
shape. Cross-realm authority belongs to `realm:admin` users in whichever realm
currently carries `IsControlPlane`.

## Trust boundary

The installation form is not anonymously claimable. An operator with shell
access first issues a short-lived, single-use installation token through the
recovery CLI. Only its SHA-256 hash is stored in the Global Store.

Both the browser wizard and CI call the same HTTP API with that token. The API
never issues installation tokens itself.

## Interactive installation

Start the container, then issue an installation link from inside it:

```bash
docker exec modgud \
  dotnet Modgud.Api.dll recover install-link \
    --base-url https://auth.example.com
```

The command prints a URL like:

```text
https://auth.example.com/install?token=...
```

Open the URL and enter:

- realm slug and display name;
- primary domain (normally the host used in `--base-url`);
- first administrator username, email and password.

The API provisions the realm inactive, creates the administrator, activates
the realm and marks installation complete. Normal API and browser routes return
`503 not_initialized` or redirect to `/install` until that sequence succeeds.

Issuing another link revokes any previous unconsumed link. The default lifetime
is 30 minutes; `--minutes` accepts values from 1 to 1440.

::: warning Production boot guards
The published image runs as **Production** and refuses dev-shaped security
configuration. In particular, OpenIddict development mode must be disabled and
an enabled Prometheus endpoint needs a strong bearer token. See
[Deployment](../operate/deployment).
:::

## Automated installation (CI/test)

Use `--json` to make the recovery command's final output line
machine-readable:

```bash
install_json="$(
  docker exec modgud \
    dotnet Modgud.Api.dll recover install-link \
      --base-url https://auth.test.localhost \
      --minutes 10 \
      --json |
  tail -n 1
)"

token="$(printf '%s' "$install_json" | jq -r .token)"
```

Wait until `GET /health/live` succeeds, then call the completion API:

```bash
curl --fail-with-body \
  --request POST \
  --header 'Content-Type: application/json' \
  --data @- \
  https://auth.test.localhost/api/install/complete <<JSON
{
  "token": "$token",
  "realm": {
    "slug": "test",
    "displayName": "Test",
    "description": "Ephemeral CI realm",
    "domains": ["auth.test.localhost"],
    "primaryDomain": "auth.test.localhost"
  },
  "admin": {
    "userName": "ci-admin",
    "email": "ci-admin@test.localhost",
    "firstName": "CI",
    "lastName": "Admin",
    "password": "$MODGUD_CI_ADMIN_PASSWORD"
  }
}
JSON
```

Useful endpoints:

| Endpoint | Purpose |
| --- | --- |
| `GET /api/install/status` | Returns whether installation is complete |
| `POST /api/install/validate` | Validates a token without consuming it |
| `POST /api/install/complete` | Performs the complete first installation |

`complete` is idempotently closed after success: once a realm exists or the
global completion marker is present, another first installation is rejected.
The token is a bearer secret; do not print or persist it in CI artifacts.

For an HTTP-only test host, pass its `http://...` origin as `--base-url`.
Production and local Caddy installations should use their external HTTPS URL.

## Additional realms

After installation, a `realm:admin` in the Control-Plane realm creates further
realms through `POST /api/admin/realms`. Those realms are normal data-plane
realms and do not receive the Control-Plane flag.

```http
POST /api/admin/realms HTTP/1.1
Host: auth.example.com
Content-Type: application/json
Cookie: Modgud.Auth=...

{
  "Slug": "acme",
  "DisplayName": "Acme Corp",
  "Domains": ["auth.acme.com"],
  "InitialAdmin": {
    "UserName": "max",
    "Email": "max@acme.com",
    "Firstname": "Max",
    "Lastname": "Mustermann"
  }
}
```

The existing Control-Plane admin authorizes this operation. The new realm's
first admin receives the regular bootstrap invite. This is separate from the
deployment-wide first-installation token.

## Recovery after installation

Tenant-scoped recovery commands infer the realm only when exactly one active
realm exists. With multiple realms, pass `--realm <slug>` explicitly. For
example:

```bash
docker exec modgud \
  dotnet Modgud.Api.dll recover bootstrap-admin \
    --realm acme \
    --email recovery-admin@example.com \
    --username recovery-admin \
    --password 'StrongPass1!'
```

`bootstrap-admin` adds the user to the realm's existing Administrators group
and therefore restores a `realm:admin` path. See
[Recovery CLI](../operate/recovery-cli).

## Recommended next steps

1. Enable TOTP or a passkey on the first administrator.
2. Configure SMTP and test outbound mail.
3. Register the first OAuth/OIDC application.
4. Configure external SSO if required.
5. Plan and test Control-Plane transfer before relying on it operationally.

The guard that prevents removal of the final realm or final effective
`realm:admin` path is a separate hardening concern. The recovery CLI remains the
break-glass path if an administrator is locked out.
