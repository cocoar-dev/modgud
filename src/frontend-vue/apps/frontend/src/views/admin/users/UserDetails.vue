<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { CoarBadge } from '@cocoar/vue-ui'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

interface UserDetailsDto {
  Id: string
  UserName: string
  Email?: string | null
  EmailConfirmed?: boolean
  PhoneNumber?: string | null
  PhoneNumberConfirmed?: boolean
  TwoFactorEnabled?: boolean
  LockoutEnd?: string | null
  LockoutEnabled?: boolean
  AccessFailedCount?: number
  FirstName?: string | null
  LastName?: string | null
  ExpiresAt?: string | null
  IsActive: boolean
  CreatedAt?: string | null
  ModifiedAt?: string | null
  Roles: string[]
  Claims: { Type: string; Value: string }[]
}

const loading = ref(true)
const error = ref<string | null>(null)
const user = ref<UserDetailsDto | null>(null)

const title = computed(() => {
  const u = user.value
  if (!u) return 'User'
  const name = [u.FirstName, u.LastName].filter(Boolean).join(' ')
  return name || u.UserName
})

const subTitle = computed(() => user.value?.UserName)

function fmtDate(v?: string | null): string {
  if (!v) return '—'
  try { return new Date(v).toLocaleString() } catch { return v }
}

onMounted(async () => {
  loading.value = true
  error.value = null
  try {
    const http = useHttpClient('/api/admin/users')
    user.value = await http.addPath(props.id).get<UserDetailsDto>()
  } catch (e) {
    error.value = e instanceof HttpClientError
      ? `Failed to load user (HTTP ${e.status}).`
      : 'Failed to load user.'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <ModalLayout :close="close" :title="title" :sub-title="subTitle" icon="user" width="36rem">
    <div v-if="loading" class="center">Loading...</div>
    <div v-else-if="error" class="error">{{ error }}</div>
    <div v-else-if="user" class="detail">
      <section>
        <div class="section-heading">Profile</div>
        <div class="field-grid">
          <div class="field"><div class="field-label">Username</div><div class="field-value">{{ user.UserName }}</div></div>
          <div class="field"><div class="field-label">Email</div>
            <div class="field-value">
              {{ user.Email || '—' }}
              <CoarBadge v-if="user.Email && user.EmailConfirmed" variant="success" size="s" class="ml-badge">verified</CoarBadge>
            </div>
          </div>
          <div class="field"><div class="field-label">First Name</div><div class="field-value">{{ user.FirstName || '—' }}</div></div>
          <div class="field"><div class="field-label">Last Name</div><div class="field-value">{{ user.LastName || '—' }}</div></div>
          <div class="field"><div class="field-label">Phone</div>
            <div class="field-value">
              {{ user.PhoneNumber || '—' }}
              <CoarBadge v-if="user.PhoneNumber && user.PhoneNumberConfirmed" variant="success" size="s" class="ml-badge">verified</CoarBadge>
            </div>
          </div>
          <div class="field"><div class="field-label">Status</div>
            <div class="field-value">
              <CoarBadge :variant="user.IsActive ? 'success' : 'neutral'" size="s">
                {{ user.IsActive ? 'Active' : 'Inactive' }}
              </CoarBadge>
              <CoarBadge v-if="user.TwoFactorEnabled" variant="info" size="s" class="ml-badge">2FA</CoarBadge>
              <CoarBadge v-if="user.LockoutEnd" variant="warning" size="s" class="ml-badge">locked</CoarBadge>
            </div>
          </div>
        </div>
      </section>

      <section>
        <div class="section-heading">Timestamps</div>
        <div class="field-grid">
          <div class="field"><div class="field-label">Created</div><div class="field-value">{{ fmtDate(user.CreatedAt) }}</div></div>
          <div class="field"><div class="field-label">Modified</div><div class="field-value">{{ fmtDate(user.ModifiedAt) }}</div></div>
          <div class="field"><div class="field-label">Expires</div><div class="field-value">{{ fmtDate(user.ExpiresAt) }}</div></div>
          <div class="field"><div class="field-label">Lockout End</div><div class="field-value">{{ fmtDate(user.LockoutEnd) }}</div></div>
        </div>
      </section>

      <section v-if="user.Roles && user.Roles.length > 0">
        <div class="section-heading">Roles ({{ user.Roles.length }})</div>
        <div class="chip-row">
          <CoarBadge v-for="r in user.Roles" :key="r" size="s" variant="info">{{ r }}</CoarBadge>
        </div>
      </section>

      <section v-if="user.Claims && user.Claims.length > 0">
        <div class="section-heading">Claims ({{ user.Claims.length }})</div>
        <table class="claims-table">
          <thead><tr><th>Type</th><th>Value</th></tr></thead>
          <tbody>
            <tr v-for="(c, i) in user.Claims" :key="i">
              <td><code>{{ c.Type }}</code></td>
              <td><code>{{ c.Value }}</code></td>
            </tr>
          </tbody>
        </table>
      </section>
    </div>
  </ModalLayout>
</template>

<style scoped>
.detail {
  display: flex;
  flex-direction: column;
  gap: 18px;
}
.center, .error {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  color: var(--coar-text-neutral-secondary, #64748b);
}
.error { color: var(--coar-text-semantic-error-bold, #b91c1c); }

.section-heading {
  font-size: 0.72rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--coar-text-neutral-secondary, #64748b);
  border-bottom: 1px solid var(--coar-border-neutral-secondary, #e2e8f0);
  padding-bottom: 4px;
  margin-bottom: 10px;
}

.field-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px 18px;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.field-label {
  font-size: 0.7rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--coar-text-neutral-secondary, #64748b);
  font-weight: 500;
}

.field-value {
  font-size: 0.875rem;
  color: var(--coar-text-neutral-primary, #0f172a);
  display: flex;
  align-items: center;
  gap: 6px;
}

.ml-badge {
  margin-left: 2px;
}

.chip-row {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.claims-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.8125rem;
}
.claims-table th, .claims-table td {
  text-align: left;
  padding: 4px 8px;
  border-bottom: 1px solid var(--coar-border-neutral-secondary, #e2e8f0);
  vertical-align: top;
}
.claims-table th {
  font-weight: 600;
  color: var(--coar-text-neutral-secondary, #64748b);
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.03em;
}
.claims-table code {
  font-family: ui-monospace, SFMono-Regular, monospace;
  font-size: 0.75rem;
}
</style>
