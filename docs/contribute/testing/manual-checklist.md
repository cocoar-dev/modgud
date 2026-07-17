# Manual smoke checklist

End-to-end smoke pass for the live system. Walk it **outside-in**: bring the system up first, then exercise auth, then admin, then OAuth, then the cross-cutting flows. Use it before a release, or whenever you suspect drift between the docs and what's actually running.

Some of this is already covered by the Playwright suite under `src/frontend-vue/e2e/` — those items are marked **(automated)**. Walk the rest by hand; tick the boxes as you go.

[[toc]]

## 0. Bring-up

- [ ] Backend builds: `cd src/dotnet && dotnet build`
- [ ] Postgres container running
- [ ] Master DB exists (created on the fly, or up front — see [Developing locally](../developing-locally))
- [ ] Docker image boots cleanly on just the env vars from your compose file — no anonymous `/setup` wizard exists, so a fresh container has no admin yet
- [ ] Frontend loads without console errors

## 1. First-time setup

- [ ] `recover bootstrap-admin` creates the first admin (see [First-time setup](../../getting-started/first-time-setup))
- [ ] Signing in as that admin lands on the dashboard with the full sidebar (`realm:admin` bypasses every gate)

## 2. Login & sign-out

- [ ] Sign out via the header menu → back at `/login`
- [ ] Wrong password 5× locks the account; the correct password is rejected until the lockout window passes **(automated)**
- [ ] Magic-link round trip: request → email → click → signed in **(automated)**
- [ ] "Remember me" persists the cookie across a real browser restart
- [ ] Logout-everywhere from `/profile/sessions` signs out other sessions

## 3. Two-factor & passwordless

- [ ] Enable TOTP, sign out, sign in with a fresh code **(automated)**
- [ ] Wrong TOTP code is rejected **(automated)**
- [ ] One recovery code is consumed once; reusing it is rejected
- [ ] Enable email OTP and sign in with the mailed code
- [ ] Enroll a passkey and sign in with it
- [ ] Disable each 2FA method

See [Two-factor authentication](../../integrate/two-factor) for the supported methods and [Native & mobile apps](../../integrate/native-apps) for the cookieless OTP/magic-link/passkey grants used by native clients.

## 4. Profile self-service

- [ ] Edit a non-email field → goes to `AdminApprovalPending` **(automated)**
- [ ] Change email → verification link → admin approval → `/me` reflects both changes atomically **(automated)**
- [ ] GDPR export
- [ ] GDPR delete request → confirmation token → deletion → masking

## 5. Sessions

- [ ] `/profile/sessions` lists the current session with browser/OS/device/IP
- [ ] A second browser session shows up as a separate row
- [ ] Revoking another session signs that browser out
- [ ] Admin → Users → user → Sessions shows the same view with force-logout

## 6. Users (admin)

- [ ] Create a user with just an email (no password) → `Status: Pending`, appears live via SignalR **(automated)**
- [ ] Edit a user's profile fields via the admin UI
- [ ] Lock / unlock a user
- [ ] Soft-delete a user (recycle bin) and GDPR-tear it down
- [ ] Admin sends a magic link from the user detail view
- [ ] 2FA grace-period override is visible and editable per user

## 7. Roles

- [ ] Create a role scoped to an app with a resource:action permission set, e.g. `AppSlug=modgud`, `Resource=user`, `Actions=["read"]` **(automated)**
- [ ] Default roles exist after bootstrap: **System Admin** (`realm:admin`), **User Manager** (read+write users, read groups/roles/logs), **Viewer** (read-only)

## 8. Groups

- [ ] Group create response carries no ABAC/access-script field — membership is pure RBAC **(automated)**
- [ ] `BoundTo` accepts an app-slug list and the wildcard `["*"]` **(automated)**
- [ ] Group detail modal tabs (General / Members / Script / Roles / Effective) all render
- [ ] Write an auto-membership script, save, and see matches on the Effective tab
- [ ] A broken membership script surfaces `MembershipLastError`
- [ ] Adding a group as its own (indirect) descendant is rejected (cycle prevention)

## 9. Apps & settings cascade

- [ ] The seeded `modgud` system app is listed with `IsSystem: true` **(automated)**
- [ ] Create a non-system app; reserved slugs (`realm`, `modgud`, `*`) are rejected **(automated)**
- [ ] The App detail modal's Settings tab lets you override origin, branding, login posture, and native-grant policy for that app; an unset section falls back to the realm default instead of showing as overridden
- [ ] Realm-wide defaults for the same settings live under **Admin → Realm settings** (`/admin/realm-settings`)

See [Applications](../../admin/applications) for the full settings list.

## 10. Invite codes

- [ ] Mint one or more codes for an app (`POST /api/app/{appId}/invite-codes`)
- [ ] A minted code is listed and can be revoked (`DELETE /api/app/{appId}/invite-codes/{id}`)
- [ ] Self-registering with a valid code succeeds; an invalid or already-used code is rejected

See [Invite codes](../../admin/invite-codes) for the full flow.

## 11. OAuth Clients

- [ ] List endpoint returns a paginated shape even without `?page=` / `?pageSize=` **(automated)**
- [ ] Create a confidential web client; assign apps via the **AppIds** multi-select
- [ ] Removing an app from AppIds stops scopes pinned to that app from validating for the client
- [ ] Rotate the client secret (one-time reveal)
- [ ] PATCH semantics: omitting `AppIds` leaves it unchanged, `[]` detaches all

## 12. OAuth Scopes

- [ ] Standard scopes (`openid`, `email`, `profile`, `roles`, `offline_access`) are seeded with `AppId = null` (global) **(automated)**
- [ ] A custom scope scoped to an app is discoverable on `/.well-known/openid-configuration`
- [ ] A scope scoped to app A is rejected at the token endpoint for a client only linked to app B

## 13. OAuth APIs (resource servers)

- [ ] List endpoint returns a paginated shape even without pagination params **(automated)**
- [ ] Create a resource server, link it to an app
- [ ] Moving a resource server to a different app switches which `resource_access` block tokens for it carry

## 14. Login providers (external IdP)

- [ ] The built-in Internal login provider is listed and active by default
- [ ] Create an OIDC/Entra ID login provider; discovery / test connection succeeds
- [ ] External login JIT-provisions a user and signs them in
- [ ] Disabling a login provider removes its login button
- [ ] Account-linking from `/profile` adds a second login provider to an existing user

## 15. Realms & provisioning

- [ ] The system realm exists with `IsControlPlane: true` **(automated)**
- [ ] Creating a realm provisions a fresh tenant database plus a bootstrap-admin invite **(automated)**
- [ ] Reserved/invalid slugs are rejected **(automated)**
- [ ] Exporting a realm as a manifest and re-applying it is a no-op
- [ ] Applying a manifest with `?prune=true` removes drifted entities but never locks out the last realm admin

See [Declarative realm provisioning](../../admin/realm-provisioning) for the manifest format and the two provisioning surfaces.

## 16. Auth log & audit

- [ ] `GET /api/admin/auth-log` returns events with actor, IP and timestamp, message rendered (not a raw template)
- [ ] Free-text search and date-range filters work
- [ ] GDPR-erased fields show as erased, not blank

## 17. Recovery CLI (break-glass)

- [ ] `recover list`
- [ ] `recover reset-2fa <username>`
- [ ] `recover set-email <username> <email>`
- [ ] `recover magic-link <username>`
- [ ] `recover rebuild-projections`

Run these against a running container with `docker exec modgud dotnet Modgud.Api.dll recover ...`.

## 18. OAuth flows (real relying party)

These need a separate demo SPA/backend acting as the client.

- [ ] Authorization Code + PKCE end-to-end
- [ ] Reference token (default) and JWT access tokens both work
- [ ] Refresh-token exchange + rotation
- [ ] RP-initiated logout (`/connect/logout` with `id_token_hint`)
- [ ] Client credentials (server-to-server)
- [ ] Device authorization flow (`/device`)

## 19. Token claims

- [ ] UserInfo carries `resource_access` keyed by app slug, with `roles` (not group names) per app
- [ ] No `resource_access` entry for an app the user isn't bound to
- [ ] No top-level `groups` claim (Modgud is an identity hub, not a groups-passthrough — see [Concepts → Authorization (RBAC)](../../concepts/groups-and-authorization))

## 20. Permission gating & bypass tiers

- [ ] A `realm:admin` user sees the full sidebar and every gate passes
- [ ] A `<resource>:admin` user is scoped to that resource only
- [ ] A plain `<resource>:read` user sees only the gated item
- [ ] Sidebar visibility always matches the backend gate (a hidden item also 403s by direct URL)

## 21. Multi-app scenarios

- [ ] The same user holds a permission in one app and a different permission in another app simultaneously, and each app's token/gate only sees its own grant
- [ ] A group with `BoundTo: []` (dormant) contributes nothing

## 22. Documentation sanity

- [ ] Public docs build is clean: `cd docs && pnpm build`
- [ ] In-app docs build is clean: `cd docs && pnpm build:in-app`
- [ ] No stale links into removed pages

## 23. Cross-cutting smoke

- [ ] Two-browser SignalR live update (e.g. a change request created in one browser appears in another's admin list)
- [ ] A valid session cookie survives a page reload without re-prompting for login
- [ ] No unexpected 4xx/5xx while navigating the admin UI normally
- [ ] Server log is clean of unhandled exceptions during a normal session
