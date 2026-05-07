# `dev-notes/` — Repo-Only Dev Notes

This tree is **never published**. It's a parking lot for things that
need to live alongside the codebase but shouldn't appear on:

- the public docs site (`pnpm build`, `config.ts`),
- the in-app help bundle (`pnpm build:in-app`, `config.in-app.ts`).

Both of those configs `srcExclude: ['dev-notes/**']`. The only way to
view this tree is locally:

```bash
cd website
pnpm dev          # uses config.dev-notes.ts → dev-notes/ visible
pnpm dev:public   # uses config.ts          → dev-notes/ excluded (public preview)
```

> **Why the name?** Originally we used `internal/` but VitePress
> silently skipped that directory (likely some bundled-plugin's
> hardcoded ignore list). Renamed to `dev-notes/` after the issue
> surfaced; the semantic intent is identical.

## What goes here

- **Future-features planning** — capabilities we know we'll need to
  build but haven't prioritised; a place to record the design space
  before it goes stale.
- **Architecture-decision drafts** — half-formed ADRs that aren't
  ready to be canonical yet.
- **Design discussions** — back-and-forth notes that are useful for
  contributors but distracting for end-users.
- **Internal runbooks** — anything operations-y that we don't want
  in the customer-visible docs.

## What does NOT go here

- Sensitive data — secrets, customer info, private keys. Even though
  this isn't published, it IS in git history; treat it like any
  other source file.
- Things that are ready to be canonical — promote them to the public
  tree (`/concepts/`, `/guide/`, `/admin/`, `/reference/`).
- Personal scratch notes — those belong in your own notebook, not
  the repo.

## Adding a new page

1. Drop the `.md` file under `dev-notes/<section>/`.
2. Register it in `website/.vitepress/config.dev-notes.ts` under the
   `'/dev-notes/'` sidebar so it shows up in local nav.
3. Add a **Status** line at the top (`Status: Idea / Design /
   In progress / Done`).
4. If it's a future feature, link to the corresponding GitHub issue
   when one exists.
