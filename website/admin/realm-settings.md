# Realm Settings

**Realm Settings** are realm-wide configuration owned by the **realm admin** (not the Control-Plane admin). They live in the tenant database as a singleton document and are managed via Administration → **Realm Settings**.

::: info Realm structure vs. Realm settings
- **[Realms](./realms)** (structural metadata: slug, domains, Control-Plane flag, active state) are managed by the **Control-Plane admin** only.
- **Realm Settings** (self-registration, DCR policy, branding, …) are managed by the **realm admin** inside their own realm. The Control-Plane admin reaches their own realm's settings the same way as any other realm admin would — through this page.
:::

## Tabs

The page is a tab surface. Each tab is independently saved and described in its own section below or on a linked page:

- [Self-Registration](#self-registration) — public sign-up policy
- [Dynamic Client Registration](#dynamic-client-registration) — anonymous OAuth-client registration policy (linked detail page: [Dynamic Client Registration](./dynamic-client-registration))
- [Branding](#branding) — per-realm SPA-shell branding (linked detail page: [Customization — Branding](./customization-branding))

Future tabs (password-policy overrides, branded email templates, …) land as additional tabs here without needing a new admin page.

Permissions: `realm-settings:read` / `realm-settings:write`. The realm-admin role grants both via the `realm:admin` bypass.

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
- **Air-gapped / intern deployment** → leave captcha disabled. Cocoar.Auth then never calls out to `challenges.cloudflare.com`. Honeypot field + per-email rate-limit (1/min, 3/hour) cover the bot-spam surface.

Resolution order when the captcha is enabled:

1. Per-realm site key + per-realm secret if both set
2. Cocoar-default site key + Cocoar-default secret
3. None configured → the verifier rejects every registration and logs a `WARN` so the admin notices the misconfiguration

::: tip One captcha secret per realm
The captcha secret is encrypted with ASP.NET Data Protection (purpose `Cocoar.Auth.SelfRegistration.CaptchaSecret.v1`). Migrating data-protection keys between deployments invalidates all per-realm captcha secrets — they need to be re-entered. The same warning applies to login-provider client secrets.
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

## Dynamic Client Registration

Anonymous OAuth-client registration policy: master toggle, token lifetimes, GC TTL, rate limits, reserved-name blocklist. The companion per-API and per-Scope toggles live on [OAuth APIs](./oauth-apis) and [OAuth Scopes](./oauth-scopes) respectively — DCR is a **triple opt-in** by design.

Off by default. See the full feature page for when to enable it, what gets accepted, and the consent-screen `[unverified]` marker:

→ **[Dynamic Client Registration](./dynamic-client-registration)** (full feature page)

## Branding

Per-realm SPA-shell branding: product name, primary color, logo, favicon. Logo and favicon reference uploaded files from the [Asset Library](./customization-assets). All four fields are optional — missing = SPA falls back to the Cocoar default.

The Branding tab here is the same form as the dedicated **Administration → Customization → Branding** view; both write the same `RealmSettings.Branding` sub-document. Editing in either place affects the same data.

→ **[Customization — Branding](./customization-branding)** (full feature page)

::: tip Where branding takes effect
Branding values are exposed by the public `/api/app-info` endpoint, which the SPA reads at boot for the login page (anonymous-reachable, no token needed). Changes take effect on the next page load — no SPA rebuild required.
:::
