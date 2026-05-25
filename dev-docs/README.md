# `dev-docs/` — Repo-Only Dev Notes

This tree is **never published**. It's a parking lot for things that
need to live alongside the codebase but shouldn't appear on:

- the public docs site (`docs/` — built by `pnpm build` inside `docs/`),
- the in-app help bundle (same `docs/` build, packaged into the
  container by `docker/Dockerfile` stage 3).

Both of those reside in `docs/`; this `dev-docs/` site is a separate
VitePress instance. The only way to view this tree is locally:

```bash
cd dev-docs
pnpm install   # first time only
pnpm dev       # local server with sidebar + search + Mermaid
```

Or just read the `.md` files directly on GitHub — Mermaid is rendered
natively, links resolve relatively.

> **Why a separate VitePress site instead of a single tree with
> filters?** Previous attempts at `srcExclude: ['dev-notes/**']`
> inside a shared `website/` tree turned into a maintenance burden
> (three configs, a post-build strip script, link-rot when a public
> page accidentally referenced a dev-notes target). Physical
> separation makes the boundary unambiguous: cross-references to the
> public site must be explicit external URLs.

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
  tree (`docs/concepts/`, `docs/guide/`, `docs/admin/`,
  `docs/reference/`).
- Personal scratch notes — those belong in your own notebook, not
  the repo.

## Adding a new page

1. Drop the `.md` file under `dev-docs/<section>/`.
2. Register it in `dev-docs/.vitepress/config.ts` sidebar so it
   shows up in local nav.
3. Add a **Status** line at the top (`Status: Idea / Design /
   In progress / Done`).
4. If it's a future feature, link to the corresponding GitHub issue
   when one exists.
