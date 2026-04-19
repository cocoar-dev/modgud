<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { CoarBadge } from '@cocoar/vue-ui'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

interface RoleDto {
  Id: string
  Name: string
  Description?: string | null
  DisplayName?: string | null
  Email?: string | null
  ClientId?: string | null
  Scopes: string[]
  CreatedAt?: string | null
  ModifiedAt?: string | null
}

const loading = ref(true)
const error = ref<string | null>(null)
const role = ref<RoleDto | null>(null)

const title = computed(() => role.value?.DisplayName || role.value?.Name || 'Role')
const subTitle = computed(() => role.value?.Name)

function fmtDate(v?: string | null): string {
  if (!v) return '—'
  try { return new Date(v).toLocaleString() } catch { return v }
}

onMounted(async () => {
  try {
    const http = useHttpClient('/api/admin/roles')
    role.value = await http.addPath(props.id).get<RoleDto>()
  } catch (e) {
    error.value = e instanceof HttpClientError
      ? `Failed to load role (HTTP ${e.status}).`
      : 'Failed to load role.'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <ModalLayout :close="close" :title="title" :sub-title="subTitle" icon="shield-check" width="36rem">
    <div v-if="loading" class="center">Loading...</div>
    <div v-else-if="error" class="error">{{ error }}</div>
    <div v-else-if="role" class="detail">
      <section>
        <div class="section-heading">General</div>
        <div class="field-grid">
          <div class="field"><div class="field-label">Name</div><div class="field-value">{{ role.Name }}</div></div>
          <div class="field"><div class="field-label">Display Name</div><div class="field-value">{{ role.DisplayName || '—' }}</div></div>
          <div class="field span-2"><div class="field-label">Description</div><div class="field-value">{{ role.Description || '—' }}</div></div>
          <div class="field"><div class="field-label">Email</div><div class="field-value">{{ role.Email || '—' }}</div></div>
          <div class="field"><div class="field-label">Client</div>
            <div class="field-value">
              <span v-if="role.ClientId"><code>{{ role.ClientId }}</code></span>
              <CoarBadge v-else variant="info" size="s">realm</CoarBadge>
            </div>
          </div>
        </div>
      </section>

      <section>
        <div class="section-heading">Timestamps</div>
        <div class="field-grid">
          <div class="field"><div class="field-label">Created</div><div class="field-value">{{ fmtDate(role.CreatedAt) }}</div></div>
          <div class="field"><div class="field-label">Modified</div><div class="field-value">{{ fmtDate(role.ModifiedAt) }}</div></div>
        </div>
      </section>

      <section v-if="role.Scopes && role.Scopes.length > 0">
        <div class="section-heading">Scopes ({{ role.Scopes.length }})</div>
        <div class="chip-row">
          <CoarBadge v-for="s in role.Scopes" :key="s" size="s" variant="info">{{ s }}</CoarBadge>
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

.field-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px 18px; }
.field { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.field.span-2 { grid-column: span 2; }

.field-label {
  font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.04em;
  color: var(--coar-text-neutral-secondary, #64748b); font-weight: 500;
}
.field-value {
  font-size: 0.875rem; color: var(--coar-text-neutral-primary, #0f172a);
  display: flex; align-items: center; gap: 6px;
}
.chip-row { display: flex; flex-wrap: wrap; gap: 6px; }
code { font-family: ui-monospace, SFMono-Regular, monospace; font-size: 0.8rem; }
</style>
