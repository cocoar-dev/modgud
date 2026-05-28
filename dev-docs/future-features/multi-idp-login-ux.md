---
title: Multi-IdP login UX — picker vs email-routing
---

# Multi-IdP login UX — picker vs email-routing

> **Status:** Designspace captured 2026-05-27, not started. Surfaced during the [saml-federation](./saml-federation) Q&A — explicitly not a SAML-specific problem, so split out into its own wave that covers all provider protocols (OIDC + SAML and anything we add later).
> **Why:** Today's login UI renders every configured login provider as an equal button ("Login with Google", "Login with EntraID", "Login with GitHub", "Login with Apple", …). The pattern doesn't scale beyond ~3 providers, becomes visually noisy, and gives the wrong semantic ("which one of these am I?") to users who already know their identity is in a specific IdP.

## Scope

Provider-protocol-agnostic. Applies equally to OIDC providers (Google, Apple, Microsoft, GitHub, Facebook, …), SAML providers (EntraID Enterprise, Okta, ADFS, …), and anything we add later (LDAP, Passkeys-only-providers, etc.).

The internal LoginProvider (password / passkey / magic-link) is a separate UI surface and not in scope here — this is about routing to *external* providers when many are configured.

## Status quo

`src/frontend-vue/src/views/login/` shows configured providers from the per-realm provider list as buttons. No grouping, no filtering, no search. Order is insertion-order. No domain-routing, no preference-memory.

Works fine for ≤3 providers per realm. Falls apart visually + semantically at ~5+.

## Designspace — three patterns

### Pattern A — Email-Domain-Routing

The enterprise-SSO default (Auth0, Okta, EntraID-B2B):

1. Login page shows **only** an email field + "weiter" button. No provider buttons visible.
2. User types `alice@customerA.com`, clicks weiter.
3. Modgud looks up the domain → matches to the configured provider → redirects to that provider's authentication.
4. Fallback: if no provider matches the domain, fall through to the internal username/password form.

**Pros:**
- One field, clean, semantically right ("I am Alice", not "I choose how to log in")
- Industry-standard pattern users recognise
- Naturally hides irrelevant providers from each user
- Provider count doesn't visually scale — UI stays the same with 3 or 30 providers

**Cons:**
- Customer-Admin must maintain a domain → provider mapping per provider
- Users who type the wrong domain (typo, personal email by accident) get confused
- Users without a domain claim on the IdP side (anonymous SAML / opaque NameID providers) don't fit the model
- Doesn't work for providers like Google/Apple where the user could legitimately be on any domain

### Pattern B — Provider-Picker with Search

The Keycloak / Okta-Workforce pattern:

1. Login page shows **one prominent** option (e.g. email/password) + a "Other login methods" dropdown.
2. Dropdown lists providers; when N > 5, a search field appears at top of the dropdown.
3. User clicks the provider they recognise.

**Pros:**
- Works with any provider type, no per-provider config beyond the existing display name + icon
- No domain-mapping pflege
- Discoverable for users who don't know what to type

**Cons:**
- Still requires the user to know "which one am I?"
- Less elegant when the realm is multi-customer (User from Customer A sees Customer B and C's providers — feels like a leak)
- Doesn't auto-route — extra click

### Pattern C — Hybrid (email-first, picker fallback)

The Auth0-modern default:

1. Login page shows email field.
2. If user types an email whose domain matches a configured provider → redirect to that provider.
3. If no domain matches → show provider-picker with the configured providers.
4. User can also click "browse all login methods" without typing anything to skip the email step.

**Pros:**
- Best of both — domain-routing where possible, picker where not
- Multi-customer realms: per-Customer SAML providers can have domain-claims; consumer-OIDC providers (Google, Apple) sit in the picker fallback
- Gives users a way out if domain-routing guesses wrong

**Cons:**
- Most implementation surface
- Two UI states to design + test
- Per-provider domain-config is optional; admin UI needs to make that clear

## Per-provider domain-config

If we adopt Pattern A or C, each LoginProvider needs an additional config field: **`DomainsForRouting: string[]`** (multi-value, e.g. `["customerA.com", "subsidiary-a.com"]`). Optional — providers without domain entries simply don't participate in domain-routing and only appear in the picker fallback (Pattern C) or are unreachable (Pattern A, which is why Pattern A alone has limits).

## Memory-of-last-choice (additive)

Independent of which pattern: remember the last provider this browser used (LocalStorage) and offer it as a one-click "Continue as you did last time" at the top. Doesn't break the underlying pattern, just adds friction-reduction for returning users.

## Recommendation

**Pattern C (Hybrid).** Email-first as primary; picker fallback when domain-routing has no match or the user opts out.

Phasing if cost is a concern:

- **Phase 1 (cheap):** Pattern B with search-when-N>5. Ships in 1-2 days. No domain-config field needed.
- **Phase 2 (full):** Add `DomainsForRouting` field + Pattern C email-first UI. Another 2-3 days.

The phasing is non-destructive — Phase 2 strictly extends Phase 1.

## Effort estimate

- **Phase 1 only:** ~2 days (frontend redesign of login page + picker component, no backend change)
- **Phase 2 additive:** ~3 days (backend: `DomainsForRouting` field on `LoginProvider.FlavorData`, admin UI for it, frontend email-routing + fallback wiring)
- **Memory-of-last-choice:** ~0.5 day, can land in either phase

**Total Pattern C complete: ~5-6 days.**

## Trigger

Default trigger: **after [saml-federation](./saml-federation) ships.** That wave will add ~3-5 SAML providers in realistic Customer scenarios, which pushes typical realm provider count past the threshold where the current button-stack starts to feel wrong.

Sales / customer-evaluation trigger that would advance the work: a prospect saying "your login screen looks cluttered, can you make it like Auth0's?" — concrete, customer-driven.

## What we explicitly are NOT doing

- **One sign-in URL per provider** (`/login?provider=entra-customer-a`). Some IdPs encourage this pattern as deep-linking from emails. Not designing for it now; the IdP-initiated SSO endpoint per provider already covers the deep-link case for SAML, OIDC has its own equivalent.
- **Provider-grouping into named sections** ("Work logins" vs "Personal logins"). Could be nice; not now. Pattern C's email-routing implicitly does this — work emails go to work providers via domain, personal emails fall to the picker.
- **Discoverable provider directory across realms** — irrelevant for our model.
