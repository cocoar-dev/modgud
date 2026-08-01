// ── Named modal sizes (UI/UX wave 3) ────────────────────────────────────────
//
// A single named-size contract replaces the per-modal one-offs. Two height
// strategies, chosen by content:
//
//  • cap-to-content (height:auto + minHeight:auto + maxHeight) — the panel
//    sizes to its content and scrolls past the cap. No dead lower half. Use
//    for single-form modals. Proven by the old SERVICE_ACCOUNT size.
//  • stable frame (height==minHeight==maxHeight in vh) — a definite ancestor
//    height for tabbed / grid / editor modals whose flex:1 children
//    (CoarDualListbox, AG-Grid, Monaco, read-only JSON panes) collapse to 0
//    without one. Sized for the heaviest tab.
//
// Big (vw) sizes carry NO minWidth rem floor: a floor wins over the vw
// computation once the viewport is narrower than the floor, overflowing the
// viewport horizontally (tested 2026-05-15 — an 84rem floor cut off the close
// button at 1280px). vw + a maxWidth cap scales to any viewport. SM/MD keep a
// rem min==max because 32/42rem are always below a real admin viewport.
//
// Lives in its own module because two consumers need the same values: the
// route table (`overlayOptions.size` on routed fragments) and the handful of
// modals that are opened from *inside* another modal via `useModalOverlay()`,
// where there is no route to own the size.

// Cap-to-content single forms. (A 32rem MODAL_SM can be added when a modal of
// ≤4 short fields needs it — none do today.)
export const MODAL_MD = {
  width: '42rem', minWidth: '42rem', maxWidth: '42rem',
  height: 'auto', minHeight: 'auto', maxHeight: '85vh',
} as const

// Stable tall frames for tabbed / grid / editor modals.
export const MODAL_LG = {
  width: '78vw', maxWidth: '80rem',
  height: '82vh', minHeight: '82vh', maxHeight: '82vh',
} as const

export const MODAL_FULL = {
  width: '92vw', maxWidth: '112rem',
  height: '90vh', minHeight: '90vh', maxHeight: '90vh',
} as const

// A tabbed form whose tabs carry dual-listboxes. 51rem is `.modal-form`'s 48rem
// cap plus the modal-content padding, so the form fills the panel instead of
// leaving dead space to its right (LG's 78vw did). FIXED height like ROLE, for
// two reasons: the listboxes need a definite ancestor height to fill, and the
// panel must not resize when switching between a short form tab and a list tab.
// 38rem is sized to the list tabs (a 22rem listbox plus tab bar, note, chrome)
// — tall enough for them without stranding the short Basics tab in empty space.
export const MODAL_LIST_FORM = {
  width: '51rem', minWidth: '51rem', maxWidth: '51rem',
  height: '38rem', minHeight: '38rem', maxHeight: '85vh',
} as const
