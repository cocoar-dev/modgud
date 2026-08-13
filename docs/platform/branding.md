# Customization — Branding

Per-realm SPA-shell branding so every tenant can present its own product name, colour, logo, and favicon at the login page and across the admin UI — without the operator rebuilding the SPA bundle.

::: info Default is "no branding"
Every realm starts unbranded. The Cocoar defaults (product name "Modgud", primary color, logo, favicon) apply until at least one branding field is set. Partial branding is supported — set just the logo and leave the colour at default, etc.
:::

::: info Reaching this surface
[Realm Settings](../admin/realm-settings#branding-separate-page) links out to this page for branding, but the editable form lives only here — under **Platform → Customization → Branding**.
:::

Permissions: `realm-settings:read` / `realm-settings:write`. The `realm:admin` bypass grants both.

## Fields

| Field | Default | Effect when set |
| --- | --- | --- |
| **Product name** | "Modgud" | Header title in the admin UI + login page; `document.title` prefix on every page. ≤ 100 characters. |
| **Primary color** | design-system blue | Drives the `--coar-color-primary` CSS variable. Accepts hex (`#rgb`, `#rrggbb`, `#rrggbbaa`), `rgb()` / `rgba()`, `hsl()` / `hsla()`, or a CSS named colour. **No** `calc()` / `var()` / arbitrary CSS — that's blocked at the API to prevent injection into the property value. |
| **Logo** | Modgud logo (`/idp-logo.svg`, white variant `/idp-logo-white.svg` for the dark header) | Header logo in the admin UI + login page. Pick from the [Asset Library](./assets) via the asset picker. |
| **Favicon** | `/idp-logo.svg` | Browser-tab icon. Same asset picker; the SPA rewrites the `<link rel="icon">` element at boot. |

::: tip Tri-state save semantics
Each field has three save states: **leave the value** (don't touch the input), **clear back to default** (empty the input and save), or **replace** (type / pick a new value). Clearing is how you revert to a Cocoar default without leaving a stale custom value behind.
:::

## Setting it up

1. Upload the images you want to use to the [Asset Library](./assets). At minimum a logo (any of the allowlisted formats — usually SVG or PNG with transparency). Favicon is typically a square `.ico` or small PNG.
2. Open **Administration → Customization → Branding**.
3. Fill in the form: type the product name, pick the primary colour, click the Logo / Favicon picker tiles and select from the uploaded assets.
4. **Save**. The branding sub-document is rewritten in the tenant DB.

## Where the values surface

| Surface | What it picks up |
| --- | --- |
| `/api/app-info` (anonymous, public) | All four fields, resolved as the *effective* branding for the request (realm branding merged with any per-Application override — see below). The endpoint resolves `LogoAssetId` / `FaviconAssetId` to public URLs (`/api/assets/{shortGuid}`) — anonymous callers never see the raw asset id. |
| Fixed authentication surfaces | Product name + logo + primary color on login, registration, forgot/reset password, magic-login, consent, device verification, logged-out and bootstrap screens; favicon set at boot. |
| Admin shell | Same. The shell picks the values up from the same `appConfig` Pinia store. |
| `document.title` | Product name as prefix. |
| Browser tab icon | `<link rel="icon" href="…">` is rewritten in JS at boot. |

## Per-Application override

A realm can host more than one Application (each with its own origin, login behaviour, and OAuth clients — see **Administration → Apps**). Each Application can optionally override all four branding fields on top of the realm-wide branding described above.

The override lives on the Application record itself: open **Administration → Apps**, pick an Application, and switch to the **Origin & Branding** tab. A "custom branding" checkbox turns the override on; leaving it off means the Application simply inherits the realm branding. Product name, primary colour, logo and favicon are independently nullable inside the override and fall back to the realm value.

The editor shows a live fixed-layout login and email preview. This preview is intentionally independent from the experimental PageBuilder: most tenants can complete their branding without replacing page structure.

For a realm with a single Application (the common case), the effective branding and the realm branding are identical, so this distinction doesn't come up.

## Asset-reference safety

The Branding sub-document stores `LogoAssetId` and `FaviconAssetId` — **not** URLs. That has two consequences:

- **Cross-domain risk**: zero. A realm admin can only reference assets uploaded into their own realm's asset library. Pasting an `https://evil.example.com/cookie-stealer.svg` into branding is not possible — the picker only shows local assets.
- **Delete-block**: the asset-library endpoint refuses to delete an asset that's referenced by realm or Application branding. You get an HTTP 409 with the exact referencing field; clear it first, then delete the asset.

## What's stored where

| Data | Location |
| --- | --- |
| Branding sub-document (the four fields) | tenant DB, inside the singleton `RealmSettings` document |
| Logo and favicon binary | tenant DB, `mt_doc_asset` (see [Asset Library](./assets)) |
| Default values served when a field is null | hardcoded in the SPA (`appconfig.store.ts`) |

## Public exposure

The `/api/app-info` endpoint is anonymous — branding is metadata, not secrets. It surfaces the same shape as the existing public realm settings and is required for the login page to render branded **before** the user authenticates. No tokens, no secrets, no cross-realm leakage (each realm's `RealmMiddleware`-resolved tenant scope only sees its own settings).
