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

## Variants and activation

Each slot owns a **library of named variants** plus an **active selection** — three separate concepts (ADR-0001): *a variant exists*, *a variant is live*, and *the built-in fixed view*. This means you can:

- author several variants for one slot (e.g. two login layouts) and switch which is live,
- **deactivate** a variant (set the slot back to Built-in) without deleting it, and
- reset the editor to the built-in template as a purely local action — nothing is persisted until you Save, and Saving creates/updates a variant, it never deletes.

**Activation is a settings decision, not an editor action.** In the Pages overview each slot has an *Active for realm* selector (Built-in / a variant). The overview also badges which variant is live so you can see the blast-radius before editing one. Applications set their own *Active for this app* selector (Inherit realm / Built-in / an app variant) in **Settings → Pages**.

Effective resolution per slot: **app selection → realm selection → built-in**. A slot resolved to Built-in is simply absent from the schema the SPA receives, so the runtime renders the hardcoded view.

## Storage

Realm variants + activation live in `RealmSettings.PageSlots`; Application variants + activation live in `ApplicationSettings.PageSlots` (both keyed by slot). Legacy single-schema `Pages` dictionaries are migrated to a single active "Custom" variant on first touch.

Endpoints (all admin-gated, all return 404 when the feature flag is off):

| Method | Path | Behaviour |
| --- | --- | --- |
| `GET` | `/api/admin/customization/pages` | Lists every slot with its variants (summaries) + active variant id. |
| `GET` | `/api/admin/customization/pages/{slug}/variants/{id}` | Returns `{Id, Name, Schema}` for one variant. |
| `POST` | `/api/admin/customization/pages/{slug}/variants` | Creates a variant. Body: `{Name, Schema}`. Does **not** activate it. |
| `PUT` | `/api/admin/customization/pages/{slug}/variants/{id}` | Updates a variant's name/schema. |
| `DELETE` | `/api/admin/customization/pages/{slug}/variants/{id}` | Removes a variant; if it was active the slot reverts to Built-in. |
| `PUT` | `/api/admin/customization/pages/{slug}/active` | Sets the live variant. Body: `{ActiveVariantId: "<id>" \| null}` (null = Built-in). |

Schemas validate as JSON (malformed rejected) and cap at 256 KB; variant names cap at 80 chars; max 50 variants per slot.

Application endpoints use `/api/app/{applicationId}/pages/...` with the same variant CRUD, plus `PUT /{slug}/active` taking `{Inherit: bool, ActiveVariantId: "<id>" \| null}` — `Inherit: true` defers to the realm; `false` + `null` forces Built-in; `false` + an id activates an app variant. Regular Application-settings saves do not touch page variants.

Slug charset: `a-z0-9-`, length 1–32. Anything else is a 400.

## Customisation vs. security

**The page-builder schema describes UI, never security policy.** MFA enforcement, password policy, account-lockout, login-provider allowlist, rate limits, captcha — all of those live server-side in `RealmSettings` and `AppSettings`, completely independent of the schema. A customised login and the hardcoded default login enforce identical security; only the visual layout differs.

Stored JSON is normalized to the current v2 schema before rendering. Unknown or disallowed elements are skipped, action IDs are matched against host-owned handlers, and invalid/unavailable schemas fall back to the fixed screen. Safe mode is therefore a UX recovery path, not a security bypass — the same backend policies apply whichever rendering path the page takes.
