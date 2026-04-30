# docs/ — internal developer notes

This folder is for **us** (the people building cocoar.auth), not for end users
or anyone reading the published docs. Plain markdown, kept in the repo, never
shipped in `wwwroot/`.

The published docs live elsewhere:

- `website/` — single VitePress source tree, two builds:
  - `pnpm build` (default config) → full public site (concepts, guide,
    reference, admin sections — everything)
  - `pnpm build:in-app` (config.in-app.ts) → reduced bundle of only
    `admin/` + `end-user/` pages, served inside the running app at
    `/docs/`. Same source, filtered via `srcExclude`, so admin docs stay
    version-synchronised with the deployed binary.

What lives here:

- [testing.md](testing.md) — what we test, what we don't, how to run it
- [backlog.md](backlog.md) — findings to revisit, tech-debt items, deferred
  features. Anything we know about but consciously left for later goes here so
  it doesn't get lost.

Add files freely as new tracking surfaces show up. Keep them short and
maintained — outdated internal notes are worse than no notes.
