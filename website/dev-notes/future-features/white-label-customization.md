# White-label customization

> **Status:** Design captured 2026-05-07. Not started.
> **Why:** Standard customer ask once past the "does it work?" phase
> — every paying customer wants their logo, colors, and brand copy
> on the login page they hand to their users. We won't have first
> paying customers ready to wait two weeks while we build it.

## The customer's actual asks (in observed-frequency order)

1. **Logo** — replace the cocoar.auth mark with theirs
2. **Color palette** — primary/accent colors that match their brand
3. **Brand name** — "Sign in to **Acme Cloud**" instead of generic
4. **Footer links** — Privacy / ToS / Support pointing at their pages
5. **Background** — color, image, or gradient on the login screen
6. **Email templates** — magic-link / password-reset emails branded
7. **Typography** — sometimes a brand font (rare, fiddly)
8. **Custom CSS** — power users wanting pixel-perfect match

## Design space — three escalation tiers

Most customer asks are covered by Tier 1. Tier 2 covers another 10%.
Tier 3 is power-user / enterprise-only.

### Tier 1 — Theme tokens + asset slots

**What customers can change:** colors, logo, favicon, brand name,
optional background image, optional legal-link footer.

**How it works:**

- New per-realm `Theme` document (Marten JSONB — flexible, no
  schema migrations when we add fields)
- Endpoint `GET /api/realm/theme` (anonymous, scoped to current
  Host's realm) returns the JSON
- SPA bootstrap (`auth.store` or `app.config.store`) fetches once
  on app init, applies CSS custom properties at runtime:
  ```ts
  document.documentElement.style.setProperty('--coar-primary', theme.primaryColor)
  ```
- `@cocoar/vue-ui` is **already** built on `--coar-*` CSS custom
  properties — the theme just has to set them, no component-library
  re-style needed
- Admin UI: Realm-Settings tab "Branding" with vue-ui's color
  picker + file upload

**Schema sketch:**

```csharp
public record RealmTheme(
    string? PrimaryColor,        // HEX, validated
    string? AccentColor,
    string? BackgroundColor,
    string? LogoAssetId,         // → /api/realm/asset/{id} served by IdP
    string? FaviconAssetId,
    string? LoginBackgroundAssetId,
    string? BrandName,           // for "Sign in to {BrandName}"
    LegalLinks? LegalLinks);     // privacy, terms, support URLs
```

**Effort:** ~3-5 days for full impl including admin UI + live
preview.

**Coverage of customer asks:** 80% (asks 1–5).

**Pros:** Sauber, sicher (zero XSS risk), admin-UI editable, no
external dependencies.

**Cons:** Limited to predefined hooks — customer wants to move the
"Forgot password" link? Not possible without a code change.

### Tier 2 — Custom copy / strings

**What customers can change:** Translation overrides per realm —
"Sign in to Acme" instead of "Sign in", custom welcome text on
the login page, custom error messages.

**How it works:**

- `@cocoar/vue-localization` is already in the stack
- Per-realm optional override-bundle stored in DB
- Resolution order: realm-override → system default
- Admin UI: tab "Texts" listing the overridable keys with a
  textarea per key

**Effort:** ~2-3 days, assumes Tier 1 already shipped.

**Coverage:** Adds another ~10% — the customers who care about
brand voice, not just brand colors.

**Pros:** Same risk profile as Tier 1 (text content is safe).

**Cons:** Translation bundles can grow unwieldy if every key is
overridable; need to decide which keys are exposed and which stay
system-locked.

### Tier 3 — Custom CSS

**What customers can change:** Visual details beyond what theme
tokens cover — repositioning, fine-grained spacing, custom
typography hooks.

**Three flavours, very different risk profiles:**

| Variant | XSS risk | Customer power |
|---|---|---|
| **a) Allowlist of CSS properties** (only color/font/spacing custom-properties via a JSON form) | **Low** | Medium |
| **b) Free custom CSS injected as `<style>`** | **High** — `background: url('//exfil')` is data exfiltration via CSS | High |
| **c) Customer-hosted stylesheet via `<link>`** | **Medium-High** — hostile customer-CDN compromise becomes our XSS | High |

**Recommendation:** Variant (a) only. Customer fills a structured
form (or JSON), backend serializes to `:root { --x: y; }`. Never
let raw CSS from a customer reach the rendered HTML.

**Effort:** ~1 week for variant (a) including the form UI.

**Coverage:** Last 10% — power-user / enterprise scenarios.

**Pros:** Pixel-perfect match becomes possible without exposing the
IdP to CSS-injection attacks.

**Cons:** Building the form UI is fiddly; some customers will still
want raw CSS and feel limited. Push back on that — variant (b) is
not worth the security drag.

## Phased rollout plan

| Phase | Ships | When |
|---|---|---|
| **1** | Tier 1 — theme tokens + asset slots + admin UI | Before first paying customer |
| **1.5** | Email-template branding | Same release if time, otherwise next |
| **2** | Tier 2 — custom copy | When first customer asks |
| **3** | Tier 3a — allowlist custom CSS | If/when an enterprise customer demands it |

Phase 1 is the minimum-acceptable shipped state. Customer comes
in, we click through the admin UI, they see their logo/colors live
in 15 minutes. That's the experience we're targeting.

## Risks / things-to-not-forget

1. **Image-content validation on uploads** — not just MIME-type
   checks. SVG with embedded `<script>` is a real attack vector;
   every second IdP forgets this. Magic-bytes verification + SVG
   sanitiser (DOMPurify with `USE_PROFILES: { svg: true }`).
2. **Self-host assets** — never link to a customer-hosted CDN. Their
   CDN going down or getting compromised becomes our problem;
   CSP `img-src` would have to be loosened (defeats the strict
   default we just shipped).
3. **Cache-busting** — asset URLs need a version hash
   (`logo-abc123.png`), otherwise updates don't show up in browsers.
4. **Default fallback safety** — if theme fetch fails (bad JSON,
   missing asset, network blip): render the cocoar.auth default,
   not a white page or a broken layout.
5. **Admin-side preview** — non-negotiable. Customers will not
   accept "upload, deploy to staging, hope for the best". Live
   preview in the admin UI before saving.
6. **Email templates need server-side rendering** — Razor /
   SmartFormat / Liquid. Different mechanism from the SPA theme;
   plan as a separate slice with its own preview.
7. **Theme is per-realm, not per-deployment** — the system realm's
   theme is the IdP-wide fallback, and tenant realms override.
   Make sure the tenant-resolution chain is clear.
8. **Don't let theme touch security-critical UI surfaces** —
   buttons that say "Allow this app" in OAuth consent must not be
   hidable / restyle-able beyond color, or a malicious tenant could
   trick their own users into consenting to things they didn't
   notice.

## Open questions to settle when we start

- Asset storage: Marten attachments? File system? Object storage?
  → Probably a Marten "asset" document with the binary in a
  `bytea` column for simplicity, until customer count makes that
  a problem.
- Asset size limits: 1 MB for logo, 5 MB for background?
- Theme inheritance: tenant realm falls back to system realm's
  theme for unset fields, or starts from a hardcoded default?
- Live-preview implementation: iframe with `?preview-theme=`
  query param, or in-page React-style render? iframe is safer
  (full isolation) but heavier.
- Do customers ever need *dark-mode* customisation? `@cocoar/vue-ui`
  already supports light/dark — the theme would need both
  variants per token.

## What this is NOT

- This is **not** a multi-tenant whitelabel-build pipeline (separate
  Docker image per customer). That defeats the point of multi-tenant.
  Every customer's branding is data-driven at runtime.
- This is **not** "let customers run their own CSS frameworks". We
  control the framework; they pick the brand-tokens within it.
