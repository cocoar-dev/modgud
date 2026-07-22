# Customization — Pages

Drag-and-drop editor for the SPA's login, signed-out, and forgot-password screens. Schemas can be defined as Realm defaults and overridden per Application. The SPA renders them with `<CoarPageRenderer>` at runtime.

::: warning Beta — gated behind an operator feature flag
This surface is **disabled by default**. The Pages tile in the sidebar is hidden, the routes redirect away, and the underlying API returns 404 until the operator flips `AppSettings.Features.PageBuilder = true`. See [Feature Flags](../operate/feature-flags) for how to turn it on in your environment.

The flag gates the editor, persistence endpoints, anonymous schema delivery, and runtime rendering together. Stored schemas remain in the database while the flag is off.
:::

## When it's safe to enable

The end-to-end path is wired, but remains opt-in while the PageBuilder integration is beta. Enable it first in a test realm and verify each saved page at desktop and mobile widths before enabling it in production.

- Realm pages require `realm-settings:read` / `realm-settings:write`.
- Application overrides require `app:read` / `app:write` and are opened from the Application's Settings → Pages tab.
- `?safemode=1` on `/login`, `/forgot-password`, or `/logged-out` bypasses a stored schema for UX recovery.

The flag itself is operator-level — realm admins cannot turn it on.

## Page slots (today)

Three hardcoded slugs. Adding more is a code change (slug allowlist + per-slot action list + tile entry).

| Slug | Purpose |
| --- | --- |
| `login` | Username + password + provider buttons. Hosts MFA-prompt actions too. |
| `logout` | `/logged-out` confirmation after local or federated sign-out. |
| `password-forgot` | Email-address entry for the password-reset flow. |

## What the editor lets you compose

The page-builder is **headless** — you choose elements from a palette and arrange them in a stack/card/section layout. The shared editor/runtime allowlist keeps the surface tight:

- **Containers**: stack, card, section, divider, spacer
- **Static content**: heading, paragraph, note, Application-brand header
- **Inputs**: text, password, checkbox — bound only to the slot's declared fields
- **Interactive**: button/link with an allowlisted action, image from the asset library
- **Login only**: a live external-login-provider block

Each slot defines its own list of **available actions**. Login supports credentials, passkey, magic-link, forgot-password, and register; forgot-password supports submit/back; logout supports back-to-login. The runtime only provides handlers for that fixed list. MFA choice, TOTP/email OTP, and secure-setup screens intentionally remain fixed UI: a schema cannot weaken or skip those transitions.

## Storage

Realm schemas live in `RealmSettings.Pages`; Application overrides live in `ApplicationSettings.Pages`. Both are dictionaries keyed by slot. Effective settings overlay Application keys over Realm keys, so an Application can override only `login` and inherit the other slots.

Endpoints (all admin-gated, all return 404 when the feature flag is off):

| Method | Path | Behaviour |
| --- | --- | --- |
| `GET` | `/api/admin/customization/pages/{slug}` | Returns `{Slug, Schema}` or `Schema: null` if never saved. |
| `PUT` | `/api/admin/customization/pages/{slug}` | Persists. Body: `{Schema: "<json>"}`. Server validates as JSON (rejects malformed) and caps at 256 KB. |
| `DELETE` | `/api/admin/customization/pages/{slug}` | Clears the slot. Runtime falls back to the hardcoded default view. |

Application endpoints use `/api/app/{applicationId}/pages/{slug}` with the same methods. Application `GET` also returns `EffectiveSchema` and `InheritsRealm`; `DELETE` removes the override and resumes Realm inheritance. Regular Application-settings saves do not replace page schemas.

Slug charset: `a-z0-9-`, length 1–32. Anything else is a 400.

## Customisation vs. security

**The page-builder schema describes UI, never security policy.** MFA enforcement, password policy, account-lockout, login-provider allowlist, rate limits, captcha — all of those live server-side in `RealmSettings` and `AppSettings`, completely independent of the schema. A customised login and the hardcoded default login enforce identical security; only the visual layout differs.

Stored JSON is normalized to the current v2 schema before rendering. Unknown or disallowed elements are skipped, action IDs are matched against host-owned handlers, and invalid/unavailable schemas fall back to the fixed screen. Safe mode is therefore a UX recovery path, not a security bypass — the same backend policies apply whichever rendering path the page takes.
