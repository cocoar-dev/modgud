<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { CoarBadge } from '@cocoar/vue-ui'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

interface ScopeDto {
  Id: string
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Resources: string[]
  Enabled?: boolean
  Required?: boolean
  Emphasize?: boolean
  ShowInDiscoveryDocument?: boolean
  UserClaims: string[]
}

const loading = ref(true)
const error = ref<string | null>(null)
const scope = ref<ScopeDto | null>(null)

const title = computed(() => scope.value?.DisplayName || scope.value?.Name || 'Scope')
const subTitle = computed(() => scope.value?.Name)

onMounted(async () => {
  try {
    const http = useHttpClient('/api/admin/oauth/scopes')
    scope.value = await http.addPath(props.id).get<ScopeDto>()
  } catch (e) {
    error.value = e instanceof HttpClientError
      ? `Failed to load scope (HTTP ${e.status}).`
      : 'Failed to load scope.'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <ModalLayout :close="close" :title="title" :sub-title="subTitle" icon="scan-line" width="36rem">
    <div v-if="loading" class="center">Loading...</div>
    <div v-else-if="error" class="error">{{ error }}</div>
    <div v-else-if="scope" class="detail">
      <section>
        <div class="section-heading">General</div>
        <div class="field-grid">
          <div class="field"><div class="field-label">Name</div><div class="field-value"><code>{{ scope.Name }}</code></div></div>
          <div class="field"><div class="field-label">Display Name</div><div class="field-value">{{ scope.DisplayName || '—' }}</div></div>
          <div class="field span-2"><div class="field-label">Description</div><div class="field-value">{{ scope.Description || '—' }}</div></div>
        </div>
      </section>

      <section>
        <div class="section-heading">Flags</div>
        <div class="chip-row">
          <CoarBadge :variant="scope.Enabled === false ? 'neutral' : 'success'" size="s">
            {{ scope.Enabled === false ? 'Disabled' : 'Enabled' }}
          </CoarBadge>
          <CoarBadge v-if="scope.Required" variant="info" size="s">Required</CoarBadge>
          <CoarBadge v-if="scope.Emphasize" variant="warning" size="s">Emphasize</CoarBadge>
          <CoarBadge v-if="scope.ShowInDiscoveryDocument === false" variant="neutral" size="s">Hidden from Discovery</CoarBadge>
        </div>
      </section>

      <section v-if="(scope.Resources || []).length > 0">
        <div class="section-heading">Resources</div>
        <div class="chip-row">
          <CoarBadge v-for="r in scope.Resources" :key="r" size="s" variant="info">{{ r }}</CoarBadge>
        </div>
      </section>

      <section v-if="(scope.UserClaims || []).length > 0">
        <div class="section-heading">User Claims</div>
        <div class="chip-row">
          <CoarBadge v-for="c in scope.UserClaims" :key="c" size="s" variant="neutral">{{ c }}</CoarBadge>
        </div>
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
.chip-row { display: flex; flex-wrap: wrap; gap: 6px; }
code { font-family: ui-monospace, SFMono-Regular, monospace; font-size: 0.8rem; }
</style>
