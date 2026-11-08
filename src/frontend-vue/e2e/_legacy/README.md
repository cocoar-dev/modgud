# Legacy E2E specs (archived)

These specs predate the post-cutover Phase-1+ rewrites and were the
original E2E suite from the pre-rebuild codebase. They aren't run by
Playwright today — `playwright.config.ts` excludes this folder with
`testIgnore: ['**/_legacy/**']`.

They're kept here as **patterns for the porting effort**: when a new
spec under `e2e/` covers the same area (login, MFA, magic-link,
admin grids, OIDC, passkey, auth-enforcement), grab the relevant
shape from the file in this folder and adapt it to the new test rig
(Release image in the Staging environment + Mailpit for outbound email +
`<resource>:<action>` permissions whose App context comes from the role).

## Why they don't run as-is

- Container names + image tags are still from the pre-rebuild era.
- They depend on `/api/dev/emails` and `/api/dev/reset-mfa` (gone —
  inspection now lives in Mailpit).
- They assume the permission model of their time; today a role is tied to
  an App and grants `<resource>:<action>` strings (see
  `docs/concepts/permissions.md`).
- They wire the runtime in `ASPNETCORE_ENVIRONMENT=Development` —
  the new rig runs a Release image in Staging (production build and
  behaviour, without the deploy-only start guards).

## Status of each file

| File | What it pinned | Notes for porting |
|---|---|---|
| `auth.spec.ts` | password login + sign-out | Smallest — port first as a sanity check after the new helpers are in place. |
| `auth-enforcement.spec.ts` | grace period + AuthenticationMinimumLevel | Needs Settings UI (F18) to land first or driven via API. |
| `magic-link.spec.ts` | request + verify magic link | Replace `/api/dev/emails` polling with Mailpit polling. |
| `email-otp.spec.ts` | enable Email OTP + login with code | Same as above. |
| `passkey.spec.ts` | virtual authenticator + register + login | Helpers (`addVirtualAuthenticator`) carry over verbatim. |
| `oidc.spec.ts` | external IdP JIT login | Needs the TestIdP container; out of Phase-A scope. |
| `admin.spec.ts` | admin sidebar + user CRUD | Smallest UI port; covers §6 of the manual checklist. |
| `helpers.ts` | login / addVirtualAuthenticator / TestIdP plumbing | The auth-related parts come back near-verbatim. |
