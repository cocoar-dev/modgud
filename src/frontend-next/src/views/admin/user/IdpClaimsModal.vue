<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { CoarTabGroup, CoarTab } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { type ExternalLinkDto } from '@/models/externalLink'

const { t } = useI18n()

const props = defineProps<{
  id: string                       // userId (ShortGuid) — route param
  close: (result?: unknown) => void
}>()

const activeTab = ref<'before' | 'after'>('before')
const loading = ref(false)
const links = ref<ExternalLinkDto[]>([])
const userName = ref('')
const selectedLinkId = ref<string>('')

const selectedLink = computed(() =>
  links.value.find(l => l.Id === selectedLinkId.value) ?? links.value[0])

const anyRawStored = computed(() => links.value.some(l => l.LastRawClaims != null))

function prettyJson(value: unknown): string {
  if (value === null || value === undefined) return ''
  try {
    return JSON.stringify(value, null, 2)
  } catch {
    return String(value)
  }
}

onMounted(async () => {
  loading.value = true
  try {
    const userRes = await fetch(`/api/user/${props.id}`)
    if (userRes.ok) {
      const u = await userRes.json()
      userName.value = [u.Firstname, u.Lastname].filter(Boolean).join(' ') || u.UserName || ''
    }

    const linksHttp = useHttpClient(`/api/admin/users/${props.id}/external-links`)
    links.value = await linksHttp.get<ExternalLinkDto[]>()
    if (links.value.length > 0) selectedLinkId.value = links.value[0].Id
  } catch (e) {
    console.error('Failed to load external links', e)
  } finally {
    loading.value = false
  }
})

const modalTitle = computed(() => userName.value || t('admin.idpClaims.loadingUser', {}, 'Loading...'))
</script>

<template>
  <ModalLayout
    :close="close"
    :title="modalTitle"
    icon="key-round"
    width="min(1100px, 95vw)"
  >
    <div v-if="loading" class="flex items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>

    <div v-else-if="links.length === 0" class="flex items-center justify-center p-8 text-gray-400">
      {{ t('admin.idpClaims.noLinks', {}, 'No IdP logins yet for this user.') }}
    </div>

    <div v-else class="flex flex-col flex-1 min-h-0 gap-3">
      <!-- Per-link meta summary + selector if >1 links -->
      <div class="flex flex-wrap gap-2">
        <button
          v-for="l in links"
          :key="l.Id"
          :class="['link-badge', { 'link-badge-selected': l.Id === selectedLink?.Id }]"
          @click="selectedLinkId = l.Id"
        >
          <span class="badge-name">{{ l.IdpDisplayName }}</span>
          <span class="badge-meta">
            {{ t('admin.idpClaims.lastLogin', {}, 'Last login') }}: {{ new Date(l.LastLoginAt).toLocaleString() }}
            <template v-if="l.LastCapturedAt"> · {{ t('admin.idpClaims.captured', {}, 'captured') }} {{ new Date(l.LastCapturedAt).toLocaleString() }}</template>
          </span>
          <span v-if="!l.LastScriptSucceeded" class="badge-error">
            {{ t('admin.idpClaims.scriptFailed', {}, 'Script failed') }}
          </span>
        </button>
      </div>

      <CoarTabGroup v-model="activeTab">
        <CoarTab id="before">{{ t('admin.idpClaims.tabBefore', {}, 'Before script (raw)') }}</CoarTab>
        <CoarTab id="after">{{ t('admin.idpClaims.tabAfter', {}, 'After script (output)') }}</CoarTab>
      </CoarTabGroup>

      <!-- Before: raw claims JSON -->
      <div v-show="activeTab === 'before'" class="flex flex-col flex-1 min-h-0">
        <p class="text-xs text-gray-500 mb-2">
          {{ t('admin.idpClaims.beforeHint', {}, 'Every claim the identity provider sent over the wire. Only populated when the IdP config has "Store raw claims" enabled — otherwise this view is empty.') }}
        </p>
        <div v-if="!anyRawStored" class="text-xs text-amber-700 bg-amber-50 border border-amber-200 rounded px-3 py-2 mb-2">
          {{ t('admin.idpClaims.rawDisabled', {}, 'Raw claim storage is disabled for at least one of the linked IdP configs. Enable "Store raw claims" on the IdP config to capture them on the next login.') }}
        </div>
        <pre v-if="selectedLink?.LastRawClaims" class="json-view">{{ prettyJson(selectedLink.LastRawClaims) }}</pre>
        <div v-else class="text-xs text-gray-400 italic p-3">
          {{ t('admin.idpClaims.noRaw', {}, 'No raw claims stored for this login.') }}
        </div>
      </div>

      <!-- After: user-update-script output JSON -->
      <div v-show="activeTab === 'after'" class="flex flex-col flex-1 min-h-0">
        <p class="text-xs text-gray-500 mb-2">
          {{ t('admin.idpClaims.afterHint', {}, 'The object the user-update script returned. TimeToDo uses this to patch the user record (firstname, lastname, email, acronym). Other keys are kept only for debugging.') }}
        </p>
        <div v-if="selectedLink?.LastScriptError"
          class="text-xs text-red-700 bg-red-50 border border-red-200 rounded px-3 py-2 mb-2">
          <strong>{{ t('admin.idpClaims.scriptError', {}, 'Script error') }}:</strong>
          <code class="ml-1">{{ selectedLink.LastScriptError }}</code>
        </div>
        <pre v-if="selectedLink?.LastScriptOutput" class="json-view">{{ prettyJson(selectedLink.LastScriptOutput) }}</pre>
        <div v-else class="text-xs text-gray-400 italic p-3">
          {{ t('admin.idpClaims.noOutput', {}, 'No script output recorded for this login.') }}
        </div>
      </div>
    </div>
  </ModalLayout>
</template>

<style scoped>
.link-badge {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  background: var(--coar-background-neutral-secondary, #f3f4f6);
  border: 1px solid transparent;
  border-radius: 6px;
  font-size: 0.8rem;
  cursor: pointer;
}
.link-badge:hover {
  background: var(--coar-background-neutral, #e5e7eb);
}
.link-badge-selected {
  border-color: var(--coar-accent, #2563eb);
  background: var(--coar-background-accent-subtle, #eff6ff);
}
.badge-name {
  font-weight: 600;
}
.badge-meta {
  color: #6b7280;
  font-size: 0.75rem;
}
.badge-error {
  color: #b91c1c;
  background: #fef2f2;
  padding: 1px 6px;
  border-radius: 3px;
  font-size: 0.7rem;
}
.json-view {
  flex: 1;
  min-height: 0;
  overflow: auto;
  background: var(--coar-background-code, #0f172a);
  color: #e2e8f0;
  padding: 12px;
  border-radius: 6px;
  font-family: ui-monospace, SFMono-Regular, monospace;
  font-size: 0.78rem;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
