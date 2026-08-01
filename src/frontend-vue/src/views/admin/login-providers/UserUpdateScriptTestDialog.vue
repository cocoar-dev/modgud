<script setup lang="ts">
import { ref, watch } from 'vue'
import { CoarButton, CoarNotice, CoarTab, CoarTabGroup } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import type { TestUserUpdateResponse } from '@/models/loginProvider'

const { t } = useI18n()
const store = useLoginProviderStore()

const props = defineProps<{
  script: string
  loginProviderId?: string
  isNew?: boolean
  sampleClaims: string
  onSampleClaimsChange?: (value: string) => void
  close: () => void
}>()

const activeTab = ref<'input' | 'output'>('input')
const claims = ref(props.sampleClaims)
const result = ref<TestUserUpdateResponse | null>(null)
const testError = ref<string | null>(null)
const testing = ref(false)
const loadingLast = ref(false)

watch(claims, (value) => props.onSampleClaimsChange?.(value))

async function loadLast() {
  if (!props.loginProviderId || props.isNew) return

  testError.value = null
  loadingLast.value = true
  try {
    const raw = await store.getLastRawClaims(props.loginProviderId)
    if (raw) {
      claims.value = JSON.stringify(raw, null, 2)
      activeTab.value = 'input'
    } else {
      testError.value = t('admin.loginProviders.noLastClaims', {}, 'No saved login sample available yet.')
      activeTab.value = 'output'
    }
  } catch (e: any) {
    testError.value = e?.message ?? String(e)
    activeTab.value = 'output'
  } finally {
    loadingLast.value = false
  }
}

async function runTest() {
  testError.value = null
  result.value = null

  let parsed: Record<string, unknown>
  try {
    parsed = JSON.parse(claims.value)
  } catch (e: any) {
    testError.value = t('admin.loginProviders.invalidJson', {}, 'Invalid JSON: ') + (e?.message ?? String(e))
    activeTab.value = 'output'
    return
  }

  testing.value = true
  try {
    if (props.isNew || !props.loginProviderId) {
      testError.value = t('admin.loginProviders.testAfterSave', {}, 'Save the configuration first, then you can test the script.')
      activeTab.value = 'output'
      return
    }

    result.value = await store.testUserUpdate(props.loginProviderId, {
      Script: props.script,
      Claims: parsed,
    })
    activeTab.value = 'output'
  } catch (e: any) {
    testError.value = e?.response?.data?.Message ?? e?.message ?? String(e)
    activeTab.value = 'output'
  } finally {
    testing.value = false
  }
}
</script>

<template>
  <div class="script-test-dialog">
    <div class="script-test-toolbar">
      <CoarTabGroup v-model="activeTab">
        <CoarTab id="input">
          {{ t('admin.loginProviders.sampleInput', {}, 'Beispiel-Input') }}
        </CoarTab>
        <CoarTab id="output">
          {{ t('admin.loginProviders.output', {}, 'Ergebnis') }}
        </CoarTab>
      </CoarTabGroup>

      <div class="script-test-actions">
        <CoarButton
          size="s"
          variant="ghost"
          icon-start="download"
          :disabled="isNew"
          :loading="loadingLast"
          @click="loadLast">
          {{ t('admin.loginProviders.loadLastClaims', {}, 'Letzter Login') }}
        </CoarButton>
        <CoarButton size="s" icon-start="play" :loading="testing" @click="runTest">
          {{ t('admin.loginProviders.runTest', {}, 'Ausführen') }}
        </CoarButton>
      </div>
    </div>

    <div v-show="activeTab === 'input'" class="script-test-content">
      <textarea
        v-model="claims"
        class="claims-textarea"
        spellcheck="false"
        :aria-label="t('admin.loginProviders.sampleInput', {}, 'Beispiel-Input')"
      />
    </div>

    <div v-show="activeTab === 'output'" class="script-test-content">
      <CoarNotice v-if="testError" variant="error">
        {{ testError }}
      </CoarNotice>
      <pre v-else-if="result" class="output-pre">{{ JSON.stringify(result, null, 2) }}</pre>
      <div v-else class="empty-result">
        {{ t('admin.loginProviders.noResult', {}, 'Click "Run" to see the computed patch.') }}
      </div>
    </div>

    <div class="script-test-footer">
      <CoarButton variant="secondary" @click="props.close()">
        {{ t('common.close', {}, 'Schließen') }}
      </CoarButton>
    </div>
  </div>
</template>

<style scoped>
.script-test-dialog {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  height: min(34rem, 68vh);
  min-height: 24rem;
}

.script-test-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  flex: 0 0 auto;
}

.script-test-actions {
  display: flex;
  align-items: center;
  gap: 0.375rem;
}

.script-test-content {
  display: flex;
  flex: 1;
  min-height: 0;
  flex-direction: column;
}

.claims-textarea,
.output-pre {
  box-sizing: border-box;
  width: 100%;
  height: 100%;
  min-height: 0;
  margin: 0;
  overflow: auto;
  border: 1px solid var(--coar-border-neutral, #d8dce5);
  border-radius: var(--coar-radius-s, 4px);
  background: var(--coar-background-neutral-secondary, #fafafa);
  color: var(--coar-text-neutral-primary, #1f2937);
  font-family: "Cascadia Code", Consolas, Monaco, monospace;
  font-size: 0.8rem;
  line-height: 1.5;
  padding: 0.75rem;
}

.claims-textarea {
  resize: none;
}

.claims-textarea:focus-visible {
  border-color: var(--coar-border-brand, #188dc5);
  outline: 2px solid color-mix(in srgb, var(--coar-border-brand, #188dc5) 22%, transparent);
  outline-offset: -1px;
}

.empty-result {
  color: var(--coar-text-neutral-secondary, #667085);
  padding: 0.75rem;
}

.script-test-footer {
  display: flex;
  flex: 0 0 auto;
  justify-content: flex-end;
}
</style>
