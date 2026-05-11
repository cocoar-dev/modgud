# @cocoar/vue-ui — CoarListbox / CoarDualListbox: cumulative-highlight mode

**Repo:** `@cocoar/vue-ui`
**Components:** `CoarListbox`, `CoarDualListbox`
**Reported by:** Cocoar.Auth onboarding (2026-05-11) — an integrator
filling the Scopes / Grants / Apps pickers on the OAuth-Client dialog
expected single-click to toggle, hit the "this behaves single-select"
trap, and didn't discover Ctrl/Shift-Click on their own.

## Problem

`CoarListbox` uses **OS-listbox highlight semantics**:

- Single click → replace the highlight set with this item.
- Ctrl/Cmd-Click → add/remove this item from the highlight set.
- Shift-Click → range-select from the anchor.

This is the correct behavior for a desktop listbox but a poor default
for **web-style admin forms** where users expect "click to toggle" — the
GitHub-issue-row pattern, the Notion-multi-select pattern, the
Linear-filter pattern. Users who don't know the keyboard shortcuts come
away thinking the listbox is broken.

Symptom on Cocoar.Auth's `ClientDetails.vue`:

> Multi-Select in den Listboxen (Scopes/Grants/Apps) verhält sich
> single-select — Mehrfaches Anklicken behält nur den letzten markierten
> Eintrag, Ctrl/Shift hat (ohne Anleitung) nicht funktioniert. Workaround:
> jeden Eintrag einzeln markieren + "Move to selected" klicken.

(Author's note: Ctrl-Click *does* work, but the integrator's environment
or muscle-memory bypassed it; the broader UX point — discoverability —
stands either way.)

## Proposed change

Add an opt-in prop that flips the click semantics from
"replace-on-click" to "toggle-on-click":

```ts
interface CoarListboxProps<T> {
  // …existing props…

  /**
   * How a plain click affects the highlight set.
   *
   * - `'replace'` (default): single-click replaces the highlight set
   *   with the clicked item; Ctrl-click adds/removes; Shift-click
   *   range-selects. Standard OS-listbox semantics.
   * - `'toggle'`: single-click flips this item's highlight state,
   *   leaving the rest of the set alone. Mimics GitHub /
   *   Linear / Notion multi-select rows. Ctrl/Shift still work for
   *   power users but are no longer required.
   */
  highlightMode?: 'replace' | 'toggle';
}
```

`CoarDualListbox` forwards the prop to both columns.

### Implementation sketch

In the click handler (single-click branch):

```ts
function onItemClick(item: ListboxOption, event: MouseEvent) {
  if (event.ctrlKey || event.metaKey) {
    toggle(item);        // existing behavior
    return;
  }
  if (event.shiftKey) {
    rangeSelect(item);   // existing behavior
    return;
  }
  if (props.highlightMode === 'toggle') {
    toggle(item);        // new: bare-click toggles
  } else {
    replace(item);       // existing default
  }
}
```

Keyboard behavior (Space / Enter on the focused item) should already
toggle — leave that path alone.

## Rationale

- **Discoverability without docs.** Web admins shouldn't have to read a
  hint to use a picker. Toggle-on-click is what every modern web UI
  does.
- **Opt-in, no breaking change.** Default stays `'replace'` so
  existing consumers don't shift behavior on an upgrade. New consumers
  pick the mode that fits their UX.
- **Composes with `dragDrop`.** Drag-and-drop already supports moving
  multiple highlighted items at once. With `highlightMode='toggle'`
  the user can quickly accumulate a multi-selection by clicking, then
  drag the whole batch — and that's the workflow the dual-listbox is
  optimised for.
- **Power-users untouched.** Ctrl/Shift still do what they always did,
  so anyone with keyboard muscle-memory keeps it.

## References

- GitHub issue-list multi-select (single-click toggle): live on
  `https://github.com/<org>/<repo>/issues`
- Linear: same single-click-toggle pattern in its filter dropdowns
- Notion database multi-select: same

## Workaround until fixed

Document the keyboard shortcuts in a one-line tab-hint per picker — done
in Cocoar.Auth's `ClientDetails.vue` (`admin.dualListbox.multiSelectHint`
localisation key, ~2026-05-11 commit). Effective but blunt: every page
using a picker has to repeat the hint, and the underlying confusion is
still there for first-time users.
