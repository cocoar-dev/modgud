# SAML AMR → `amr` wiring

**Status:** Deferred (federation v1, decision I15). Parsed-but-not-consumed today; no functional gap for v1.

**Why:** SAML providers express the strength/method of an authentication via `AuthnContextClassRef` URIs in the assertion (e.g. `urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport`, or vendor MFA refs). The OIDC equivalent is the `amr` claim. Modgud already preserves OIDC `amr` from the external ticket onto the session principal (`ExternalLoginProcessor.Success` copies `amr` → `modgud.external.amr`, consumed by the `TwoFactorFederated` detection). The SAML side has the configuration surface for the same mapping — `SamlFlavorData.AmrMapping` (a `Dictionary<AuthnContextClassRef-URI, amr-value[]>`), seeded by the EntraID and ADFS flavor presets — but **no code reads it yet**. The SAML login flow does not currently translate `AuthnContextClassRef` through `AmrMapping` and stamp the resulting `amr` onto the principal.

This was deferred during the federation v1 build because it is orthogonal to the A–G membership/authorization model that wave delivered, and there is no v1 consumer that depends on SAML-derived `amr` (federated-MFA detection works for OIDC; SAML logins simply carry no `amr`).

## Current state

- `SamlFlavorData.AmrMapping` — defined, validated, JSON round-tripped, and seeded by `EntraIdSamlFlavor` / `AdfsSamlFlavor`. Covered by `SamlFlavorDataTests` / `SamlFlavorTests`.
- **No reader.** The SAML assertion's `AuthnContextClassRef` is not looked up against this map; nothing appends `modgud.external.amr` on a SAML login.
- The field carries an XML-doc note pointing here (`SamlFlavorData.AmrMapping`).

## What the wiring would do

1. In the SAML login flow (`SamlLoginFlow` / `BuildExternalPrincipal`), read the assertion's `AuthnContextClassRef` value(s).
2. Look each up in the provider's `AmrMapping`; collect the mapped `amr` values.
3. Add them as `amr` claims on the external `ClaimsPrincipal` so they flow through the single shared seam (`ExternalLoginProcessor.Success`) exactly like the OIDC `amr` values — no new consumer code, the existing `TwoFactorFederated` path picks them up.

## Why it is safe to defer

- Fail-closed: absent `amr` means "no asserted MFA method", which is the conservative default for `TwoFactorFederated` detection.
- Additive: the config + presets already exist, so enabling it later is a read-side change only — no migration, no schema change, no breaking the persisted flavor data.

## When to pick it up

When a customer needs step-up / MFA-state awareness from a SAML IdP (EntraID, ADFS), or when the OIDC and SAML federated-MFA behaviors must be at parity. Pair it with any broader `amr`/ACR work on the OIDC side so both protocols share one normalization table.
