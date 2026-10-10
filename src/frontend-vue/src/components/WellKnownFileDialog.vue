<script setup lang="ts">
// Shows a file an app has to publish on its own domain (e.g. /.well-known/webauthn),
// readable and copyable. Opened via useDialog().open(); `close` is injected.
import { ref } from 'vue'
import { CoarButton } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

const props = defineProps<{
  url: string
  description: string
  content: string
  /** Shown below the file — e.g. what the generated content cannot know. */
  note?: string
  close: () => void
}>()

const { t } = useI18n()
const copied = ref(false)

async function copy() {
  await navigator.clipboard.writeText(props.content)
  copied.value = true
  setTimeout(() => { copied.value = false }, 2000)
}
</script>

<template>
  <div class="well-known">
    <p class="text-sm">{{ description }}</p>
    <code class="well-known__url">{{ url }}</code>
    <pre class="well-known__content">{{ content }}</pre>
    <p v-if="note" class="well-known__note">{{ note }}</p>
    <div class="well-known__actions">
      <CoarButton variant="secondary" @click="copy">
        {{ copied ? t('common.copied', {}, 'Copied') : t('common.copy', {}, 'Copy') }}
      </CoarButton>
      <CoarButton @click="close()">{{ t('common.close', {}, 'Close') }}</CoarButton>
    </div>
  </div>
</template>

<style scoped>
.well-known { display: flex; flex-direction: column; gap: 12px; }
.well-known__url { font-size: 0.8125rem; word-break: break-all; }
.well-known__content {
  margin: 0;
  padding: 12px;
  overflow-x: auto;
  border-radius: 6px;
  background: var(--coar-background-neutral-secondary);
  font-size: 0.8125rem;
  line-height: 1.5;
}
.well-known__note { margin: 0; font-size: 0.8125rem; color: var(--coar-text-neutral-secondary, #6b7280); }
.well-known__actions { display: flex; justify-content: flex-end; gap: 8px; }
</style>
