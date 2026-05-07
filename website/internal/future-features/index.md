# Future Features

Capabilities we know we'll need to build but haven't prioritised.
Each page captures the design space — options, trade-offs, risks —
so that when a customer ask makes one of them urgent, we don't have
to re-derive the analysis from scratch.

## Pages

### [White-label customization](./white-label-customization)

Per-realm theming: logo, colors, brand copy, optional custom CSS.
**Standard ask** from every paying customer once they're past the
"does it work?" phase. Three escalation tiers (theme tokens →
custom copy → custom CSS), with a phased rollout plan that ships
the 80%-coverage version first.

**Status:** Design captured 2026-05-07. Not started.

### [Login alerts + manual IP blacklist](./login-alerts-ip-blacklist)

Brute-force visibility without NAT-aussperr-Risk: detect anomalous
failed-login volume, alert admin, let the human decide whether to
blacklist. The NAT-safe alternative to a naive IP rate-limit
(which we explicitly rejected — see
`feedback_no_ip_rate_limit_password_login.md` in claude memory).

**Status:** Idea captured 2026-05-07. Not started.

---

## Adding to this section

1. Create `internal/future-features/<feature-slug>.md`
2. Open with **Status** + **Why** lines (see existing pages for
   format)
3. Capture the design space, not the final design — options with
   pros/cons, risks, what would block, what could phase
4. Register in `.vitepress/config.internal.ts` so it shows up in
   the local sidebar
5. Link from this page

When the feature ships, **promote** the page: move it into the
appropriate public section (`/concepts/`, `/guide/`, `/admin/`,
or `/reference/`), update the sidebar registrations, delete the
internal entry. Don't leave stale "future" docs around once the
future has arrived.
