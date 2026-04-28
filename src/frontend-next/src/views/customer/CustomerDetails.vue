<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useCustomerStore } from '@/stores/customer.store'
import { CoarTextInput, CoarFormField, CoarCheckbox } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const customerStore = useCustomerStore()
const isCreate = computed(() => props.id === 'create')
const loading = ref(false)

const form = ref({
  Name: '',
  Important: false,
})

const modalTitle = computed(() => {
  const name = form.value.Name?.trim()
  if (name) return name
  return isCreate.value ? t('admin.customerDetails.createTitle', {}, 'Create Customer') : ''
})

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.Name.trim() || loading.value,
  onClick: save,
}))

onMounted(async () => {
  if (!isCreate.value) {
    loading.value = true
    try {
      const customer = await customerStore.getById(props.id)
      form.value = { Name: customer.Name, Important: customer.Important }
    } finally {
      loading.value = false
    }
  }
})

async function save() {
  if (!form.value.Name.trim()) return
  loading.value = true
  try {
    if (isCreate.value) {
      await customerStore.createEntity({ Name: form.value.Name, Important: form.value.Important })
    } else {
      await customerStore.updateEntity(props.id, { Name: form.value.Name, Important: form.value.Important } as any)
    }
    props.close()
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="building-2" :footer-button="footerButton">
    <form v-if="!loading" class="flex flex-col gap-2" @submit.prevent="save">
      <div class="flex items-end gap-4">
        <CoarFormField :label="t('admin.customerDetails.name', {}, 'Name')" class="flex-1">
          <CoarTextInput v-model="form.Name" clearable />
        </CoarFormField>
        <div class="flex items-center pb-2">
          <CoarCheckbox v-model="form.Important" :label="t('admin.customerDetails.important', {}, 'Important')" />
        </div>
      </div>
    </form>
    <div v-else class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
  </ModalLayout>
</template>
