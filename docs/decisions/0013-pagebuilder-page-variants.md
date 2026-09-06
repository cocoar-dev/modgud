# PageBuilder: named page variants and activation at realm and application level

**Status:** Accepted — shipped 2026-07-22 (PR #167, refined by #168) · **Decided:** 2026-07-22

## PageBuilder: named page variants + activation

### Context

The initial PageBuilder runtime (PR #166, shipped dark behind `AppSettings.Features.PageBuilder`) stores **exactly one** schema per SPA page-slot (`login`, `logout`, `password-forgot`). Field testing surfaced three UX problems rooted in conflating three distinct concepts under one entry:

1. **"Reset to default" was destructive and overloaded** — it deleted server-side, then repopulated the editor with a template, so Save re-created a custom page and users could never return to the built-in login.
2. **No way to deactivate without destroying** an elaborate custom page.
3. **Only one page per slot** — operators want several variants and to pick which is live per realm and per application.

### Decision

Introduce **named page variants** plus an explicit **activation pointer**, decoupling *a schema exists* ≠ *it is active* ≠ *the built-in fallback*.

#### Model (refined during implementation)

The variant library is **realm-global**. Applications do not author their own variants — they only *select* one of the realm variants.

- `PageVariant { Id (Guid string), Name, Schema (opaque JSON), CreatedAt, UpdatedAt? }`
- Realm slot config (`RealmSettings.PageSlots[slug]`): `{ Variants: PageVariant[], ActiveVariantId: string? }` — `null` ⇒ **built-in**.
- App slot config (`ApplicationSettings.PageSlots[slug]`): `{ InheritActive: bool (default true), ActiveVariantId: string? }` — `InheritActive` ⇒ defer to realm; when overriding, `null` ⇒ built-in, else a **realm** variant id. No app-owned variant list.

Legacy `Pages` dictionaries migrate on load: a realm entry becomes one active "Custom" variant; an App entry (no longer representable) is dropped so the App inherits. The feature only ran dark on a test environment, so blast-radius is ~zero.

#### Effective resolution (per slot)

1. Application in context and not inheriting → its selection (built-in, or the realm variant it points at).
2. Else the realm's selection (built-in or realm variant).
3. Missing/`null` → the **built-in** hardcoded SPA view.

`/api/app-info` keeps its wire shape (`Pages: Record<slug, schemaJson>`) — it publishes the single *effective active* schema per slot, so the SPA runtime is unchanged.

#### UI

- **Platform → Pages** is a data grid of every variant (name, type, *Used By* count with a hover of the exact consumers, updated). Right-click / toolbar creates a Login / Logout / Forgot page; double-click edits; context menu deletes. Variants are authored only here.
- **Realm settings → Pages**: three selectors (login / logout / forgot) choosing the realm's live variant (Built-in / a variant).
- **Application → Settings → Pages**: three selectors, each Inherit realm (default) / Built-in / a realm variant.
- "Reset to default" in the editor is a **UI-only** buffer load (no server call); Save creates/updates a variant, never deletes.

### Consequences

- Backend: new domain types + lazy migration; `CustomizationPagesEndpoints` provides realm variant CRUD + set-active, and a per-App select endpoint (`GET` returns the realm library as options, `PUT /{slug}/active` validates the id against the realm library); `EffectiveSettings` resolution is activation-aware; the realm list endpoint computes each variant's `UsedByApps`.
- Frontend: Pages grid with usage + right-click create; variant-scoped editor with non-destructive reset; realm + app settings gain three page selectors.
- The built-in hardcoded views remain the always-safe fallback and the `?safemode=1` escape hatch — unchanged.
- Supersedes the single-schema-per-slot storage introduced in PR #166 (same flag, still dark until enabled).
