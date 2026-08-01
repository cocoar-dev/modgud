import { markRaw, type Component } from 'vue'
import { useOverlay, useOverlayParent, modalPreset, type OverlaySpec } from '@cocoar/vue-ui'

/**
 * Open a `ModalLayout` component as a bare overlay — the exact plumbing the
 * routed fragments use (`type: 'modal'` in `route.meta.routedFragments`), for
 * the cases where a routed fragment is not available: a modal opened from
 * *inside* another modal.
 *
 * Deliberately NOT `useDialog()`. The CoarDialog shell brings its own header,
 * close button, padding and card background, so wrapping a `ModalLayout` in it
 * renders a second modal frame around the modal — a visible modal-in-modal.
 * The bare overlay contributes backdrop, positioning and sizing only; the
 * `ModalLayout` inside owns header, close button and footer, exactly like a
 * routed modal.
 *
 * The opened component receives its props plus the injected `close(result?)`
 * callback; `open()` resolves to whatever `close()` was called with, and to
 * `undefined` when the modal was dismissed (backdrop / Escape / the header's
 * close button).
 *
 * Sizes come from `@/router/modal-sizes` — the same named constants the route
 * table assigns to routed modals.
 */
export function useModalOverlay() {
  const overlay = useOverlay()
  // Stacks the child above the modal it was opened from, and stops that
  // parent from treating clicks inside the child as outside-clicks.
  const parent = useOverlayParent()

  function open<T = unknown>(
    component: Component,
    size: OverlaySpec['size'],
    props: Record<string, unknown> = {},
  ): Promise<T | undefined> {
    const ref$ = overlay.open({
      spec: { ...modalPreset, size },
      content: { kind: 'component', component: markRaw(component) },
      inputs: { ...props, close: (result?: T) => ref$.close(result) },
      parent,
    })
    return ref$.afterClosed as Promise<T | undefined>
  }

  return { open }
}
