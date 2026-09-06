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

Four slugs. Adding more is a code change (per-slot action list + tile entry).

| Slug | Purpose |
| --- | --- |
| `login` | Username + password + provider buttons. Hosts MFA-prompt actions too. |
| `logout` | `/logged-out` confirmation after local or federated sign-out. |
| `password-forgot` | Email-address entry for the password-reset flow. |
| `consent` | The OAuth consent screen. |

## What the editor lets you compose

The page-builder is **headless** — you choose elements from a palette and arrange them in a stack/card/section layout. The shared editor/runtime allowlist keeps the surface tight:

- **Containers**: stack, card, section, divider, spacer
- **Static content**: heading, paragraph, note, Application-brand header
- **Inputs**: text, password, checkbox — bound only to the slot's declared fields
- **Interactive**: button/link with an allowlisted action, image from the asset library
- **Login only**: a live external-login-provider block

Each slot defines its own list of **available actions**. Login supports credentials, passkey, magic-link, forgot-password, and register; forgot-password supports submit/back; logout supports back-to-login. The runtime only provides handlers for that fixed list. MFA choice, TOTP/email OTP, and secure-setup screens intentionally remain fixed UI: a schema cannot weaken or skip those transitions.

## Variants and activation

The variant library is **realm-global** (ADR-0013): each slot owns a set of named variants, authored in **Platform → Pages**. Three concepts stay separate — *a variant exists*, *a variant is live*, and *the built-in fixed view* — so you can:

- author several variants for one slot (e.g. two login layouts) and switch which is live,
- **deactivate** a variant (set the slot back to Built-in) without deleting it, and
- reset the editor to the built-in template as a purely local action — nothing is persisted until you Save, and Saving creates/updates a variant, it never deletes.

**Where you do what:**

- **Platform → Pages** is a grid of all variants (name, type, *Used By* count with a hover of the exact consumers, status, last-updated). Right-click creates a new Login / Logout / Forgot-password / Consent page (the toolbar button is the shortcut for Login); double-click edits; the context menu deletes.
- **Realm settings → Sign-in pages** has one selector per slot choosing the realm's live variant — **Built-in** or a variant.
- An **Application → Settings → Pages** has the same selectors, each **Inherit realm** (default) / **Built-in** / one of the realm variants. An App never authors its own variant — it only *selects* from the realm library.

Effective resolution per slot: **app selection → realm selection → built-in**. A slot resolved to Built-in is simply absent from the schema the SPA receives, so the runtime renders the hardcoded view.

## Draft, publish, activate

Saving in the editor does **not** put a page in front of users. Each variant holds an editable **draft** plus the **published revision** the runtime actually serves, and they are separate on purpose: you can leave a half-finished layout saved for days while the live page stays what it was.

1. **Save** writes the draft. The variant list then reports `HasUnpublishedChanges: true`, and `IsPublished` stays `false` until the first publish.
2. **Publish** validates the draft, compiles it (compositions are materialised into the document at this point), appends an **immutable revision** and promotes it. Publishing a draft that is identical to the published one does nothing.
3. **Activate** points the slot at a variant. If that variant has unpublished changes, activating publishes it first — so activation can never put an unvalidated document live.

The runtime always serves the **published** revision, never the draft. Variants that predate this model keep working: if a variant has no published revision yet, its draft is served until the next edit publishes it through the validation gate.

**Rollback** re-publishes an earlier revision as a *new* revision, recording which revision it came from. History is never rewritten, so the audit trail of what was live and when stays intact. Each variant keeps its **last 100 revisions**; older ones are pruned.

Validation happens at the publish boundary, not at save time. A structurally invalid document can be saved as a draft, but publishing or activating it returns `400` with the reason — for example `Root node must be an object with type 'page'`.

## Storage

The realm variant library + the realm's active selection live in `RealmSettings.PageSlots` (keyed by slot). Each Application's per-slot selection (inherit / built-in / a realm variant id) lives in `ApplicationSettings.PageSlots`. Legacy single-schema `Pages` data migrates on first touch — a realm entry becomes an active "Custom" variant; an App entry (which can no longer be represented) is dropped, so the App inherits.

Endpoints (all admin-gated, all return 404 when the feature flag is off):

| Method | Path | Behaviour |
| --- | --- | --- |
| `GET` | `/api/admin/customization/pages` | Lists every slot with its variant summaries + active id. |
| `GET` | `/api/admin/customization/pages/{slug}` | One slot: its variant summaries + active id. |
| `GET` | `/api/admin/customization/pages/{slug}/variants/{id}` | Returns `{Id, Name, Schema}` for one variant — the **draft**. |
| `POST` | `/api/admin/customization/pages/{slug}/variants` | Creates a variant. Body: `{Name, Schema}`. Does **not** publish or activate it. |
| `PUT` | `/api/admin/customization/pages/{slug}/variants/{id}` | Updates a variant's name/draft schema. |
| `POST` | `/api/admin/customization/pages/{slug}/variants/{id}/publish` | Validates and compiles the draft, appends a revision, promotes it. Returns `{Id, PublishedRevision, PublishedAt}`. |
| `POST` | `/api/admin/customization/pages/{slug}/variants/{id}/rollback/{revision}` | Re-publishes that revision as a new revision. |
| `DELETE` | `/api/admin/customization/pages/{slug}/variants/{id}` | Removes a variant; the realm active pointer clears if it targeted it. |
| `PUT` | `/api/admin/customization/pages/{slug}/active` | Sets the realm's live variant. Body: `{ActiveVariantId: "<id>" \| null}` (null = Built-in). Publishes the variant first if it has unpublished changes. |

Each variant summary carries `Name`, `CreatedAt`, `PublishedRevision`, `IsPublished`, `HasUnpublishedChanges`, `RealmActive` and `UsedByApps`.

Schemas validate as JSON (malformed rejected) and cap at 256 KB; variant names cap at 80 chars; max 50 variants per slot; max 100 retained revisions per variant.

Application endpoints under `/api/app/{applicationId}/pages`: `GET` returns each slot's selection (`InheritActive`, `ActiveVariantId`) plus the `AvailableVariants` (the realm library) to choose from; `PUT /{slug}/active` takes `{Inherit: bool, ActiveVariantId: "<id>" \| null}` where the id must be a **realm** variant — `Inherit: true` defers to the realm, `false` + `null` forces Built-in, `false` + an id selects that realm variant. Regular Application-settings saves do not touch the page selection.

Slug charset: `a-z0-9-`, length 1–32. Anything else is a 400.

## Customisation vs. security

**The page-builder schema describes UI, never security policy.** MFA enforcement, password policy, account-lockout, login-provider allowlist, rate limits, captcha — all of those live server-side in `RealmSettings` and `AppSettings`, completely independent of the schema. A customised login and the hardcoded default login enforce identical security; only the visual layout differs.

Stored JSON is normalized to the current v2 schema before rendering. Unknown or disallowed elements are skipped, action IDs are matched against host-owned handlers, and invalid/unavailable schemas fall back to the fixed screen. Safe mode is therefore a UX recovery path, not a security bypass — the same backend policies apply whichever rendering path the page takes.
