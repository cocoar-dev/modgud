# Customization — Pages

Per-realm drag-and-drop editor for the SPA's login / logout / forgot-password screens. Editor stores a JSON schema per page-slot in the tenant DB; a future runtime sprint hooks `<CoarPageRenderer>` into those routes so the schemas actually render at login time.

::: warning Beta — gated behind an operator feature flag
This surface is **disabled by default**. The Pages tile in the sidebar is hidden, the routes redirect away, and the underlying API returns 404 until the operator flips `AppSettings.Features.PageBuilder = true`. See [Feature Flags](../admin/feature-flags) for how to turn it on in your environment.

The editor itself is functional and persists schemas. **Runtime rendering of stored schemas on `/login` / `/logout` / `/forgot-password` is not yet wired** — saving a schema in the editor does not currently change what end-users see. That second half is a separate sprint, planned but unscheduled.
:::

## When it's safe to enable

Today, **never in production** — the editor can produce schemas that the runtime can't yet render. Enable it in dev to:

- preview the editor flow and the per-slot palette
- pin down which page-slots you'd want to customise once the runtime ships
- give early feedback into `@cocoar/vue-page-builder` (Modgud is the first beta integration)

Permissions when the flag is on: `realm-settings:read` / `realm-settings:write`. The `realm:admin` bypass grants both. The flag itself is operator-level — realm admins cannot turn it on.

## Page slots (today)

Three hardcoded slugs. Adding more is a code change (slug allowlist + per-slot action list + tile entry).

| Slug | Purpose |
| --- | --- |
| `login` | Username + password + provider buttons. Hosts MFA-prompt actions too. |
| `logout` | Post-sign-out screen. |
| `password-forgot` | Email-address entry for the password-reset flow. |

## What the editor lets you compose

The page-builder is **headless** — you choose elements from a palette and arrange them in a stack/card/section layout. Per-slot whitelists keep the surface tight:

- **Containers**: stack, card, section, divider
- **Static content**: heading, paragraph
- **Inputs**: text-input, checkbox
- **Interactive**: button (with an action id), link, image (from the asset library)

Each slot defines its own list of **available actions** for buttons. For `login` that's `auth:login`, `auth:passkey`, `auth:magic-link`, `auth:forgot-password`, `auth:register`, `auth:mfa-totp`, `auth:mfa-email-otp`. The renderer dispatches the action when the button is clicked; unknown action ids are ignored by design (forward compat).

## Storage

Schemas live as a `Dictionary<string, string>` sub-document on the singleton `RealmSettings` record — keyed by slug, value is the serialised PageNode JSON. New slot additions don't need a schema migration; the dictionary grows implicitly.

Endpoints (all admin-gated, all return 404 when the feature flag is off):

| Method | Path | Behaviour |
| --- | --- | --- |
| `GET` | `/api/admin/customization/pages/{slug}` | Returns `{Slug, Schema}` or `Schema: null` if never saved. |
| `PUT` | `/api/admin/customization/pages/{slug}` | Persists. Body: `{Schema: "<json>"}`. Server validates as JSON (rejects malformed) and caps at 256 KB. |
| `DELETE` | `/api/admin/customization/pages/{slug}` | Clears the slot. Runtime falls back to the hardcoded default view. |

Slug charset: `a-z0-9-`, length 1–32. Anything else is a 400.

## Customisation vs. security

**The page-builder schema describes UI, never security policy.** MFA enforcement, password policy, account-lockout, login-provider allowlist, rate limits, captcha — all of those live server-side in `RealmSettings` and `AppSettings`, completely independent of the schema. A customised login and the hardcoded default login enforce identical security; only the visual layout differs.

That property means a future safe-mode URL (`/login?safemode=1`) that bypasses the schema is a pure UX recovery, not a security bypass — the same backend policies apply whichever rendering path the page takes.

## What's coming (deferred sprint)

When the runtime sprint lands:

- `<CoarPageRenderer>` mounted on `/login`, `/logout`, `/forgot-password`. Per-slug resolver: stored schema → render; missing → hardcoded fallback view.
- Each auth step is its own slug (`login`, `mfa-totp`, `mfa-email-otp`, …). Action handlers decide the next step; the state machine stays in code, not in the schema.
- **Save-time validation**: the PUT endpoint will gain a "schema must contain the slot's primary-action button + required-field inputs" check, so you can't save a structurally broken page that locks the realm out.
- **Safe-mode URL**: `?safemode=1` will render the hardcoded fallback even when a stored schema exists. Universal recovery for realm admins; not auth-gated (it's UX, not security).
- **Recovery CLI fallback**: `recover reset-page --realm <slug> --page <slug>` for the SaaS-operator backstop when even safe-mode can't help.

Until that sprint ships, leave the feature flag off in production.
