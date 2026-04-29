<script setup lang="ts">
import { watch } from 'vue'
import { CoarIcon, CoarButton } from '@cocoar/vue-ui'
import { provideUI, type UIButton } from '@/composables/useUI'

const props = defineProps<{
  close: (result?: unknown) => void
  title?: string
  subTitle?: string
  icon?: string
  width?: string
  footerButton?: UIButton
}>()

const { state: ui } = provideUI()

// Sync props to UI state reactively
watch(
  () => [props.title, props.subTitle, props.icon, props.footerButton] as const,
  ([title, subTitle, icon, footerButton]) => {
    ui.header.title = title
    ui.header.subTitle = subTitle
    ui.header.icon = icon
    if (footerButton) {
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
  <div class="modal-container" :style="{ width: width ?? '40rem' }">
    <!-- Header -->
    <div v-if="ui.header.show" class="modal-header">
      <CoarIcon v-if="ui.header.icon" :name="ui.header.icon" size="l" class="modal-header-icon" />
      <div class="flex flex-col justify-center min-w-0 flex-1">
        <div class="modal-title" :class="{ 'modal-title--solo': !ui.header.subTitle }">
          {{ ui.header.title }}
        </div>
        <div v-if="ui.header.subTitle" class="modal-subtitle">
          {{ ui.header.subTitle }}
        </div>
      </div>
      <button class="modal-close" @click="close()">
        <CoarIcon name="x" size="m" />
      </button>
    </div>

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
  max-height: 90vh;
  max-width: 95vw;
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
