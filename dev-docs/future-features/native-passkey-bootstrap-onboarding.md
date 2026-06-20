# Native passkey enrollment — bootstrap onboarding (ADR-0009 Gate #4)

Minimum UX specification for the first-run "sign in once, then add a passkey for
this app" flow that a native consumer app implements against the per-client
WebAuthn RP-ID (ADR-0009). This is the IdP-side contract + the recommended app-side
sequence; the actual UI lives in the consuming app, not in modgud.

## Why a bootstrap is needed

A passkey is bound to one **RP ID**. With per-client RP-IDs (ADR-0009), app A's
passkeys live under app A's branded apex (e.g. `app.amzettel.at`) and app B's under
B's — they are deliberately separate. So the **first** time a user opens a new app
they have **no passkey for that app's RP ID** yet. They must authenticate by some
other factor once, and then enroll a passkey for *this* app. After that, passkey
sign-in is the steady state for that app.

This is inherent to WebAuthn (one credential per RP per user), not a modgud quirk.
Platform passkeys live in the iCloud/Google keychain, so a reinstall does **not**
lose the credential — the bootstrap runs once per (user, app), not once per install.

## The endpoints

Native login (already shipped, ADR-0010 + ADR-0009):
- `POST /connect/passkey/begin` — anonymous; optional `client_id` form field selects
  the app's RP ID (absent ⇒ realm-scoped). Returns `{ ceremonyId, options }`.
- `POST /connect/token` with `grant_type=urn:cocoar:passkey`, `client_id`,
  `ceremony_id`, `assertion` — mints tokens.

Native enrollment (ADR-0009, Bearer-authenticated):
- `POST /connect/passkey/enroll/begin` — **requires a valid access token** (the
  factor-1 login below). The RP ID is resolved from the token's client. Returns
  `{ ceremonyId, options }` (attestation `CredentialCreateOptions`).
- `POST /connect/passkey/enroll` — Bearer; body `{ ceremonyId, attestation }`. Stores
  the credential under the client's RP ID.

Both enroll endpoints are gated behind the per-realm `NativeGrants` flag.

## Recommended first-run sequence (app side)

1. App attempts native passkey login (`/connect/passkey/begin` with its `client_id`
   → assertion → `urn:cocoar:passkey` grant).
2. If the user has no passkey for this app's RP ID, the platform sheet finds no
   credential / the grant returns `invalid_grant`. The app falls back to a **factor-1**
   login it already supports — any of:
   - `urn:cocoar:otp` (email OTP), `urn:cocoar:magic` (magic link), or
   - the interactive `authorization_code` flow (password / external IdP / SIWA).
3. With the resulting access token, the app immediately offers **"Set up Face ID /
   passkey for {app}"** — explicit and skippable, but encouraged. On accept it calls
   `enroll/begin` → platform create-credential → `enroll`.
4. Subsequent launches use native passkey login as the primary path.

## UX requirements (minimum bar)

- The "add a passkey for this app" prompt MUST be explicit and skippable (never a
  silent/forced enrollment).
- Copy should name the app ("…for {app}"), because the credential is app-scoped — a
  user may legitimately have separate passkeys per app under one identity.
- Communicate that the passkey persists across reinstalls (platform keychain), so the
  bootstrap is a one-time step per app.
- If `NativeGrants` is disabled for the realm, the enroll endpoints return
  `400 NativeGrants.Disabled` — the app should treat passwordless as unavailable and
  fall back to its interactive login.

## Notes

- RP ID is an admin-set, high-trust per-client setting (no public-suffix check). The
  app's own apex must serve its WebAuthn association file (AASA / assetlinks) — modgud
  does not route or serve the branded apex, it only validates it inside the assertion.
- Changing a client's RP ID after passkeys are enrolled invalidates them (same
  property `PrimaryDomain` has) — the admin UI warns about this.
