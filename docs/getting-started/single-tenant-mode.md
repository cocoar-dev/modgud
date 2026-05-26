# Single-tenant mode

Modgud is multi-tenant by design — every realm gets its own
PostgreSQL database, hostname routing, OAuth-client + user store. But
the multi-tenant architecture is **opt-in**: for a "one app, one
company" deployment the system realm is enough on its own.

## When this fits

- You're running modgud as the IdP for **your own** apps and
  users, not hosting tenants for third parties.
- You don't need per-customer data isolation.
- The deployment has one public hostname (e.g. `auth.acme.com`) and
  every user signs in there.

If any of those don't fit, you're a SaaS or multi-customer scenario
and want one realm per tenant. See
[Multi-realm deployment](../operate/realms) for that pattern.

## What you get

The system realm is a **fully-featured realm** that behaves
identically to any tenant realm for everyday IdP work — plus the
control-plane functions on top.

| Feature | Available in system realm |
|---|---|
| Users, Groups, Roles, Permissions | ✅ |
| OAuth clients for your apps | ✅ |
| OAuth scopes, resource APIs | ✅ |
| Login providers (Internal, OIDC federation) | ✅ |
| Custom permissions, auto-membership scripts | ✅ |
| Magic-link, 2FA, Passkeys, email-OTP | ✅ |
| `/api/admin/realms` (cross-realm management) | ✅ control-plane only |
| `control-plane:*` permission namespace | ✅ control-plane only |

The two control-plane-only items don't get in the way for a
single-tenant deployment — they just sit there unused.

## Setup

Same recipe as the [Quickstart](quickstart) without any extra steps:

```bash
docker run -d \
  --name modgud \
  -p 80:8081 \
  -v cocoar-keys:/app/data/keys \
  -e DbSettings__ConnectionString="Host=...;Database=modgud;..." \
  -e OpenIddict__Issuer="https://auth.example.com" \
  -e ProxyAllowedNetworks="<reverse-proxy-CIDR>" \
  ghcr.io/cocoar/modgud:latest

# Add the public hostname to the system realm and restart
docker exec modgud dotnet Modgud.Api.dll \
    recover realm-add-domain --slug system --domain auth.example.com
docker compose restart auth

# Bootstrap the first admin into the system realm (default)
docker exec modgud dotnet Modgud.Api.dll \
    recover bootstrap-admin \
    --email admin@example.com --username admin --password 'StrongPass1!'
```

That's it. From the browser:

1. `https://auth.example.com/login` → sign in as `admin`
2. Set up 2FA (the grace-period dialog appears on first login)
3. Admin → Users → invite your team
4. Admin → OAuth clients → register the apps that will sign in
   against this IdP
5. Admin → Roles + Groups → wire up app-specific permissions

## What to avoid

- **Don't grant `control-plane:*` permissions to regular users.** The
  default seeding doesn't — `realm:admin` (in the seeded
  Administratoren group) is the only privileged role. Custom roles
  you create yourself shouldn't list `control-plane:realm:read` or
  `control-plane:realm:write` unless the user genuinely is a
  deployment-level admin.
- **Don't deactivate or delete the system realm.** Both are blocked
  by service-level guards (the deployment would lose its admin
  surface), but they're a configuration footgun if you're scripting
  realm CRUD.

## Growing into multi-tenant later

If a single-tenant deployment later needs to host a second tenant
(merger, white-label rollout, …), nothing has to change in the
existing system realm. You just create a new realm via
`POST /api/admin/realms` (or the Admin UI), give it its own
hostname, and the existing users / clients / scopes in the system
realm stay where they are.

The new realm gets its own PostgreSQL database (`<master-db>_<slug>`),
its own hostname-routing entry, its own everything. Cross-realm
isolation is enforced at the database level — there's no path for
tenant data to leak from one realm to another, even if a bug in
modgud opened a query without the tenant scope.
