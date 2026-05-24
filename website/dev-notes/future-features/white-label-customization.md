# White-label customization (Phase 2 ideas)

> **Status:** Phase 1 shipped 2026-05-13. See [Branding](/plattform/branding),
> [Asset Library](/plattform/assets), [Pages (Beta)](/plattform/pages).

The Phase 1 surface covers the top customer asks: per-realm logo + favicon
(via Asset Library), product name + primary color, plus an opt-in
PageBuilder editor for login/logout/forgot-password.

Phase 2 ideas that are NOT built and remain design-only:

- **Footer links** — Privacy / ToS / Support per realm
- **Login background** — color, image, or gradient on the login screen
- **Email templates** — magic-link / password-reset email branding
- **Brand typography** — optional custom web font per realm
- **Custom CSS escape hatch** — power-user pixel-perfect overrides

None of these have customer-driven priority yet. Pick up here when a
specific request comes in — the per-realm `RealmSettings` Marten doc is
the natural extension point (add a typed section, expose via
`GET /api/realm/branding` augmented with the new fields, render in the
SPA shell).
