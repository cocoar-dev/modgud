<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { CoarBadge } from '@cocoar/vue-ui'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

interface ApiSecretEntry {
  SecretId: string
  Type: string
  Description?: string | null
  Expiration?: string | null
  CreatedAt: string
}

interface ApiDto {
  Id: string
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Enabled: boolean
  Scopes: string[]
  UserClaims: string[]
  Secrets: ApiSecretEntry[]
}

const loading = ref(true)
const error = ref<string | null>(null)
const api = ref<ApiDto | null>(null)

const title = computed(() => api.value?.DisplayName || api.value?.Name || 'API')
const subTitle = computed(() => api.value?.Name)

function fmtDate(v?: string | null): string {
  if (!v) return '—'
  try { return new Date(v).toLocaleString() } catch { return v }
}

onMounted(async () => {
  try {
    const http = useHttpClient('/api/admin/oauth/apis')
    api.value = await http.addPath(props.id).get<ApiDto>()
  } catch (e) {
    error.value = e instanceof HttpClientError
      ? `Failed to load API (HTTP ${e.status}).`
      : 'Failed to load API.'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <ModalLayout :close="close" :title="title" :sub-title="subTitle" icon="server" width="38rem">
    <div v-if="loading" class="center">Loading...</div>
    <div v-else-if="error" class="error">{{ error }}</div>
    <div v-else-if="api" class="detail">
      <section>
        <div class="section-heading">General</div>
        <div class="field-grid">
          <div class="field"><div class="field-label">Name</div><div class="field-value"><code>{{ api.Name }}</code></div></div>
          <div class="field"><div class="field-label">Display Name</div><div class="field-value">{{ api.DisplayName || '—' }}</div></div>
          <div class="field span-2"><div class="field-label">Description</div><div class="field-value">{{ api.Description || '—' }}</div></div>
          <div class="field"><div class="field-label">Enabled</div>
            <div class="field-value">
              <CoarBadge :variant="api.Enabled ? 'success' : 'neutral'" size="s">{{ api.Enabled ? 'Yes' : 'No' }}</CoarBadge>
            </div>
          </div>
        </div>
      </section>

      <section v-if="(api.Scopes || []).length > 0">
        <div class="section-heading">Scopes ({{ api.Scopes.length }})</div>
        <div class="chip-row">
          <CoarBadge v-for="s in api.Scopes" :key="s" size="s" variant="info">{{ s }}</CoarBadge>
        </div>
      </section>

      <section v-if="(api.UserClaims || []).length > 0">
        <div class="section-heading">User Claims</div>
        <div class="chip-row">
          <CoarBadge v-for="c in api.UserClaims" :key="c" size="s" variant="neutral">{{ c }}</CoarBadge>
        </div>
      </section>

      <section v-if="(api.Secrets || []).length > 0">
        <div class="section-heading">Secrets ({{ api.Secrets.length }})</div>
        <table class="secrets-table">
          <thead>
            <tr><th>Id</th><th>Type</th><th>Description</th><th>Created</th><th>Expires</th></tr>
          </thead>
          <tbody>
            <tr v-for="s in api.Secrets" :key="s.SecretId">
              <td><code>{{ s.SecretId }}</code></td>
              <td>{{ s.Type }}</td>
              <td>{{ s.Description || '—' }}</td>
              <td>{{ fmtDate(s.CreatedAt) }}</td>
              <td>{{ fmtDate(s.Expiration) }}</td>
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
.chip-row { display: flex; flex-wrap: wrap; gap: 6px; }
.secrets-table { width: 100%; border-collapse: collapse; font-size: 0.8125rem; }
.secrets-table th, .secrets-table td { text-align: left; padding: 4px 8px; border-bottom: 1px solid var(--coar-border-neutral-secondary, #e2e8f0); }
.secrets-table th { font-weight: 600; color: var(--coar-text-neutral-secondary, #64748b); font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.03em; }
code { font-family: ui-monospace, SFMono-Regular, monospace; font-size: 0.8rem; }
</style>
