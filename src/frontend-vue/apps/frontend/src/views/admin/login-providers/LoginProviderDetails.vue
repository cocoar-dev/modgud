<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { CoarBadge } from '@cocoar/vue-ui'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

interface ProviderDto {
  Id: string
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Type: string
  Configuration: Record<string, string>
  IsBuiltIn: boolean
}

const loading = ref(true)
const error = ref<string | null>(null)
const provider = ref<ProviderDto | null>(null)

const title = computed(() => provider.value?.DisplayName || provider.value?.Name || 'Login Provider')
const subTitle = computed(() => provider.value?.Name)

const configEntries = computed(() => {
  const cfg = provider.value?.Configuration ?? {}
  return Object.keys(cfg).sort().map(k => ({ key: k, value: cfg[k] }))
})

onMounted(async () => {
  try {
    const http = useHttpClient('/api/admin/login-providers')
    provider.value = await http.addPath(props.id).get<ProviderDto>()
  } catch (e) {
    error.value = e instanceof HttpClientError
      ? `Failed to load login provider (HTTP ${e.status}).`
      : 'Failed to load login provider.'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <ModalLayout :close="close" :title="title" :sub-title="subTitle" icon="lock" width="38rem">
    <div v-if="loading" class="center">Loading...</div>
    <div v-else-if="error" class="error">{{ error }}</div>
    <div v-else-if="provider" class="detail">
      <section>
        <div class="section-heading">General</div>
        <div class="field-grid">
          <div class="field"><div class="field-label">Name</div><div class="field-value"><code>{{ provider.Name }}</code></div></div>
          <div class="field"><div class="field-label">Display Name</div><div class="field-value">{{ provider.DisplayName || '—' }}</div></div>
          <div class="field"><div class="field-label">Type</div><div class="field-value">{{ provider.Type }}</div></div>
          <div class="field"><div class="field-label">Built-in</div>
            <div class="field-value">
              <CoarBadge :variant="provider.IsBuiltIn ? 'info' : 'neutral'" size="s">
                {{ provider.IsBuiltIn ? 'Yes' : 'No' }}
              </CoarBadge>
            </div>
          </div>
          <div class="field span-2"><div class="field-label">Description</div><div class="field-value">{{ provider.Description || '—' }}</div></div>
        </div>
      </section>

      <section v-if="configEntries.length > 0">
        <div class="section-heading">Configuration</div>
        <table class="cfg-table">
          <thead><tr><th>Key</th><th>Value</th></tr></thead>
          <tbody>
            <tr v-for="e in configEntries" :key="e.key">
              <td><code>{{ e.key }}</code></td>
              <td><code>{{ e.value }}</code></td>
            </tr>
          </tbody>
        </table>
      </section>
    </div>
  </ModalLayout>
</template>

<style scoped>
.detail { display: flex; flex-direction: column; gap: 18px; }
.center, .error { flex: 1; display: flex; align-items: center; justify-content: center; padding: 24px; color: var(--coar-text-neutral-secondary, #64748b); }
.error { color: var(--coar-text-semantic-error-bold, #b91c1c); }

.section-heading {
  font-size: 0.72rem; font-weight: 600; text-transform: uppercase; letter-spacing: 0.05em;
  color: var(--coar-text-neutral-secondary, #64748b);
  border-bottom: 1px solid var(--coar-border-neutral-secondary, #e2e8f0);
  padding-bottom: 4px; margin-bottom: 10px;
}

.field-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px 18px; }
.field { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.field.span-2 { grid-column: span 2; }
.field-label {
  font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.04em;
  color: var(--coar-text-neutral-secondary, #64748b); font-weight: 500;
}
.field-value { font-size: 0.875rem; color: var(--coar-text-neutral-primary, #0f172a); }
.cfg-table { width: 100%; border-collapse: collapse; font-size: 0.8125rem; }
.cfg-table th, .cfg-table td { text-align: left; padding: 4px 8px; border-bottom: 1px solid var(--coar-border-neutral-secondary, #e2e8f0); }
.cfg-table th { font-weight: 600; color: var(--coar-text-neutral-secondary, #64748b); font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.03em; }
code { font-family: ui-monospace, SFMono-Regular, monospace; font-size: 0.8rem; word-break: break-all; }
</style>
