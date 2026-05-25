# Customization — Branding

Per-realm SPA-shell branding so every tenant can present its own product name, colour, logo, and favicon at the login page and across the admin UI — without the operator rebuilding the SPA bundle.

::: info Default is "no branding"
Every realm starts unbranded. The Cocoar defaults (product name "Modgud", primary color, logo, favicon) apply until at least one branding field is set. Partial branding is supported — set just the logo and leave the colour at default, etc.
:::

::: info Two ways to reach this surface
This page (**Administration → Customization → Branding**) and the **Branding** tab inside [Realm Settings](../admin/realm-settings#branding) edit the same `RealmSettings.Branding` sub-document. Both render the same form. Pick whichever fits your mental model.
:::

Permissions: `realm-settings:read` / `realm-settings:write`. The `realm:admin` bypass grants both.

## Fields

| Field | Default | Effect when set |
| --- | --- | --- |
| **Product name** | "Modgud" | Header title in the admin UI + login page; `document.title` prefix on every page. ≤ 100 characters. |
| **Primary color** | design-system blue | Drives the `--coar-color-primary` CSS variable. Accepts hex (`#rgb`, `#rrggbb`, `#rrggbbaa`), `rgb()` / `rgba()`, `hsl()` / `hsla()`, or a CSS named colour. **No** `calc()` / `var()` / arbitrary CSS — that's blocked at the API to prevent injection into the property value. |
| **Logo** | Cocoar logo (`/td-logo.svg`) | Header logo in the admin UI + login page. Pick from the [Asset Library](./assets) via the asset picker. |
| **Favicon** | `/td-logo.svg` | Browser-tab icon. Same asset picker; the SPA rewrites the `<link rel="icon">` element at boot. |

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
| `/api/app-info` (anonymous, public) | All four fields. The endpoint resolves `LogoAssetId` / `FaviconAssetId` to public URLs (`/api/assets/{shortGuid}`) — anonymous callers never see the raw asset id. |
| Login page (`/login`) | Product name + logo + primary color; favicon set at boot. |
| Admin shell | Same. The shell picks the values up from the same `appConfig` Pinia store. |
| `document.title` | Product name as prefix. |
| Browser tab icon | `<link rel="icon" href="…">` is rewritten in JS at boot. |

## Asset-reference safety

The Branding sub-document stores `LogoAssetId` and `FaviconAssetId` — **not** URLs. That has two consequences:

- **Cross-domain risk**: zero. A realm admin can only reference assets uploaded into their own realm's asset library. Pasting an `https://evil.example.com/cookie-stealer.svg` into branding is not possible — the picker only shows local assets.
- **Delete-block**: the asset-library endpoint refuses to delete an asset that's referenced by the realm's branding. You get an HTTP 409 with the referencing field name; clear the branding field first, then delete the asset.

## What's stored where

| Data | Location |
| --- | --- |
| Branding sub-document (the four fields) | tenant DB, inside the singleton `RealmSettings` document |
| Logo and favicon binary | tenant DB, `mt_doc_asset` (see [Asset Library](./assets)) |
| Default values served when a field is null | hardcoded in the SPA (`appconfig.store.ts`) |

## Public exposure

The `/api/app-info` endpoint is anonymous — branding is metadata, not secrets. It surfaces the same shape as the existing public realm settings and is required for the login page to render branded **before** the user authenticates. No tokens, no secrets, no cross-realm leakage (each realm's `RealmMiddleware`-resolved tenant scope only sees its own settings).
