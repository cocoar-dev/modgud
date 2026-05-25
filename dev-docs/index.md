# 🔒 Dev Notes

> **Repo-only.** This site lives in `dev-docs/` and never ships in
> any deployed artifact — not on the public docs site, not in the
> in-app help bundle. Visible only when running VitePress locally
> via `pnpm dev` inside `dev-docs/`, or reading the .md files on
> GitHub. See [README](./README) for the convention.

Parking lot for things that need to live alongside the codebase but
shouldn't appear on customer-facing docs:

- **Future features** we'll need to build eventually, captured before
  the design space goes stale.
- **Architecture-decision drafts** that aren't canonical yet.
- **Design discussions** useful to contributors but distracting for
  end-users.

## Sections

### [Future Features](./future-features/)

Capabilities we know we'll need to build but haven't prioritised:

- [White-label customization](./future-features/white-label-customization) —
  per-realm theming, logos, brand colors, custom copy. Standard
  customer ask after the first beta.
- [Login alerts + manual IP blacklist](./future-features/login-alerts-ip-blacklist) —
  NAT-safe brute-force detection: alert + admin decides, no auto-block.

### [Upstream feature-requests](./upstream-feature-requests/)

Drafted-but-not-filed issues against Cocoar libraries we depend on
(e.g. `@cocoar/vue-ui`). Each page is structured to drop straight
into a GitHub issue — Problem / Proposed change / Rationale /
Workaround. Currently parked: two `@cocoar/vue-ui` requests
surfaced during the first external onboarding.

---

When something here matures into a real plan, promote it: move the
file out of `dev-docs/` into the appropriate public section in
`docs/` (`/concepts/`, `/guide/`, `/admin/`, `/reference/`), update
the sidebar registration in `docs/.vitepress/config.ts`, and the next
public build picks it up.
