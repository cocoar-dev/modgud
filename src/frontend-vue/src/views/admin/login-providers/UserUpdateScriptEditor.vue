<script setup lang="ts">
import { ref, watch } from 'vue'
import { CoarScriptEditor } from '@cocoar/vue-script-editor'
import { CoarButton, CoarTabGroup, CoarTab } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import type { TestUserUpdateResponse } from '@/models/loginProvider'

const { t } = useI18n()
const store = useLoginProviderStore()

const props = defineProps<{
  modelValue: string
  loginProviderId?: string
  isNew?: boolean
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', value: string): void
}>()

const script = ref(props.modelValue)
watch(() => props.modelValue, (v) => { script.value = v })
watch(script, (v) => emit('update:modelValue', v))

const sampleClaims = ref<string>(JSON.stringify({
  iss: 'https://login.microsoftonline.com/your-tenant/v2.0',
  sub: '00000000-0000-0000-0000-000000000001',
  email: 'alice@acme.com',
  preferred_username: 'alice',
  name: 'Alice Anderson',
  given_name: 'Alice',
  family_name: 'Anderson',
}, null, 2))

const activeTab = ref<'input' | 'output'>('input')
const result = ref<TestUserUpdateResponse | null>(null)
const testError = ref<string | null>(null)
const testing = ref(false)

// Script-editor type hints: the input is the raw-claims dictionary and the
// script must return a partial user-record. Return-shape is intentionally
// narrow — Firstname/Lastname/Email/Acronym — to make it obvious what the
// script can and cannot touch.
const extraLibs = [{
  content: `
interface RawClaims {
  [key: string]: string | string[] | undefined;
}
interface UserUpdate {
  /** Patched onto User.Firstname. undefined = skip, null = clear, '' = skip. */
  firstname?: string | null;
  /** Patched onto User.Lastname. */
  lastname?: string | null;
  /** Patched onto User.Email. An existing different user owning this email rejects the login. */
  email?: string | null;
  /** Patched onto User.Acronym. */
  acronym?: string | null;
}
declare const claims: RawClaims;
`,
  filePath: 'file:///types/user-update.d.ts',
}]

async function loadLast() {
  if (!props.loginProviderId || props.isNew) return
  try {
    const raw = await store.getLastRawClaims(props.loginProviderId)
    if (raw) sampleClaims.value = JSON.stringify(raw, null, 2)
    else testError.value = t('admin.loginProviders.noLastClaims', {}, 'Noch kein gespeichertes Login-Sample verfügbar.')
  } catch (e: any) {
    testError.value = e?.message ?? String(e)
  }
}

async function runTest() {
  testError.value = null
  result.value = null
  let parsed: Record<string, unknown>
  try { parsed = JSON.parse(sampleClaims.value) }
  catch (e: any) {
    testError.value = t('admin.loginProviders.invalidJson', {}, 'Ungültiges JSON: ') + (e?.message ?? String(e))
    return
  }

  testing.value = true
  try {
    if (props.isNew || !props.loginProviderId) {
      testError.value = t('admin.loginProviders.testAfterSave', {}, 'Speichere die Konfiguration zuerst, dann kannst du das Script testen.')
      return
    }
    const res = await store.testUserUpdate(props.loginProviderId, {
      Script: script.value,
      Claims: parsed,
    })
    result.value = res
    activeTab.value = 'output'
  } catch (e: any) {
    testError.value = e?.response?.data?.Message ?? e?.message ?? String(e)
  } finally {
    testing.value = false
  }
}
</script>

<template>
  <div class="editor-layout">
    <div class="editor-side">
      <div class="side-heading">
        {{ t('admin.loginProviders.userUpdateScript', {}, 'User-Update-Script') }}
      </div>
      <CoarScriptEditor
        v-model="script"
        :extra-libs="extraLibs"
        variant="inline"
        script-mode
        class="editor-body"
        placeholder="(claims) => ({ firstname: claims.given_name?.trim(), lastname: claims.family_name?.trim(), email: claims.email, acronym: (claims.given_name?.[0] ?? '') + (claims.family_name?.[0] ?? '') })"
      />
    </div>

    <div class="test-side">
      <div class="side-heading flex items-center justify-between">
        <span>{{ t('admin.loginProviders.testPanel', {}, 'Test') }}</span>
        <div class="flex gap-1">
          <CoarButton size="xs" variant="ghost" icon-start="download" :disabled="isNew" @click="loadLast">
            {{ t('admin.loginProviders.loadLastClaims', {}, 'Letzter Login') }}
          </CoarButton>
          <CoarButton size="xs" icon-start="play" :disabled="testing" @click="runTest">
            {{ t('admin.loginProviders.runTest', {}, 'Ausführen') }}
          </CoarButton>
        </div>
      </div>

      <CoarTabGroup v-model="activeTab" class="tabs-row">
        <CoarTab id="input">{{ t('admin.loginProviders.sampleInput', {}, 'Beispiel-Input') }}</CoarTab>
        <CoarTab id="output">{{ t('admin.loginProviders.output', {}, 'Ergebnis') }}</CoarTab>
      </CoarTabGroup>

      <div v-if="activeTab === 'input'" class="tab-body">
        <textarea
          v-model="sampleClaims"
          class="claims-textarea"
          spellcheck="false"
        />
      </div>

      <div v-else class="tab-body">
        <div v-if="testError" class="error-banner">{{ testError }}</div>
        <pre v-if="result" class="output-pre">{{ JSON.stringify(result, null, 2) }}</pre>
        <div v-else-if="!testError" class="text-sm text-gray-400 p-3">
          {{ t('admin.loginProviders.noResult', {}, 'Klick "Ausführen", um das berechnete Patch zu sehen.') }}
        </div>
      </div>

      <div v-if="testError && activeTab === 'input'" class="error-banner">{{ testError }}</div>
    </div>
  </div>
</template>

<style scoped>
.editor-layout {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
  height: 100%;
  min-height: 0;
}
.editor-side, .test-side {
  display: flex;
  flex-direction: column;
  min-height: 0;
}
.side-heading {
  font-size: 0.8rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: #525e76;
  padding-bottom: 6px;
  margin-bottom: 6px;
  border-bottom: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
}
.editor-body {
  flex: 1;
  min-height: 0;
}
.tabs-row { margin-bottom: 4px; }
.tab-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.claims-textarea {
  flex: 1;
  min-height: 0;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  line-height: 1.45;
  padding: 8px;
  border: 1px solid var(--coar-border-neutral, #e5e7eb);
  border-radius: 4px;
  background: var(--coar-background-neutral-secondary, #fafafa);
  resize: none;
}
.output-pre {
  flex: 1;
  overflow: auto;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  padding: 8px;
  background: var(--coar-background-neutral-secondary, #fafafa);
  border: 1px solid var(--coar-border-neutral, #e5e7eb);
  border-radius: 4px;
  margin: 0;
}
.error-banner {
  font-size: 0.85rem;
  color: #b91c1c;
  background: #fef2f2;
  border: 1px solid #fecaca;
  padding: 6px 8px;
  border-radius: 4px;
  margin-top: 6px;
}
</style>
