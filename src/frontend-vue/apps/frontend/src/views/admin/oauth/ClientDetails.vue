<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { CoarBadge } from '@cocoar/vue-ui'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

interface ClientDto {
  Id: string
  ClientId: string
  DisplayName?: string | null
  ClientType: string
  ConsentType: string
  RedirectUris: string[]
  PostLogoutRedirectUris: string[]
  Permissions: string[]
  AccessTokenType?: string | number
  CreatedAt?: string | null
  Enabled?: boolean
  RefreshTokenUsage?: string | number
  AllowAccessTokensViaBrowser?: boolean
  RequireClientSecret?: boolean
  EnableLocalLogin?: boolean
  RequireConsent?: boolean
  AllowRememberConsent?: boolean
  AllowedGrantTypes?: string[]
  AllowedCorsOrigins?: string[]
  IdentityTokenLifetime?: number | null
  AccessTokenLifetime?: number | null
  AuthorizationCodeLifetime?: number | null
  AbsoluteRefreshTokenLifetime?: number | null
  SlidingRefreshTokenLifetime?: number | null
  Roles?: string[]
}

const loading = ref(true)
const error = ref<string | null>(null)
const client = ref<ClientDto | null>(null)

const title = computed(() => client.value?.DisplayName || client.value?.ClientId || 'Client')
const subTitle = computed(() => client.value?.ClientId)

function fmtDate(v?: string | null): string {
  if (!v) return '—'
  try { return new Date(v).toLocaleString() } catch { return v }
}

onMounted(async () => {
  try {
    const http = useHttpClient('/api/admin/oauth/clients')
    client.value = await http.addPath(props.id).get<ClientDto>()
  } catch (e) {
    error.value = e instanceof HttpClientError
      ? `Failed to load client (HTTP ${e.status}).`
      : 'Failed to load client.'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <ModalLayout :close="close" :title="title" :sub-title="subTitle" icon="key-round" width="44rem">
    <div v-if="loading" class="center">Loading...</div>
    <div v-else-if="error" class="error">{{ error }}</div>
    <div v-else-if="client" class="detail">
      <section>
        <div class="section-heading">General</div>
        <div class="field-grid">
          <div class="field"><div class="field-label">Client ID</div><div class="field-value"><code>{{ client.ClientId }}</code></div></div>
          <div class="field"><div class="field-label">Display Name</div><div class="field-value">{{ client.DisplayName || '—' }}</div></div>
          <div class="field"><div class="field-label">Client Type</div><div class="field-value">{{ client.ClientType }}</div></div>
          <div class="field"><div class="field-label">Consent Type</div><div class="field-value">{{ client.ConsentType }}</div></div>
          <div class="field"><div class="field-label">Enabled</div>
            <div class="field-value">
              <CoarBadge :variant="client.Enabled === false ? 'neutral' : 'success'" size="s">
                {{ client.Enabled === false ? 'No' : 'Yes' }}
              </CoarBadge>
            </div>
          </div>
          <div class="field"><div class="field-label">Access Token Type</div><div class="field-value">{{ client.AccessTokenType ?? '—' }}</div></div>
          <div class="field"><div class="field-label">Created</div><div class="field-value">{{ fmtDate(client.CreatedAt) }}</div></div>
        </div>
      </section>

      <section>
        <div class="section-heading">Flags</div>
        <div class="chip-row">
          <CoarBadge v-if="client.RequireClientSecret" size="s" variant="info">RequireSecret</CoarBadge>
          <CoarBadge v-if="client.RequireConsent" size="s" variant="info">RequireConsent</CoarBadge>
          <CoarBadge v-if="client.AllowRememberConsent" size="s" variant="info">RememberConsent</CoarBadge>
          <CoarBadge v-if="client.EnableLocalLogin === false" size="s" variant="warning">NoLocalLogin</CoarBadge>
          <CoarBadge v-if="client.AllowAccessTokensViaBrowser" size="s" variant="warning">TokensInBrowser</CoarBadge>
        </div>
      </section>

      <section v-if="(client.RedirectUris || []).length > 0">
        <div class="section-heading">Redirect URIs</div>
        <ul class="uri-list"><li v-for="u in client.RedirectUris" :key="u"><code>{{ u }}</code></li></ul>
      </section>

      <section v-if="(client.PostLogoutRedirectUris || []).length > 0">
        <div class="section-heading">Post-Logout Redirect URIs</div>
        <ul class="uri-list"><li v-for="u in client.PostLogoutRedirectUris" :key="u"><code>{{ u }}</code></li></ul>
      </section>

      <section v-if="(client.AllowedGrantTypes || []).length > 0">
        <div class="section-heading">Allowed Grant Types</div>
        <div class="chip-row">
          <CoarBadge v-for="g in client.AllowedGrantTypes" :key="g" size="s" variant="neutral">{{ g }}</CoarBadge>
        </div>
      </section>

      <section v-if="(client.AllowedCorsOrigins || []).length > 0">
        <div class="section-heading">Allowed CORS Origins</div>
        <ul class="uri-list"><li v-for="o in client.AllowedCorsOrigins" :key="o"><code>{{ o }}</code></li></ul>
      </section>

      <section v-if="(client.Permissions || []).length > 0">
        <div class="section-heading">Permissions ({{ client.Permissions.length }})</div>
        <div class="chip-row">
          <CoarBadge v-for="p in client.Permissions" :key="p" size="s" variant="info">{{ p }}</CoarBadge>
        </div>
      </section>

      <section v-if="(client.Roles || []).length > 0">
        <div class="section-heading">Roles</div>
        <div class="chip-row">
          <CoarBadge v-for="r in client.Roles" :key="r" size="s" variant="info">{{ r }}</CoarBadge>
        </div>
      </section>

      <section>
        <div class="section-heading">Token Lifetimes (seconds)</div>
        <div class="field-grid">
          <div class="field"><div class="field-label">Identity</div><div class="field-value">{{ client.IdentityTokenLifetime ?? 'default' }}</div></div>
          <div class="field"><div class="field-label">Access</div><div class="field-value">{{ client.AccessTokenLifetime ?? 'default' }}</div></div>
          <div class="field"><div class="field-label">Auth Code</div><div class="field-value">{{ client.AuthorizationCodeLifetime ?? 'default' }}</div></div>
          <div class="field"><div class="field-label">Abs Refresh</div><div class="field-value">{{ client.AbsoluteRefreshTokenLifetime ?? 'default' }}</div></div>
          <div class="field"><div class="field-label">Sliding Refresh</div><div class="field-value">{{ client.SlidingRefreshTokenLifetime ?? 'default' }}</div></div>
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
.field-label {
  font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.04em;
  color: var(--coar-text-neutral-secondary, #64748b); font-weight: 500;
}
.field-value { font-size: 0.875rem; color: var(--coar-text-neutral-primary, #0f172a); }
.chip-row { display: flex; flex-wrap: wrap; gap: 6px; }
.uri-list { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 4px; }
code { font-family: ui-monospace, SFMono-Regular, monospace; font-size: 0.8rem; }
</style>
