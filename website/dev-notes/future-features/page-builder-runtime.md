# Page-builder runtime (deferred sprint)

The page-builder editor under `/plattform/pages` (see
[Customization — Pages](/plattform/pages)) persists schemas today but
the **runtime that renders those schemas on `/login` / `/logout` /
`/forgot-password` is not yet wired**. This page parks the design for
the rendering sprint so it doesn't get lost in the editor's user-doc.

## Scope

- `<CoarPageRenderer>` mounted on `/login`, `/logout`,
  `/forgot-password`. Per-slug resolver: stored schema → render;
  missing → hardcoded fallback view.
- Each auth step is its own slug (`login`, `mfa-totp`,
  `mfa-email-otp`, …). Action handlers decide the next step; the
  state machine stays in code, not in the schema.
- **Save-time validation**: the PUT endpoint gains a "schema must
  contain the slot's primary-action button + required-field inputs"
  check, so you can't save a structurally broken page that locks the
  realm out.
- **Safe-mode URL**: `?safemode=1` renders the hardcoded fallback
  even when a stored schema exists. Universal recovery for realm
  admins; not auth-gated (it's UX, not security).
- **Recovery CLI fallback**: `recover reset-page --realm <slug>
  --page <slug>` for the SaaS-operator backstop when even safe-mode
  can't help.

## Why this is deferred

The editor was shipped as the integration partner for
`@cocoar/vue-page-builder` so the library could harden against a real
consumer. The runtime needs more design work — at minimum the
state-machine wiring + the slot-allowlisted action ids + the
schema-validation rules — and it gates on the operator feature flag
staying off in production, so there is no externally observable
behaviour today that the runtime would break.

## Status

Editor: ✅ shipped, gated behind `AppSettings.Features.PageBuilder`.

Runtime: ⏳ deferred — not scheduled. Until the runtime lands, leave
the feature flag off in production.
