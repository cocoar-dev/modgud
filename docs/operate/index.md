---
title: Operate
description: Running Modgud in production — Docker deployment, persistence, multi-tenancy provisioning, observability, recovery procedures.
---

# Operate

How to run Modgud once you've got the basics from
[Getting Started](/getting-started/). These pages assume you're the
operator: someone who deploys the container, monitors it, and steps in
when something breaks.

## Deploying

- [Docker & Deployment](./deployment) — the official image, env-var
  reference, reverse-proxy expectations, certificate handling.
- [Persistence (Marten)](./database) — PostgreSQL setup,
  master-table-tenancy model, per-realm database naming.
- [Multi-tenancy / Realms](./realms) — the operator-facing side of
  realm provisioning, domain wiring, and migration paths.
- [Running two instances](./deployment#running-two-instances) — two
  containers against one PostgreSQL, sticky proxy with active health
  checks, and image updates that replace one node after the other
  with no downtime; which releases are safe to roll.

## Day 2

- [Observability](./observability) — OpenTelemetry traces + metrics,
  Prometheus scrape, the in-app live activity feed.
- [Recovery CLI](./recovery-cli) — first-time bootstrap and break-glass
  operations: `bootstrap-admin`, `realm-add-domain`,
  `realm-set-primary-domain`, `set-email`, `magic-link`, `reset-2fa`,
  `rotate-signing-key`, `control-plane transfer`, `rebuild-projections`.

## Architecture

- [Backend layout](./backend-architecture) — the slice structure
  (Authentication, Authorization, Domain, Infrastructure, Api) and
  how they wire together.
- [Feature Flags](./feature-flags) — the runtime toggles that gate
  in-progress surfaces (currently: Page Builder, …).
