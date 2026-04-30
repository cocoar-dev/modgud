# Manual smoke checklist

End-to-end smoke pass for the live system. Order is **outside-in**: bring
the system up first, then exercise auth, then admin, then OAuth, then the
cross-cutting flows.

> **Convention:** `[ ]` = open, `[x]` = done, `[!]` = found a problem
> (write a one-line note next to it).

[[toc]]

## 0. Bring-up

- [ ] Backend builds: `cd src/dotnet && dotnet build`
- [ ] Postgres container running: `docker ps | grep cocoar-postgres`
- [ ] Master DB exists: `docker exec cocoar-postgres psql -U postgres -lqt | grep cocoar_auth_next`
- [ ] Backend starts cleanly: `ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile` in `Cocoar.Auth.Api/`
- [ ] No errors on startup (bootstrap path: master DB → schema → system tenant → realm seed → cache warm)
- [ ] Frontend dev server starts: `cd src/frontend-vue && pnpm dev`
- [ ] `http://localhost:4300` loads without console errors

## 1. First-time setup

- [ ] `/setup` is reachable on a fresh DB
- [ ] Submit username `admin`, password `ABC12abc!`, optional email
- [ ] Auto-login lands on dashboard
- [ ] Sidebar shows **all** sections (Authorization, OAuth, Identity, System) — confirms `realm:admin` works
- [ ] `Auth log` lists `UserCreated` + `UserLoggedIn` for the new admin

## 2. Login & sign-out

- [ ] Sign out via header menu — lands on `/login`
- [ ] Wrong password 5× → account locked (1 min lock)
- [ ] Correct password unlocks after the timeout
- [ ] "Remember me" persists the cookie across browser restart (cookie expiry visible in DevTools)
- [ ] Magic-link request: enter email on `/login`, check email inspector at `/api/dev/emails`, click the link, lands logged in
- [ ] Logout-everywhere from `/profile/sessions` ends every active session

## 3. Two-factor

- [ ] Enable TOTP from `/profile`: scan QR, enter 6-digit code, recovery codes shown
- [ ] Sign out + sign in: TOTP step appears, code validates
- [ ] One recovery code consumed once; same code is rejected on a second try
- [ ] Enable Email-OTP, sign in with code from `/api/dev/emails`
- [ ] Add a passkey (FIDO2): platform authenticator works
- [ ] Passkey login: username + passkey only, no password prompt
- [ ] Disable each 2FA method in `/profile`; admin grace period kicks in if `AuthenticationMinimumLevel >= 1`

## 4. Profile self-service

- [ ] Edit `Firstname` / `Lastname` / `Acronym` — saves immediately
- [ ] Change email → admin gets notification, change-request appears under Admin → Change Requests
- [ ] Verify email link from `/api/dev/emails` flips request to `EmailVerified`
- [ ] Admin approves: new email is live; rejecting reverts cleanly
- [ ] GDPR export: download JSON from `/profile/privacy`, sanity-check fields
- [ ] GDPR delete request: confirmation token sent → deletion confirms → user is masked, sessions ended

## 5. Sessions (self + admin)

- [ ] `/profile/sessions` lists current session with browser/OS/device/IP
- [ ] Open a second browser → new session row appears in the first one
- [ ] Revoke the second session → second browser is signed out on next request
- [ ] Admin → Users → user → Sessions: same view + force-logout works

## 6. Users (admin)

- [ ] Create a user (no password, only email) — they can sign in via magic link
- [ ] Edit a user's profile fields
- [ ] Lock / unlock a user via the unlock endpoint
- [ ] Soft-delete a user → no longer in lookup; admin GDPR delete tear-down works
- [ ] Admin sends magic link from the user detail; recipient can use it
- [ ] 2FA grace-period extension visible + editable per user

## 7. Roles

- [ ] Create role `User Reader` with `AppSlug = cocoar-auth`, `ResourceType = user`, `Permissions = [read]`
- [ ] Edit: change description, add `write` action
- [ ] Delete: confirms; old assignees keep the role id but resolution returns nothing
- [ ] Verify the three default roles exist after first-time setup (System Admin = `realm:admin`, User Manager, Viewer)

## 8. Groups (this is where Phase 6 changes most)

- [ ] Group detail modal has tabs: **General**, **Members**, **Script** (auto only), **Roles**, **Effective** — **no "Access" tab anymore**
- [ ] Create a Manual group, add members (Person + nested Group), assign a role
- [ ] **Bound to apps** MultiSelect lists the registered apps + the `★ All apps (*)` wildcard
- [ ] Empty BoundTo → group is dormant: members get nothing from its roles
- [ ] BoundTo `["cocoar-auth"]` → contributes only on `cocoar-auth:*` lookups
- [ ] BoundTo `["*"]` → contributes everywhere
- [ ] Remove an app from BoundTo → role assignments stay (no cascade), permission goes silent for that app
- [ ] Re-adding the app reactivates the group
- [ ] Auto-Membership: switch to Auto, write `(p) => Type.Is(p, 'person') && p.IsActive`, save → Effective tab shows the matched users
- [ ] Membership-script error path: type a broken script → `MembershipLastError` shown in the modal
- [ ] Cycle prevention: try adding a descendant group as a member → 400 with cycle error

## 9. Apps

- [ ] Admin → Applications: `cocoar-auth` listed as system app, cannot be deleted
- [ ] Create app `timetodo` with display name + description + resources (`todo`, `project`)
- [ ] System app's `IsSystem=true` flag visible
- [ ] App detail: Klick-Aktion **Create default resource server** provisions an OAuth API
- [ ] One-time secret reveal: clear-text shown once, masked thereafter
- [ ] Pressing the button again shows the existing RS without rotating
- [ ] Try creating slug `realm` / `*` / `cocoar-auth` → reserved-slug rejection
- [ ] Lookup endpoint `GET /api/app/lookup` returns minimal shape

## 10. OAuth Clients

- [ ] Create a confidential web client; assign one or more apps via **AppIds** MultiSelect
- [ ] Edit AppIds: add and remove an app; on remove, scopes pinned to that app stop validating
- [ ] Rotate secret: new secret shown once
- [ ] Delete (soft); audit entry present
- [ ] Empty AppIds = realm-wide client
- [ ] PATCH semantics: omit AppIds = no change; `[]` = detach-all

## 11. OAuth Scopes

- [ ] Standard scopes (`openid`, `email`, `profile`, `roles`, `offline_access`) seeded with `AppId = null` (global)
- [ ] Create custom scope with `AppId = timetodo` → discoverable on `/connect/.well-known/openid-configuration`
- [ ] Scope on app A used by client linked only to app B → token endpoint rejects
- [ ] App-scoped scopes flagged visually in the list

## 12. OAuth APIs (Resource Servers)

- [ ] Create RS, link to app `timetodo`
- [ ] Add a parallel API secret (rotation rehearsal); both work simultaneously
- [ ] Delete the old secret; only the new one works
- [ ] Move RS to a different app → distribution-API responses switch context
- [ ] RS without linked App → distribution-API call returns 400 `ResourceServerUnassigned`

## 13. Login Providers + IdP Config (OIDC)

- [ ] Internal Login Provider listed and active by default
- [ ] Create an IdP config (Entra ID flavor): client id + secret + tenant
- [ ] Generic OIDC flavor: discovery URL → Test connection succeeds
- [ ] UserUpdateScript runs on first JIT login: claims map onto user fields (`Firstname`, `Acronym`, …)
- [ ] Test-script endpoint executes the script with sample claims and returns the resulting user diff
- [ ] Disable an IdP → button gone from `/login`
- [ ] External login: `/login` → click IdP → callback → user JIT-created + signed in
- [ ] `ExternalIdentityLink` exists on the user detail (admin view)
- [ ] Account-linking from `/profile` adds a second IdP to the same user

## 14. Realms (system realm only)

- [ ] In the system realm, `/admin/realms` lists the system realm + any others
- [ ] Create realm `acme`: DB created (`cocoar_auth_next_acme`), `realms.mt_tenant_databases` row, `acme` realm document
- [ ] Visit `acme.<host>/setup` → first-time-setup runs in the new realm
- [ ] Cross-realm isolation: data in `acme` invisible from `system` and vice versa
- [ ] Outside the system realm, `/admin/realms` returns 404 (not 403 — leak-proof)
- [ ] Edit realm: domain list updates; `Slug` is read-only
- [ ] Deactivate realm → all requests to its domain return 404

## 15. Auth log

- [ ] Filters work: timestamp, event type, actor, target, outcome
- [ ] Free-text search hits actor, target, event type
- [ ] Failed login burst is visible (force 3+ wrong passwords)
- [ ] GDPR-erased user: PII fields show `***ERASED***`, stable id retained

## 16. Settings

- [ ] `/admin/settings` only opens for `realm:admin` (System Admin) — User Manager / Viewer get 403
- [ ] Toggle `AuthenticationMinimumLevel`: enforcement middleware kicks in immediately
- [ ] Branding fields persist + render
- [ ] Magic-link self-service toggle hides the form on `/login` when off

## 17. Recovery CLI (break-glass)

In a separate shell, with the API stopped:

- [ ] `dotnet Cocoar.Auth.Api.dll recover list` lists users
- [ ] `recover reset-2fa <username>` clears TOTP/passkey
- [ ] `recover set-email <username> <new@example.com>` updates the email
- [ ] `recover magic-link <username>` prints a one-shot link
- [ ] `recover rebuild-projections` runs to completion

## 18. OAuth flows (real RP)

Spin up the demo SPA + backend, point at the local IdP, run:

- [ ] **Authorization Code + PKCE** (web client): redirect → consent (if any) → callback → ID token + access token + refresh token
- [ ] Access token is a **reference token** by default (opaque string, not JWT)
- [ ] Switch the client to JWT → access token decodes via jwt.io with the realm issuer
- [ ] Refresh token: refresh exchange returns a new access token, old one is invalidated (reference) or runs to natural expiry (JWT)
- [ ] Logout (`/connect/logout`): RP-initiated logout works, ID-token-hint accepted
- [ ] **Client credentials**: server-to-server, returns access token without user
- [ ] **Device code** (lightly tested): polling + completion flow

## 19. Token claims (Phase 4 — Keycloak resource_access)

- [ ] UserInfo response carries `resource_access` keyed by app slug
- [ ] Each `resource_access[<app>].roles` lists role **names** (not group names)
- [ ] No `groups` claim emitted from `/me` cookie (IDP/IAM split — Phase 5)
- [ ] `Cocoar.Auth.Client.AspNetCore` library on the RP flattens roles to `ClaimTypes.Role` so `[Authorize(Roles="…")]` works without per-endpoint code

## 20. Distribution API (Phase 5)

- [ ] `GET /api/v1/distribution/me-permissions` with **only** Bearer → 401 + `WWW-Authenticate: CocoarAuthRS`
- [ ] Same call with `X-Resource-Server-Id` + `X-Resource-Server-Secret` → 200, returns `{UserId, AppSlug, Permissions, Groups, Roles}`
- [ ] App context derived from RS (no `?app=` accepted/needed)
- [ ] Permissions are **fully qualified** `<app>:<resource>:<action>` — verify shape
- [ ] `Cache-Control: private, max-age=30` set on the response
- [ ] Wrong RS secret → 401
- [ ] RS not linked to an App → 400 `ResourceServerUnassigned`

## 21. Permission gating + bypass tiers (3-segment, Phase 1)

Set up three test users in the same realm, each in a separate group with a
different role:

- [ ] `realm-admin-user` (group `BoundTo: ["*"]`, role with `realm:admin`) → sees everything in every app
- [ ] `app-admin-user` (group `BoundTo: ["cocoar-auth"]`, role with `cocoar-auth:admin`) → sees the IAM admin sidebar fully, gets 403 on `timetodo:*`
- [ ] `resource-admin-user` (role with `cocoar-auth:user:admin`) → can do every action on Users, gets 403 on Roles
- [ ] `read-only-user` (role with only `cocoar-auth:user:read`) → sees Users in the sidebar, **only** Users; everything else is hidden
- [ ] Sidebar visibility matches the backend gate: a hidden item also returns 403 if hit by URL

## 22. Multi-app scenarios

- [ ] Create app `timetodo`, role `Editor` with `AppSlug = timetodo`, `ResourceType = todo`, action `write`
- [ ] Group with that role + `BoundTo: ["timetodo"]`, member: `read-only-user`
- [ ] User now has `cocoar-auth:user:read` AND `timetodo:todo:write` simultaneously
- [ ] Distribution API for `timetodo` returns the `timetodo:todo:write` grant
- [ ] Distribution API for an unrelated app the user is not bound to returns no permissions
- [ ] Same role on a group with `BoundTo: []` contributes **nothing**

## 23. Documentation sanity

- [ ] VitePress build clean: `cd website && pnpm build`
- [ ] In-app build clean: `pnpm build:in-app`
- [ ] Open `http://localhost:5173/` (or the preview URL) → landing page renders
- [ ] No "Access Scripts (ABAC)" page in the sidebar
- [ ] Concepts → ABAC and the IAM boundary page renders
- [ ] Concepts → Authorization (RBAC) page describes the 3-segment model + 3 bypass tiers
- [ ] Admin → Groups page mentions the no-row-level-ABAC info box
- [ ] No remaining German prose anywhere in the rendered docs

## 24. Cross-cutting smoke

- [ ] Tab open in two browsers as the same user: SignalR live update reflects role/group changes within ~1 s
- [ ] Change-request created in browser A appears in browser B's admin list immediately
- [ ] Pressing F5 anywhere doesn't prompt re-login as long as the cookie is valid
- [ ] Network DevTools: no 4xx/5xx during normal admin navigation
- [ ] Server log clean of unexpected stack traces during a regular session

---

## Found-issues log

Track anything you flag with `[!]` here, one line each:

- ...
- ...
- ...
