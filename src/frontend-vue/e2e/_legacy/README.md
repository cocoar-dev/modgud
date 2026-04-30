# Legacy E2E specs (archived)

These specs predate the post-cutover Phase-1+ rewrites and were the
original TimeToDo-flavoured E2E suite. They aren't run by Playwright
today — `playwright.config.ts` only picks up `*.spec.ts` directly
under `e2e/`, not under `_legacy/`.

They're kept here as **patterns for the porting effort**: when a new
spec under `e2e/` covers the same area (login, MFA, magic-link,
admin grids, OIDC, passkey, auth-enforcement), grab the relevant
shape from the file in this folder and adapt it to the new test rig
(production-mode container + Mailpit for outbound email + the
3-segment permission model).

## Why they don't run as-is

- Container names + image tags are still `timetodo-e2e-*`.
- They depend on `/api/dev/emails` and `/api/dev/reset-mfa` (gone —
  inspection now lives in Mailpit).
- They use the legacy 2-segment permission strings (`user:read`)
  rather than the post-Phase-1 `<app>:<resource>:<action>` form.
- They wire the runtime in `ASPNETCORE_ENVIRONMENT=Development` —
  the new rig runs Production mode against the same image we ship,
  so timing-sensitive paths get tested for real.

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
