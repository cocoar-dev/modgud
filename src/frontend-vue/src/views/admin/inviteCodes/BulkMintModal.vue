<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { CoarTextInput, CoarNumberInput, CoarFormField, CoarButton } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useInviteCodeStore } from '@/stores/inviteCode.store'
import { useApplicationsStore } from '@/stores/applications.store'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useInviteCodeStore()
const applicationsStore = useApplicationsStore()

const appId = computed(() => store.selectedAppId)
const appLabel = computed(() => {
  const a = applicationsStore.apps.find((x) => x.Id === appId.value)
  return a ? `${a.DisplayName} (${a.Slug})` : appId.value ?? ''
})

const count = ref<number>(1)
const boundEmail = ref<string>('')
const expiresInDays = ref<number>(14)

const loading = ref(false)
const error = ref<string | null>(null)
// Once minted, the plaintext codes are shown ONCE — the server never returns
// them again, only their hashes are stored.
const mintedCodes = ref<string[] | null>(null)
const copied = ref(false)

const isDone = computed(() => mintedCodes.value !== null)

const footerButton = computed(() => ({
  visible: true,
  text: isDone.value
    ? t('common.done', {}, 'Done')
    : t('admin.inviteCodes.mint', {}, 'Mint codes'),
  disabled: !appId.value || count.value < 1 || loading.value,
  loading: loading.value,
  onClick: isDone.value ? () => props.close() : mint,
}))

onMounted(() => {
  applicationsStore.initialize()
})

async function mint() {
  if (!appId.value || count.value < 1) return
  loading.value = true
  error.value = null
  try {
    const result = await store.mint(appId.value, {
      Count: Number(count.value),
      BoundEmail: boundEmail.value.trim() || null,
      ExpiresInDays: Number(expiresInDays.value) || null,
    })
    mintedCodes.value = result.Codes
  } catch (e: any) {
    error.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

async function copyAll() {
  if (!mintedCodes.value) return
  try {
    await navigator.clipboard.writeText(mintedCodes.value.join('\n'))
    copied.value = true
    setTimeout(() => (copied.value = false), 1500)
  } catch {
    /* clipboard blocked — the codes are still visible to copy manually */
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="t('admin.inviteCodes.mint.title', {}, 'Mint invite codes')"
    :sub-title="appLabel" icon="ticket" :footer-button="footerButton">
    <div class="flex flex-col min-w-0 min-h-0 flex-1">
      <div class="modal-form">
        <!-- Phase 1: the mint form -->
        <section v-if="!isDone" class="form-section">
          <div class="modal-form-grid">
            <CoarFormField class="col-half" :label="t('admin.inviteCodes.count', {}, 'How many')" required>
              <CoarNumberInput v-model="count" :min="1" :step="1" />
              <p class="field-hint">{{ t('admin.inviteCodes.count.hint', {}, 'Number of single-use codes to generate.') }}</p>
            </CoarFormField>
            <CoarFormField class="col-half" :label="t('admin.inviteCodes.expiresInDays', {}, 'Expires in (days)')">
              <CoarNumberInput v-model="expiresInDays" :min="1" :step="1" />
              <p class="field-hint">{{ t('admin.inviteCodes.expiresInDays.hint', {}, 'Code lifetime. Default 14 days.') }}</p>
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('admin.inviteCodes.bindEmail', {}, 'Bind to email (optional)')">
              <CoarTextInput v-model="boundEmail" clearable
                :placeholder="t('admin.inviteCodes.bindEmail.placeholder', {}, 'Leave blank for bearer codes')" />
              <p class="field-hint">{{ t('admin.inviteCodes.bindEmail.hint', {}, 'When set, a code only works for that exact recipient. Blank = bearer (anyone holding the code).') }}</p>
            </CoarFormField>
          </div>
          <p v-if="error" class="text-red-500 text-sm mt-2">{{ error }}</p>
        </section>

        <!-- Phase 2: the one-time plaintext codes -->
        <section v-else class="form-section">
          <p class="text-sm mb-3">
            {{ t('admin.inviteCodes.result.warning', {}, 'Copy these codes now — they are shown only once. Only their hashes are stored.') }}
          </p>
          <div class="rounded border border-gray-200 dark:border-gray-700 p-3 font-mono text-sm whitespace-pre-wrap break-all">{{ mintedCodes!.join('\n') }}</div>
          <div class="mt-3">
            <CoarButton size="s" icon-start="copy" @click="copyAll">
              {{ copied ? t('common.copied', {}, 'Copied!') : t('common.copyAll', {}, 'Copy all') }}
            </CoarButton>
          </div>
        </section>
      </div>
    </div>
  </ModalLayout>
</template>
