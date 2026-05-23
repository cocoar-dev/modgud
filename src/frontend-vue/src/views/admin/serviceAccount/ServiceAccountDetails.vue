<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useServiceAccountStore } from '@/stores/serviceAccount.store'
import { CoarTextInput, CoarFormField, CoarCheckbox, CoarNote } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useServiceAccountStore()
const isCreate = computed(() => props.id === 'create')
const loading = ref(false)
const error = ref<string | null>(null)

const form = ref({
  AccountName: '',
  Purpose: '',
  IsActive: true,
})
const originalAccountName = ref('')
const originalIsActive = ref(true)

const modalTitle = computed(() => {
  return isCreate.value
    ? t('admin.serviceAccounts.createTitle', {}, 'Create service account')
    : (form.value.AccountName || t('admin.serviceAccounts.editTitle', {}, 'Service account'))
})

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.AccountName.trim() || loading.value,
  onClick: save,
}))

onMounted(async () => {
  if (!isCreate.value) {
    loading.value = true
    try {
      const sa = await store.getById(props.id)
      form.value = {
        AccountName: sa.AccountName,
        Purpose: sa.Purpose ?? '',
        IsActive: sa.IsActive,
      }
      originalAccountName.value = sa.AccountName
      originalIsActive.value = sa.IsActive
    } catch (e: any) {
      error.value = e?.data?.Message ?? e?.message ?? String(e)
    } finally {
      loading.value = false
    }
  }
})

async function save() {
  if (!form.value.AccountName.trim()) return
  loading.value = true
  error.value = null
  try {
    if (isCreate.value) {
      await store.createEntity({
        AccountName: form.value.AccountName.trim(),
        Purpose: form.value.Purpose.trim() || undefined,
      })
    } else {
      // Send only fields that actually changed. Treat empty string in Purpose
      // as explicit clear (server normalises blank to null).
      const body: Record<string, unknown> = {
        Purpose: form.value.Purpose.trim() === '' ? null : form.value.Purpose.trim(),
      }
      if (form.value.AccountName.trim() !== originalAccountName.value) {
        body.AccountName = form.value.AccountName.trim()
      }
      if (form.value.IsActive !== originalIsActive.value) {
        body.IsActive = form.value.IsActive
      }
      await store.httpClient.addPath(props.id).put(body)
    }
    props.close()
  } catch (e: any) {
    error.value = e?.data?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="cpu" :footer-button="footerButton" width="36rem">
    <div v-if="!loading || isCreate" class="flex flex-col gap-3 p-1">
      <CoarFormField :label="t('admin.serviceAccounts.accountName', {}, 'Account name')" required>
        <CoarTextInput v-model="form.AccountName" clearable :disabled="!isCreate"
          :placeholder="t('admin.serviceAccounts.accountNamePlaceholder', {}, 'ci.build-agent, integrations.timetodo, …')" />
      </CoarFormField>
      <p v-if="isCreate" class="text-xs text-surface-500 -mt-2">
        {{ t('admin.serviceAccounts.accountNameHint', {}, 'Lowercase letters, digits, dots, hyphens or underscores. Becomes the audit-log handle for this account.') }}
      </p>

      <CoarFormField :label="t('admin.serviceAccounts.purpose', {}, 'Purpose')">
        <CoarTextInput v-model="form.Purpose" clearable
          :placeholder="t('admin.serviceAccounts.purposePlaceholder', {}, 'CI deployment, nightly sync, …')" />
      </CoarFormField>

      <div v-if="!isCreate" class="mt-1">
        <CoarCheckbox v-model="form.IsActive" :label="t('admin.serviceAccounts.active', {}, 'Active')" />
      </div>

      <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>
    </div>
    <div v-else class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
  </ModalLayout>
</template>
