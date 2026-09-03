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

Both the browser installation form and CI call the same HTTP API with that token. The API
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

**The `--base-url` you pass is the deployment's public origin.** It decides two
things, both of them from that single declaration:

1. Installation sends you back to it verbatim — scheme, host and port. You are
   standing at that origin, so that is where the sign-in page has to be.
2. It is recorded as the new realm's **public origin**, and from then on every
   outbound link is built against it: magic links, password resets, email
   verification, invites, and the login-provider callback URLs you paste into an
   upstream IdP. Nothing is inferred from the environment, so a deployment on a
   non-default port works without special cases.

Change it later with `recover realm-set-public-url --slug <slug> --url <origin>`.
It is separate from the realm's **primary domain**, which stays a bare host name
because it is also the passkey relying-party ID — see
[Deployment](../operate/deployment#where-public-urls-come-from).

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
  "Domains": ["auth.acme.com"]
}
```

The existing Control-Plane admin authorizes this operation. The result is a
complete, active realm and tenant database; an administrator is not required
for realm creation.

When ownership should be handed over, use the realm's context-menu action
**Invite realm admin** or call
`POST /api/admin/realms/{slug}/admin-invites`. The link is single-use, expires
after 24 hours, and issuing a new invitation revokes any previous open one.
For API compatibility, realm creation also accepts an optional `InitialAdmin`
object and issues the same invitation atomically. Realm-admin invitations are
separate from the deployment-wide first-installation token.

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
