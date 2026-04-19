import { markRaw, type Component } from 'vue'
import { useOverlay, modalPreset } from '@cocoar/vue-ui'

/**
 * Programmatic modal helper. Wraps `useOverlay()` with the `modalPreset` and
 * renders a component directly — without CoarDialogShell's own header.
 *
 * The component being opened is responsible for rendering its own chrome
 * (header, content, footer). It receives a `close(result?)` prop for
 * closing from inside.
 *
 * Returns a promise that resolves with the component's `close()` result.
 */
export function useModal() {
  const overlay = useOverlay()

  function open<T = unknown>(
    component: Component,
    props?: Record<string, unknown>,
    options?: {
      closeOnBackdropClick?: boolean
      closeOnEscape?: boolean
      size?: 's' | 'm' | 'l'
    },
  ): Promise<T | undefined> {
    const size = options?.size ?? 'm'
    const maxWidth = size === 's' ? '28rem' : size === 'l' ? '56rem' : '44rem'

    const ref = overlay.open({
      spec: {
        ...modalPreset,
        size: { maxWidth, maxHeight: '90vh' },
        dismiss: {
          outsideClick: options?.closeOnBackdropClick ?? false,
          escapeKey: options?.closeOnEscape ?? true,
        },
        backdrop: { kind: 'modal', closeOnBackdropClick: options?.closeOnBackdropClick ?? false },
        focus: { trap: true, restore: true },
        a11y: { role: 'dialog' },
      },
      content: { kind: 'component', component: markRaw(component) },
      inputs: {
        ...props,
        close: (result?: unknown) => ref.close(result),
      },
    })

    return ref.afterClosed as Promise<T | undefined>
  }

  return { open }
}
