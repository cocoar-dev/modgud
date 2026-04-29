# docs/ — internal developer notes

This folder is for **us** (the people building cocoar.auth), not for end users
or anyone reading the published docs. Plain markdown, kept in the repo, never
shipped in `wwwroot/`.

The two public doc trees live elsewhere:

- `website/` — VitePress technical docs (concepts, guide, reference, slice docs)
- `userdocs/` — VitePress end-user documentation (German, du-form, served at
  `/docs/` after login)

What lives here:

- [testing.md](testing.md) — what we test, what we don't, how to run it
- [backlog.md](backlog.md) — findings to revisit, tech-debt items, deferred
  features. Anything we know about but consciously left for later goes here so
  it doesn't get lost.

Add files freely as new tracking surfaces show up. Keep them short and
maintained — outdated internal notes are worse than no notes.
