# Getting Started

Modgud is an OpenID-Connect-shaped identity provider that puts a multi-app permission model at its core. This section gets you from "downloaded the repo" to "first SaaS app integrated" in a small number of pages.

## Three angles to start from

Pick the one that matches what you're trying to do right now:

- **Run it locally** — [Quickstart (Docker)](./quickstart). Spins up Postgres + Modgud, creates the first admin via the recovery CLI, leaves you with a logged-in admin SPA.
- **Integrate a SaaS app you already have** — go straight to the [SaaS Integration Walkthrough](../integrate/saas-walkthrough). It links into the relevant admin docs as you go.
- **Embed Modgud into your own deployment** — [Requirements](./requirements) and [Features](./features) explain what you're getting and what infrastructure you'll need.

## What Modgud is — in one paragraph

A self-hostable IdP. OAuth 2.0 + OpenID Connect server, runs on .NET 10, persists in PostgreSQL via Marten (event-sourced where it matters). Each customer / environment lives in an isolated realm with its own database. Apps within a realm declare their own permission catalogs and OAuth bindings. Tokens carry Keycloak-style `resource_access` keyed per Audience, with bypass-pre-expansion and per-RS subset narrowing — resource servers do straight exact-match against a flat permission list, no custom claim format and no separate IdP roundtrip.

## What it isn't

- Not a hosted service. You run it.
- Not a user database for arbitrary domain data. Profiles only — your apps own their own tables.
- Not a BFF. It issues tokens; downstream apps consume them.
- Not a SAML provider. OIDC and OAuth 2.0 only.

## Sections

- [**Quickstart (Docker)**](./quickstart) — `docker compose up`, bootstrap the first admin, sign in — in 10 minutes
- [**Requirements**](./requirements) — runtime and infra checklist
- [**Features**](./features) — point-by-point list of what the box delivers
- [**First-time setup**](./first-time-setup) — the three bootstrap paths and when to use which
