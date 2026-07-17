# Realm Settings

**Realm Settings** are realm-wide configuration owned by the **realm admin** (not the Control-Plane admin). They live in the tenant database as a singleton document and are managed via Administration → **Realm Settings**.

::: info Realm structure vs. Realm settings
- **[Realms](./realms)** (structural metadata: slug, domains, Control-Plane flag, active state) are managed by the **Control-Plane admin** only.
- **Realm Settings** (self-registration, DCR policy, branding, …) are managed by the **realm admin** inside their own realm. The Control-Plane admin reaches their own realm's settings the same way as any other realm admin would — through this page.
:::

::: tip These are the realm defaults — Applications can override them
The Self-Registration, Registration-Fields, DCR, CIMD and Native Passwordless
Grants policies (and branding / email branding) set here are the **realm
defaults**. An individual [Application](./applications#application-settings)
can override a slice of them per-app (sparse, field by field — anything it
doesn't set inherits the realm value here). Captcha, account deletion and the
DCR garbage-collection interval stay realm-only.
:::

## Tabs

The page currently has these tabs:

- [Self-Registration](#self-registration) — public sign-up policy
- [Registration Fields](#registration-fields) — which identity fields are
  required when an account is created
- [Dynamic Client Registration](#dynamic-client-registration) —
  anonymous OAuth-client registration policy (linked detail page:
  [Dynamic Client Registration](./dynamic-client-registration))
- **Client ID Metadata Documents** — fetch-on-demand OAuth-client
  identification by HTTPS-URL `client_id` (linked detail page:
  [Client ID Metadata Documents](./client-id-metadata-documents)).
  Off by default.
- **Native Passwordless Grants** — the per-realm master toggle for the
  cookieless `urn:cocoar:*` grants (see [Native app integration](../integrate/native-apps))
- [Rate Limits](#rate-limits) — per-IP request ceilings for the realm's auth endpoints
- [Account Deletion](#account-deletion) — grace period and recycle-bin retention policy
- [Signing Keys](#signing-keys) — rotate the realm's OAuth/OIDC token-signing key

Per-realm **branding** is configured on a separate page under
Plattform — see [Customization → Branding](../plattform/branding).

Permissions: `realm-settings:read` / `realm-settings:write`. The
realm-admin role grants both via the `realm:admin` bypass.

## Self-Registration

Public sign-up: visitors can create an account themselves at `/register`. Opt-in per realm, **disabled by default** — anonymous probes to `/api/account/self-registration-info` return the all-defaults shape so disabled realms can't be enumerated.

### Enabling self-registration

1. Open Administration → **Realm Settings** → tab **Self-Registration**.
2. Check **Enable self-registration**.
3. Configure the additional fields that appear (see below).
4. **Save**.

Once enabled, the login page picks up a **"No account yet? Register →"** link and `/register` becomes reachable.

### Fields

| Field | Default | Meaning |
| --- | --- | --- |
| **Enable self-registration** | off | Master toggle. When off, the `/register` route returns the same anti-enumeration response as a never-registered email. |
| **Require email verification** | on | New accounts are created with `EmailConfirmed=false` and can't sign in until they click the magic-link in the verification mail. Turn off only for trusted-internal scenarios. |
| **Require admin approval** | off | Layered on top of email verification — after the user confirms the link, the account stays `IsActive=false` until an admin flips the flag manually. Useful for moderated communities. |
| **Allowed email domains** | empty (all) | Whitelist. Empty = accept any domain. Case-insensitive match on the part after the last `@`. |
| **Default groups** | empty | Groups the new user is auto-attached to once the account is fully active (post-verification + post-approval). Role memberships flow through groups, so this is the lever for "what can self-registered users do?". |
| **Terms-of-Service URL** | empty | When set: the registration form shows a required "I accept" checkbox linking here. The endpoint rejects submissions without the checkbox ticked. |
| **Privacy Policy URL** | empty | When set: rendered as a discreet footer link on the registration form. No checkbox. |
| **Enable Cloudflare Turnstile captcha** | off | Independent of the master toggle. See [Captcha](#captcha) below. |
| **Captcha site key** | empty | Per-realm Turnstile site key. Empty + captcha enabled = falls back to the Cocoar-default keys. |
| **Captcha secret** | not set | Per-realm Turnstile secret, encrypted at rest. Write-only — never returned, only an "is configured" flag. Empty + save = clear (revert to Cocoar default). |

### Captcha

Cloudflare Turnstile is the only supported captcha provider. It is **independent of the master toggle** so two scenarios both work:

- **Public-internet deployment** → enable captcha, configure either per-realm keys or rely on the Cocoar-default keys configured via the `Turnstile__SiteKey` / `Turnstile__SecretKey` environment variables.
- **Air-gapped / intern deployment** → leave captcha disabled. Modgud then never calls out to `challenges.cloudflare.com`. Honeypot field + per-email rate-limit (1/min, 3/hour) cover the bot-spam surface.

Resolution order when the captcha is enabled:

1. Per-realm site key + per-realm secret if both set
2. Cocoar-default site key + Cocoar-default secret
3. None configured → the verifier rejects every registration and logs a `WARN` so the admin notices the misconfiguration

::: tip One captcha secret per realm
The captcha secret is encrypted with ASP.NET Data Protection (purpose `Modgud.SelfRegistration.CaptchaSecret.v1`). Migrating data-protection keys between deployments invalidates all per-realm captcha secrets — they need to be re-entered. The same warning applies to login-provider client secrets.
:::

### Anti-enumeration

The public endpoints are explicitly engineered against enumeration:

- `POST /api/account/register` always responds with the same generic success message regardless of outcome: existing email, existing username, captcha failure, honeypot trigger, rate-limit, domain-whitelist rejection — all look identical to a real success from the client's perspective. No mail is sent in the rejected cases.
- `GET /api/account/self-registration-info` returns the same all-defaults shape (`Enabled=false`) whether the realm has the feature off, doesn't exist at all, or is currently being configured. The SPA reads `Enabled` to decide between rendering the form vs. redirecting to `/login`.

The email-verification endpoint (`POST /api/account/register/verify-email`) is the exception — it returns real error codes for expired / used / unknown tokens, because by the time someone is consuming a token they already have it, and there is nothing left to enumerate.

### What's stored where

| Data | Location |
| --- | --- |
| Realm-settings document (the toggles above) | tenant DB, singleton document `RealmSettings` |
| Pending self-registration (token-hash, user ID, expiry) | tenant DB, `mt_doc_pendingselfregistration` |
| User record | tenant DB, `mt_doc_applicationuser` (created at register time, activated on verify) |
| Captcha secret (encrypted) | inside the `RealmSettings` document, encrypted with Data Protection |

### Known limitations (current MVP)

- **No dedicated "pending approvals" UI.** When admin-approval is required, the user record is created with `IsActive=false` and an admin has to flip the flag from the regular user-edit modal. A filter chip on the user list for "pending approval" is a sensible follow-up.
- **In-memory rate limiter.** The per-email cap (1/min, 3/h) resets on restart and isn't shared across multiple instances. Single-instance deployments are fine; multi-instance setups can bypass the cap by hopping between instances. A Redis-backed implementation behind the same interface is a follow-up.
- **No pre-submit username availability check.** The form surfaces username collisions through the generic 200-OK like every other rejection. An anonymous `GET /api/account/check-username/{name}` (rate-limited) would improve the UX without touching the anti-enumeration guarantees on email.
- **Email template is shared with the email-change flow.** Both reuse `EmailTemplate.EmailVerification`. A dedicated `EmailTemplate.SelfRegistrationVerify` with welcome wording is a quality-of-life improvement.

## Registration Fields

Controls **which identity fields are required when a user account is created** —
across every creation path (admin create/edit, public self-registration, native
passwordless registration). **Email is always required** and is the anchor every
other field is resolved against; it is not configurable.

Each of the other three fields is a uniform tri-state. The **default is
`Optional` for all three**, which is exactly the historical behaviour — a realm
that never touches this tab behaves as before (zero change).

| Field | `Off` | `Optional` | `Required` |
| --- | --- | --- | --- |
| **Username** | hidden; the username **is always the email** | shown; blank → the email | a non-empty username must be supplied |
| **First name** | not collected | shown, may be blank | must be supplied |
| **Last name** | not collected | shown, may be blank | must be supplied |

### Why configure it

- **Consumer apps** (e.g. a task-management app) stay email-only — leave
  everything `Optional` (or set `Username = Off`) for the lowest-friction
  passwordless sign-up.
- **Enterprise apps** typically want a real first/last name on every account —
  set them `Required`. This is coherent with enterprise tenants that disable
  self-registration, but it is enforced on *all* paths regardless.

### How it is enforced

- A missing **required** field is rejected on every creation path. On the admin
  and native endpoints it surfaces as a hard `400`; on the anti-enumeration
  self-registration endpoint it is folded into the uniform generic response.
- The check is **independent of whether the email already exists**, so it never
  leaks account existence.
- The resolved policy is published anonymously at `GET /api/app-info`
  (`RegistrationFields`), so native apps and the web register form render exactly
  the inputs the realm (or App) requires.

::: info Per-Application override + native clients
A single [Application](./applications#application-settings) can override this
policy (e.g. an Enterprise App requiring names inside a tenant whose realm
default is lenient). When an App requires a field, its **native clients must
collect and send it** — see
[Integrate → Native apps](../integrate/native-apps#required-identity-fields).
:::

::: warning Federation (SSO) is lenient
Just-in-time accounts created from an external IdP (OIDC/SAML) are **not** held
to the required-field policy today — they take whatever `given_name` /
`family_name` claims the IdP provides. Tightening this is a possible follow-up.
:::

## Dynamic Client Registration

Anonymous OAuth-client registration policy: master toggle, token lifetimes, GC TTL, rate limits, reserved-name blocklist. The companion per-API and per-Scope toggles live on [OAuth APIs](./oauth-apis) and [OAuth Scopes](./oauth-scopes) respectively — DCR is a **triple opt-in** by design.

Off by default. See the full feature page for when to enable it, what gets accepted, and the consent-screen `[unverified]` marker:

→ **[Dynamic Client Registration](./dynamic-client-registration)** (full feature page)

## Rate Limits

Per-IP request ceilings for this realm's auth endpoints. Each policy is a **max requests / window (minutes)** pair, partitioned by source IP and applied **per realm**. The shipped defaults are the secure production posture — the knob exists so a test realm, dev, or a legitimately bursty consumer can raise a ceiling **without a modgud code change + redeploy**, and so a hardened realm can tighten one.

| Policy | Endpoint | Default |
| --- | --- | --- |
| Native OTP request | `POST /api/account/native/otp/request` (+ native register) | 5 / 60 min |
| Magic-link request | `POST /api/account/magic-link/request` | 5 / 60 min |
| Password-reset request | `POST /api/account/forgot-password` | 5 / 60 min |
| Email-OTP login verify | `POST /api/account/email-otp/login` | 30 / 1 min |
| Email verification resend | `POST /api/account/email/send-verification` | 5 / 60 min |
| Passkey ceremony begin / enroll | `POST /connect/passkey/begin` (+ enroll) | 60 / 5 min |
| First-admin bootstrap | `POST /api/account/bootstrap-admin` | 10 / 15 min |

Notes:

- A realm that never touches this tab behaves exactly as before — the defaults are the previously-hardcoded values.
- Limits are **per realm**: raising a ceiling on one realm does not affect another on the same shared IdP.
- The limiter is in-memory and resets on app restart; over-limit requests get `429 Too Many Requests`.
- A config change takes effect within a few seconds (a short cache smooths the per-request lookup).
- This governs the **request rate**; it is independent of per-challenge attempt counters (e.g. the email-OTP `MaxAttempts` brute-force lock) and of the realm's anti-enumeration uniform responses.

## Account Deletion

Controls the account-deletion lifecycle for this realm — the self-service grace period and the admin recycle-bin retention. These replace the old hardcoded 7-day confirm-token window. The mechanics of both flows are documented under [Users → recycle bin & permanent erase](./users#recycle-bin-permanent-erase) and [Profile → Privacy](../end-user/profile#privacy).

### Fields

| Field | Default | Meaning |
| --- | --- | --- |
| **Grace period (days)** | 30 | Self-service window: after a user requests deletion they stay able to sign in and cancel for this many days, then the account is auto-erased. |
| **Reminder lead (days)** | 2 | How many days before the grace deadline the "your account is about to be deleted" reminder email is sent. Must be **less than** the grace period to ever fire. |
| **Admin retention (days)** | 30 | Recycle-bin window: how long an admin-deleted (deactivated) account is kept before it becomes eligible for auto-purge. An admin can restore or permanently delete it at any point during this window. |
| **Auto-purge** | on | When on, a scheduled job permanently erases recycle-bin accounts once their retention window elapses. When off, the bin is only emptied by explicit admin action (*Delete permanently*). |

::: info One job drives all three
A single scheduled sweep (`account-lifecycle-sweep`) does the work for the whole realm: it sends reminders, auto-erases self-service accounts past their grace deadline, and (when auto-purge is on) empties the admin recycle bin past retention. Changing these values takes effect on its next run.
:::

## Signing Keys

Every realm signs its OpenIddict **access tokens and id tokens** with its own
RSA-2048 key — a token signed for realm A cannot validate against realm B's
JWKS, so the keys are the cryptographic core of realm isolation. The key is
generated lazily on first token issuance; this tab lets a realm admin **rotate**
it on demand.

### Rotating the key

1. Open Administration → **Realm Settings** → tab **Signing Keys**.
2. Click **Rotate signing key** and confirm.

On rotation:

- A fresh RSA keypair is generated and becomes the **active** signing key — all
  *new* tokens are signed with it immediately.
- The previous key is **retired** but kept in the realm's JWKS and verification
  set for a **30-day overlap window**, so tokens issued just before the rotation
  (and resource servers that cache the JWKS) keep validating until they
  naturally expire / refresh.
- Once the overlap window elapses, the retired key is **hard-deleted** by the
  `signing-key-janitor` scheduled job (see [Scheduled Jobs](./scheduled-jobs)).
  The in-memory verification set drops it on its own as soon as the window
  passes, even before the janitor runs.

::: warning When to rotate
Rotate on **suspected key exposure** or as scheduled hygiene — not casually.
Resource servers that cache the JWKS very aggressively (longer than typical)
may briefly reject freshly-signed tokens until they refresh their key set. The
30-day overlap is sized so a normally-behaving RS (which refreshes its JWKS far
more often) never sees an interruption.
:::

::: info Operator alternative
Rotation can also be triggered from the recovery CLI without UI access:
`recover rotate-signing-key --realm <slug>`. Both paths write an entry to the
[Auth Log](./auth-log).
:::

Permission: `realm-settings:write` (the `realm:admin` bypass grants it).

## Branding (separate page)

Branding lives on its own page under Plattform — go to
**Plattform → Customization → Branding**. It writes a sub-document on
the same `RealmSettings` doc but isn't surfaced as a tab here today.

→ **[Customization — Branding](../plattform/branding)**
