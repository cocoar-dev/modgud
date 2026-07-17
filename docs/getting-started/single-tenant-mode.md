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

For a quick local eval, use the [Quickstart](quickstart) compose as-is — the system realm is all you get out of the box. For a real single-tenant **production** deployment, run the published image with every env var the Production boot guards require:

```bash
docker run -d \
  --name modgud \
  -p 80:8081 \
  -v cocoar-keys:/app/data/keys \
  -e DbSettings__ConnectionString="Host=db.internal;Database=modgud;Username=modgud;Password=…;Keepalive=30" \
  -e ProxyAllowedNetworks="10.0.0.0/8" \
  -e Observability__Prometheus__BearerToken="$(openssl rand -hex 32)" \
  ghcr.io/cocoar-dev/modgud:latest
```

Only two of these are boot-enforced guards that fail closed: `OpenIddict__DevelopmentMode` must be `false` (the default), and Prometheus must either carry a strong `BearerToken` or be turned off entirely with `-e Observability__Prometheus__Enabled=false`. The other two settings above are still important, just not startup checks: the issuer is derived per request from the realm's Host header rather than checked at boot, so it's on you to make sure `https://auth.example.com` is what actually resolves; and omitting `ProxyAllowedNetworks` doesn't fail startup, it silently falls back to a sentinel network that never matches, so forwarded headers from your reverse proxy get rejected instead of honoured. The `-v cocoar-keys:/app/data/keys` volume persists the signing keys across restarts. Configuration is by env var only — `configuration.json` is not shipped in the published image, and Cocoar.Configuration v6 binds `Section__Property` case-insensitively. See [Deployment](../operate/deployment) for the full guard list.

```bash
# Add the public hostname to the system realm, make it the primary
# (so outbound email links resolve), then restart to pick it up
docker exec modgud dotnet Modgud.Api.dll \
    recover realm-add-domain --slug system --domain auth.example.com
docker exec modgud dotnet Modgud.Api.dll \
    recover realm-set-primary-domain --slug system --domain auth.example.com
docker restart modgud

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
- **Don't deactivate or delete the realm that currently holds the Control-Plane flag.** While a realm is the Control Plane, deactivating or deleting it is blocked by service-level guards (the deployment would lose its cross-realm admin surface). In a single-tenant deployment that's the system realm. The Control-Plane flag is transferable to another active realm first (`recover control-plane transfer <slug>`) if you genuinely need to retire the original — see [Concepts: Control Plane](../concepts/control-plane).

## Growing into multi-tenant later

If a single-tenant deployment later needs to host a second tenant
(merger, white-label rollout, …), nothing has to change in the
existing system realm. You just create a new realm via
`POST /api/admin/realms` (or the Admin UI), give it its own
hostname, and the existing users / clients / scopes in the system
realm stay where they are.

The new realm gets its own PostgreSQL database (`<master-db>_<slug>`),
its own hostname-routing entry, its own everything. Cross-realm
isolation is enforced at the database level for queries and
connections — a query that's missing its tenant scope still only
runs against the single realm's own database, so it can't reach
another tenant's data. That's a guarantee about query-level bugs
specifically, not a blanket promise that no bug anywhere in the
application layer could ever cross a realm boundary — see
[Concepts: Realms](../concepts/realms#cross-realm-isolation) for how
the other surfaces (permissions, tokens, cookies, SignalR) are
isolated.
