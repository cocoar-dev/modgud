---
title: SAML link-flow degrades under SameSite=Lax — server-side state fix
---

# SAML link-flow degrades under SameSite=Lax — server-side state fix

> **Status:** Identified by the code-review pass on `feat/saml-federation` on 2026-05-27. Documented here; not fixed in the SAML wave. Block-2-of-the-fix-sweep deferred this item because the proper fix needs cross-browser SameSite=None+Secure verification on the test server, which isn't reachable today.
> **Severity:** UX silently degrades to JIT or email-auto-link; security-relevant only when `TrustForEmailLink=true` is also set (UI warns that's "DANGEROUS").

## The bug

`SamlLoginFlow.HandleAcsAsync` reads the existing application cookie to detect link-flow:

```csharp
var existingAuth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
Guid? authenticatedUserId = null;
if (existingAuth.Succeeded && existingAuth.Principal is not null) { ... }
```

The Modgud.Auth cookie is set with `SameSiteMode.Lax` (`Program.cs:347`). The SAML ACS POST is **cross-site** (from the IdP's host, e.g. `login.microsoftonline.com`), and Lax explicitly blocks cross-site POSTs from carrying the cookie. `authenticatedUserId` therefore stays `null` even when the user IS logged in, and the processor falls through to:

- `AutoCreateUsers=true` → a fresh JIT user is created (no link)
- `TrustForEmailLink=true` + email matches a different existing user → SAML identity gets bound to *that* user (a different account than the one initiating the link)
- otherwise → "no link, no auto-create" failure, link-flow degrades to a no-op with no clear admin signal

## Why not fix it in the SAML wave

The fix needs server-side state that survives cross-site POST. Options:

1. **Signed RelayState payload** carrying `{userId, returnUrl, expiresAt}`, data-protected via `IDataProtectionProvider`. Smallest infrastructure surface; readable from anywhere; needs RelayState to be permitted ≥256 bytes by the IdP (most IdPs ignore the SAML-spec 80-byte recommendation).
2. **Short-lived `SameSite=None`+`Secure` marker cookie** scoped to `/saml/*`, set in StartLoginAsync, read in HandleAcsAsync, single-use. Cleanest from a code-flow perspective; requires verifying that real browsers (Chrome 80+, Safari 13+, Firefox 96+) actually do carry SameSite=None cookies on cross-site POSTs to the same eTLD+1 when the request was triggered by a different eTLD+1.
3. **Server-side state store** keyed by `AuthnRequest.Id` → `userId`, with TTL. Adds a storage dependency; correct but heavier.

Verification of (1) and (2) needs:
- A real HTTPS test server (SameSite=None requires Secure; localhost dev with HTTP is a separate code path).
- A real IdP that does the cross-site POST (simplesamlphp on dev is on the same `localhost` so it's NOT cross-site — the dev smoke test cannot reproduce this bug).

Until the test server is up and the EntraID-Smoke runs, the fix can't be properly validated end-to-end. Shipping a "fix" that breaks in real-browser cross-site cookie handling would be worse than the current silent-degrade behavior.

## What is in place today

- The single-modal Add flow defaults `TrustForEmailLink=false` and the UI label calls it "DANGEROUS" with the GEFÄHRLICH warning, so the security-relevant degradation path requires admin opt-in into a flagged setting.
- The current behavior, while not the desired link-flow, doesn't *hijack* an account silently — at worst it creates a JIT user with the SAML identity. An admin reviewing the user list will see the duplicate.

## How to pick this up

1. Stand up the test server (the SAML wave's open `What's next` item).
2. Run EntraID smoke covering the link-flow path explicitly: log in with password, navigate to profile, click "Connect EntraID account", complete the IdP login, verify the SAML identity binds to the already-logged-in user.
3. Implement option (2) — signed cookie marker — since it has the smallest blast radius. Verify in three browsers.
4. Add an integration test that asserts the marker cookie path works when the app cookie is suppressed.
