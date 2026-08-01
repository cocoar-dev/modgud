# Getting Started

Modgud is an OpenID-Connect-shaped identity provider that puts a multi-app permission model at its core. This section gets you from "nothing running" to "first SaaS app integrated" in a small number of pages — using the published Docker image, no source checkout required.

## Three angles to start from

Pick the one that matches what you're trying to do right now:

- **Run it locally** — [Quickstart (Docker)](./quickstart). Copy a compose file, `docker compose up`, issue a short-lived installation link, and create the first realm and administrator in the browser.
- **Integrate a SaaS app you already have** — go straight to the [SaaS Integration Walkthrough](../integrate/saas-walkthrough). It links into the relevant admin docs as you go.
- **Embed Modgud into your own deployment** — [Requirements](./requirements) and [Features](./features) explain what you're getting and what infrastructure you'll need.

## What Modgud is — in one paragraph

A self-hostable IdP. OAuth 2.0 + OpenID Connect server, runs on .NET 10, persists in PostgreSQL via Marten (event-sourced where it matters). Each customer / environment lives in an isolated realm with its own database. Apps within a realm declare their own permission catalogs and OAuth bindings. When a token targets a registered OAuth API and includes the `roles` and/or `permissions` scope, it can carry a Keycloak-shaped `resource_access` block keyed by that API's exact Audience, with bypass-pre-expansion and per-RS subset narrowing. Resource servers do straight exact-match against projected claims.

## What it isn't

- Not a hosted service. You run it.
- Not a user database for arbitrary domain data. Profiles only — your apps own their own tables.
- Not a BFF. It issues tokens; downstream apps consume them.
- Not a SAML identity provider for downstream apps — Modgud only ever issues OAuth 2.0 / OIDC tokens. It can *consume* SAML 2.0 as a service provider for federated login (see [SAML Federation](../admin/saml-federation)).

## Sections

- [**Quickstart (Docker)**](./quickstart) — copy the compose file, `docker compose up`, complete first installation, sign in — in 10 minutes
- [**Requirements**](./requirements) — runtime and infra checklist
- [**Features**](./features) — point-by-point list of what the box delivers
- [**First-time setup**](./first-time-setup) — the three bootstrap paths and when to use which
