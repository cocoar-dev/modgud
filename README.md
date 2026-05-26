# Modgud

**Self-hosted multi-tenant Identity Provider for ASP.NET Core.**
OAuth 2.0 / OpenID Connect server with database-per-realm isolation,
multi-app permission catalogs, Keycloak-style `resource_access`
emission, full 2FA spectrum, GDPR self-service.

[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![pre-1.0](https://img.shields.io/badge/status-pre--1.0-orange.svg)](./docs/roadmap.md)

## What you get

- **Multi-tenant by design** — every realm gets its own PostgreSQL
  database via Marten's `MasterTableTenancy`. Domain-based routing
  maps `Host` headers to tenants. No `tenant_id` columns, no
  cross-realm leaks possible.
- **Multi-app permission model** — Apps are first-class. Permissions
  are 2-segment (`<resource>:<action>`) inside an app's catalog. Two
  bypass tiers, no more. Roles bind to one App, groups carry a
  `BoundTo` activation list.
- **Keycloak-style `resource_access` on UserInfo** — per-audience
  blocks with bypass pre-expansion and per-RS subset narrowing. A
  drop-in `IClaimsTransformation` library flattens the right block
  into `ClaimTypes.Role` so `[Authorize(Roles = "...")]` works
  without per-endpoint plumbing.
- **Full 2FA spectrum + WebAuthn** — TOTP, Email-OTP, FIDO2/Passkey,
  Magic Link, recovery codes. 2FA enforcement middleware with grace
  period and per-user override.
- **OIDC federation** — Microsoft Entra ID, Google, any OIDC IdP.
  JIT user provisioning + JavaScript claim-mapping (`UserUpdateScript`).
- **Dynamic Client Registration (RFC 7591)** with triple opt-in
  (realm master / per-API / per-scope), audience-target containment,
  full audit-event trail.
- **GDPR-ready** — Article-20 self-service export, three-step
  account deletion with confirmation token, Marten data-masking +
  `ArchiveStream` for irreversible PII erase with audit-chain
  integrity preserved.
- **Observability built-in** — OpenTelemetry metrics + traces,
  Prometheus scrape endpoint, custom IdP meter, in-app live
  activity feed.
- **Recovery CLI** — break-glass admin path (bootstrap-admin,
  reset-2fa, magic-link, rebuild-projections) when the UI can't
  help you.

## Quick links

| | |
|---|---|
| [📘 Get Started](./docs/getting-started/) | What this is, requirements, first-time setup |
| [⚡ Quickstart (Docker)](./docs/getting-started/quickstart.md) | From `docker compose up` to first login in 10 minutes |
| [🧠 Concepts](./docs/concepts/) | Realms, apps, permissions, OAuth, tokens — the mental model |
| [🛠️ Operate](./docs/operate/) | Deployment, observability, recovery CLI, feature flags |
| [👤 Administer](./docs/admin/) | Users, groups, roles, OAuth clients, login providers |
| [🔌 Integrate](./docs/integrate/) | Plug your ASP.NET Core / SaaS app into Modgud |
| [📖 Reference](./docs/reference/) | OAuth / Auth / Admin / Realm endpoint reference |
| [🗺️ Roadmap](./docs/roadmap.md) | What ships today, what's coming, what's intentionally out of scope |

## Status

**Pre-1.0, actively developed.** The [Roadmap](./docs/roadmap.md)
is the canonical view of what's shipped and what's coming next — it
gets revised when something lands.

Single-maintainer, alongside a day job. Response times are in days,
not hours; see [CONTRIBUTING.md](./CONTRIBUTING.md) for the working
agreement.

## Build it yourself

```bash
# Prereqs: .NET 10 SDK, Node 22 + pnpm (via Corepack), Docker

# Backend (port 9099)
cd src/dotnet
docker exec cocoar-postgres psql -U postgres -c "CREATE DATABASE modgud;"
cd Modgud.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile

# Frontend (port 4300, separate terminal)
cd src/frontend-vue
pnpm install
pnpm dev
```

First-time admin bootstrap via the recovery CLI — see
[First-time setup](./docs/getting-started/first-time-setup.md) for
the walkthrough.

## Contributing

PRs welcome for typos and small fixes — for anything bigger, please
open a [Discussion](https://github.com/cocoar-dev/modgud/discussions)
first. The [Contributing guide](./CONTRIBUTING.md) has the full
ground rules including the solo-maintainer realities.

Security vulnerabilities **do not** go through the public issue
tracker — see [SECURITY.md](./SECURITY.md) for the private channel.

## About the name

**Modgud** takes its name from **Móðguðr**, the watcher of
*Gjallarbrú* in Norse mythology — a bridge between worlds, where
she challenged every traveler with the same question an IdP asks:
*"Who are you, and what brings you here?"* A fitting namesake.

## License

Licensed under the [Apache License, Version 2.0](./LICENSE).

Copyright © 2025–2026 [COCOAR e.U.](https://cocoar.dev) — built
solo by [@windischb](https://github.com/windischb) in Vienna,
Austria.
