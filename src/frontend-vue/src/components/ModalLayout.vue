<script setup lang="ts">
import { computed, provide, watch } from 'vue'
import { CoarIcon, CoarButton, CoarTag } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { provideUI, type UIButton } from '@/composables/useUI'
import { MODAL_READONLY_KEY } from '@/composables/useModalReadOnly'

const props = defineProps<{
  close: (result?: unknown) => void
  title?: string
  subTitle?: string
  icon?: string
  /**
   * @deprecated Modal size is owned by the route's `overlayOptions.size`
   * (see router/index.ts). This prop is ignored — kept on the type
   * surface only so existing call-sites keep compiling during the
   * migration. Remove call-site uses and then drop this prop.
   */
  width?: string
  footerButton?: UIButton
  /**
   * Render the modal in read-only mode: hides the primary save/footer
   * button and surfaces a "Nur Lesen" badge in the title bar. The slot
   * content can call useModalReadOnly() to drive its own input
   * disabled-state without prop-drilling.
   */
  readonly?: boolean
}>()
// Suppress "props.width is unused" — kept for back-compat per the JSDoc.
void props

const { t } = useI18n()
const isReadOnly = computed(() => props.readonly === true)
provide(MODAL_READONLY_KEY, isReadOnly)

const { state: ui } = provideUI()

// Sync props to UI state reactively. Read-only mode hides the footer
// even if a footerButton is supplied — the slot content can keep its
// existing onSave wiring without having to know about read-only.
watch(
  () => [props.title, props.subTitle, props.icon, props.footerButton, props.readonly] as const,
  ([title, subTitle, icon, footerButton, readonly]) => {
    ui.header.title = title
    ui.header.subTitle = subTitle
    ui.header.icon = icon
    if (footerButton && !readonly) {
      ui.footer.show = true
      Object.assign(ui.footer.button1, footerButton)
    } else {
      ui.footer.show = false
    }
  },
  { immediate: true },
)
</script>

<template>
  <div class="modal-container">
    <!-- Header -->
    <div v-if="ui.header.show" class="modal-header">
      <CoarIcon v-if="ui.header.icon" :name="ui.header.icon" size="l" class="modal-header-icon" />
      <div class="flex flex-col justify-center min-w-0 flex-1">
        <div class="modal-title" :class="{ 'modal-title--solo': !ui.header.subTitle }">
          {{ ui.header.title }}
          <CoarTag v-if="isReadOnly" size="s" variant="warning" class="readonly-badge">
            {{ t('common.readOnly', {}, 'Nur Lesen') }}
          </CoarTag>
        </div>
        <div v-if="ui.header.subTitle" class="modal-subtitle">
          {{ ui.header.subTitle }}
        </div>
      </div>

      <!--
        Header-actions slot — rendered between title and close button.
        Use for context selectors that drive what the modal is editing
        (e.g. the Flavor picker on the Login-Provider edit modal). Per-
        instance scoped, so nested modals each get their own slot.
      -->
      <div v-if="$slots['header-actions']" class="modal-header-actions">
        <slot name="header-actions" />
      </div>

      <button
        class="modal-close"
        type="button"
        :aria-label="t('common.closeModal', {}, 'Close dialog')"
        :title="t('common.closeModal', {}, 'Close dialog')"
        @click="close()"
      >
        <CoarIcon name="x" size="m" />
      </button>
    </div>

    <!--
      Banner — a statement about the WHOLE modal (see AppBanner). Rendered
      between the header and the content on purpose: inside .modal-content it
      would inherit the 20px padding (so it would no longer be full-bleed) and
      it would scroll out of view, which is wrong for something that describes
      the entire surface. At most one per modal.
    -->
    <slot name="banner" />

    <!-- Content -->
    <div class="modal-content">
      <slot />
    </div>

    <!-- Footer -->
    <div v-if="ui.footer.show" class="modal-footer">
      <div class="flex-1"></div>
      <div class="flex items-center gap-1">
        <CoarButton
          v-if="ui.footer.button3.visible"
          variant="ghost"
          size="s"
          :disabled="ui.footer.button3.disabled"
          :loading="ui.footer.button3.loading"
          @click="ui.footer.button3.onClick?.()"
        >
          {{ ui.footer.button3.text }}
        </CoarButton>
        <CoarButton
          v-if="ui.footer.button2.visible"
          variant="secondary"
          size="s"
          :disabled="ui.footer.button2.disabled"
          :loading="ui.footer.button2.loading"
          @click="ui.footer.button2.onClick?.()"
        >
          {{ ui.footer.button2.text }}
        </CoarButton>
        <CoarButton
          v-if="ui.footer.button1.visible"
          variant="primary"
          size="s"
          :disabled="ui.footer.button1.disabled"
          :loading="ui.footer.button1.loading"
          @click="ui.footer.button1.onClick?.()"
        >
          {{ ui.footer.button1.text }}
        </CoarButton>
      </div>
    </div>
  </div>
</template>

<style scoped>
.modal-container {
  display: flex;
  flex-direction: column;
  /* Modal size is owned by the route's overlayOptions.size — see
     router/index.ts. The container fills the panel completely so
     viewport-aware sizing (vw/vh + min/max constraints) on the panel
     translates directly to a same-size modal-container. */
  width: 100%;
  height: 100%;
  min-width: 0;
  min-height: 0;
  border-radius: var(--coar-radius-m, 4px);
  overflow: hidden;
  background: var(--coar-background-neutral-secondary, #f7f7f7);
  box-shadow: 0 24px 48px -12px rgba(0, 0, 0, 0.18), 0 0 0 1px rgba(0, 0, 0, 0.05);
}

.modal-header {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 64px;
  max-height: 64px;
  padding: 0 20px;
  background: var(--color-header);
  color: white;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.08);
  z-index: 1;
}

.modal-header-icon {
  color: white;
  opacity: 0.8;
  flex-shrink: 0;
}

.modal-header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
  margin-left: 8px;
}

.modal-title {
  font-size: 1.125rem;
  font-weight: 600;
  color: white;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.modal-title--solo {
  font-size: 1.25rem;
}

.readonly-badge {
  margin-left: 8px;
  vertical-align: middle;
}

.modal-subtitle {
  font-size: 0.8125rem;
  color: rgba(255, 255, 255, 0.7);
}

.modal-close {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: var(--coar-radius-m, 4px);
  border: none;
  background: none;
  color: rgba(255, 255, 255, 0.5);
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.modal-close:hover {
  background: rgba(255, 255, 255, 0.15);
  color: white;
}

.modal-content {
  flex: 1;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  padding: 16px 20px 20px;
}

.modal-footer {
  display: flex;
  align-items: center;
  padding: 12px 20px;
  background: var(--coar-background-neutral-secondary, #f7f7f7);
  border-top: 1px solid #e9e9e9;
}
</style>
