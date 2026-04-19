<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { CoarBadge } from '@cocoar/vue-ui'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

const props = defineProps<{
  slug: string
  close: (result?: unknown) => void
}>()

interface RealmDto {
  Id: string
  Slug: string
  DisplayName: string
  Description?: string | null
  Domains: string[]
  CanManageTenants: boolean
  IsActive: boolean
  NeedsSetup: boolean
  CreatedAt?: string | null
}

const loading = ref(true)
const error = ref<string | null>(null)
const realm = ref<RealmDto | null>(null)

const title = computed(() => realm.value?.DisplayName || realm.value?.Slug || 'Realm')
const subTitle = computed(() => realm.value?.Slug)

function fmtDate(v?: string | null): string {
  if (!v) return '—'
  try { return new Date(v).toLocaleString() } catch { return v }
}

onMounted(async () => {
  try {
    const http = useHttpClient('/api/admin/realms')
    realm.value = await http.addPath(props.slug).get<RealmDto>()
  } catch (e) {
    error.value = e instanceof HttpClientError
      ? `Failed to load realm (HTTP ${e.status}).`
      : 'Failed to load realm.'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <ModalLayout :close="close" :title="title" :sub-title="subTitle" icon="globe" width="36rem">
    <div v-if="loading" class="center">Loading...</div>
    <div v-else-if="error" class="error">{{ error }}</div>
    <div v-else-if="realm" class="detail">
      <section>
        <div class="section-heading">General</div>
        <div class="field-grid">
          <div class="field"><div class="field-label">Slug</div><div class="field-value"><code>{{ realm.Slug }}</code></div></div>
          <div class="field"><div class="field-label">Display Name</div><div class="field-value">{{ realm.DisplayName }}</div></div>
          <div class="field span-2"><div class="field-label">Description</div><div class="field-value">{{ realm.Description || '—' }}</div></div>
          <div class="field"><div class="field-label">ID</div><div class="field-value"><code>{{ realm.Id }}</code></div></div>
          <div class="field"><div class="field-label">Created</div><div class="field-value">{{ fmtDate(realm.CreatedAt) }}</div></div>
        </div>
      </section>

      <section>
        <div class="section-heading">Status</div>
        <div class="chip-row">
          <CoarBadge :variant="realm.IsActive ? 'success' : 'neutral'" size="s">
            {{ realm.IsActive ? 'Active' : 'Inactive' }}
          </CoarBadge>
          <CoarBadge v-if="realm.NeedsSetup" variant="warning" size="s">Needs Setup</CoarBadge>
          <CoarBadge v-if="realm.CanManageTenants" variant="info" size="s">Can Manage Tenants</CoarBadge>
        </div>
      </section>

      <section v-if="(realm.Domains || []).length > 0">
        <div class="section-heading">Domains</div>
        <ul class="dom-list"><li v-for="d in realm.Domains" :key="d"><code>{{ d }}</code></li></ul>
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
.dom-list { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 4px; }
code { font-family: ui-monospace, SFMono-Regular, monospace; font-size: 0.8rem; }
</style>
