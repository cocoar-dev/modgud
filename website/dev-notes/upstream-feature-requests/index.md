# Upstream feature-requests

Tickets we want to open against external Cocoar libraries we depend on
— parked here as fully-written drafts so that when we have bandwidth
to file them upstream, the issue text is ready to paste.

Each page is structured to drop straight into a GitHub issue: Problem,
Proposed change, Rationale, References, Workaround.

## Pages

### [@cocoar/vue-ui — CoarSidebarItem: aria-label on the root menuitem](./vue-ui-sidebar-item-aria-label)

Collapsed sidebar items render with neither `title` nor `aria-label`
on the root `role="menuitem"` element — only a hover-tooltip via
directive. Screen readers see "menuitem" without context, and browser-
automation tooling that walks the accessibility tree (Playwright
`getByRole({ name })`, chrome-devtools-mcp) can't address them.

**Status:** Drafted 2026-05-11. Not filed.

### [@cocoar/vue-ui — CoarListbox / CoarDualListbox: cumulative-highlight mode](./vue-ui-listbox-cumulative-highlight)

The listbox uses standard OS-semantics (single-click replaces
highlight, Ctrl-click toggles). Web admins expect click-to-toggle.
Propose an opt-in `highlightMode: 'toggle' | 'replace'` prop —
non-breaking, default stays replace.

**Status:** Drafted 2026-05-11. Not filed. App-side workaround
shipped (`admin.dualListbox.multiSelectHint`).

---

## Convention

When a request is filed upstream:

1. Add the issue URL near the top of the page (`**Filed:** <link>`).
2. Leave the page here while the issue is open — it's the canonical
   "what we want" reference, often more detailed than the GitHub
   issue text.
3. When the request lands in a released version of the library,
   delete the page. Don't leave stale "still want" docs around once
   the want is satisfied.
