import { type ComputedRef, type InjectionKey, computed, inject } from 'vue'

/**
 * Provided by <see cref="ModalLayout"/> when the modal is rendered in
 * read-only mode (e.g. for system-managed entities). Slot content
 * <c>inject()</c>s this to drive its own input <c>:disabled</c> binding
 * and to skip rendering edit-only affordances (Add buttons, Delete
 * buttons, etc.) without prop-drilling.
 *
 * <para>Wrapped in a <see cref="ComputedRef"/> so the consumer's
 * disabled-binding stays reactive — flipping the prop on
 * <see cref="ModalLayout"/> at runtime updates downstream controls
 * automatically.</para>
 */
export const MODAL_READONLY_KEY: InjectionKey<ComputedRef<boolean>> = Symbol('modal-readonly')

/**
 * Returns a reactive read-only flag for the nearest <see cref="ModalLayout"/>
 * ancestor. When called outside of a modal (or when the modal isn't in
 * read-only mode), returns a <c>false</c>-valued ComputedRef so the
 * caller can use it unconditionally.
 *
 * <example>
 * <code>
 * const readOnly = useModalReadOnly()
 * &lt;CoarTextInput :disabled="readOnly" v-model="form.x" /&gt;
 * &lt;CoarButton v-if="!readOnly" @click="addRow"&gt;Add&lt;/CoarButton&gt;
 * </code>
 * </example>
 */
export function useModalReadOnly(): ComputedRef<boolean> {
  const injected = inject(MODAL_READONLY_KEY, null)
  if (injected) return injected
  return computed(() => false)
}
