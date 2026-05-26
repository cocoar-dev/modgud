---
title: Concepts
description: How Modgud thinks about identity, tenancy, authorization, and the OAuth/OIDC surface — the mental model behind the code.
---

# Concepts

The conceptual map of Modgud. If you're new, read these in roughly
the order below — each one builds on the ones above it.

## Foundations

- [Glossary](./glossary) — terminology used across the docs and UI.
  Skim it once; refer back when a word feels overloaded.
- [Apps & resource_access](./apps-and-resource-access) — why
  permissions are app-scoped and how `resource_access` shows up
  on tokens.

## Tenancy

- [Realms (Multi-Tenant)](./realms) — the database-per-realm model
  and how requests get routed to the right tenant.
- [Control Plane / Data Plane](./control-plane) — how cross-realm
  administration is structurally separated from tenant operations.

## Identity

- [Authentication](./authentication) — login flows, 2FA, federated
  OIDC, sessions.

## Authorization

- [Authorization (RBAC)](./groups-and-authorization) — the
  Principal → Group → Role → Permission chain.
- [Permissions & gating](./permissions) — the three-segment
  permission grammar and the bypass tiers.
- [Auto-Membership](./auto-membership) — JsEval-scripted group
  membership predicates.
- [ABAC and the IAM boundary](./abac) — why row-level access stays
  in the consuming app, not in the IdP.

## OAuth / OIDC

- [OAuth & OIDC](./oauth) — the supported flows, signing, and
  per-realm isolation.
- [Dynamic Client Registration](./dynamic-client-registration) —
  RFC 7591 for MCP agents and self-onboarding apps.
- [Sessions & Tokens](./tokens) — what's on a token, where session
  state lives, and how rotation works.
