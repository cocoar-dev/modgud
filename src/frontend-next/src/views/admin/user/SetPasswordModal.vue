<script setup lang="ts">
import { ref, computed } from 'vue'
import { useUserStore } from '@/stores/user.store'
import { CoarPasswordInput, CoarFormField } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const userStore = useUserStore()
const password = ref('')
const saving = ref(false)

const user = computed(() =>
  userStore.entities.find(u => u.Id === props.id),
)

const modalTitle = computed(() => {
  if (!user.value) return t('admin.setPassword.title', {}, 'Set Password')
  const name = `${user.value.Firstname} ${user.value.Lastname}`.trim()
  return name ? t('admin.setPassword.titleWithName', { name }, 'Set Password - {name}') : t('admin.setPassword.title', {}, 'Set Password')
})

const footerButton = computed(() => ({
  visible: true,
  text: t('admin.setPassword.button', {}, 'Set Password'),
  disabled: !password.value.trim() || saving.value,
  loading: saving.value,
  onClick: savePassword,
}))

async function savePassword() {
  if (!password.value.trim()) return
  saving.value = true
  try {
    await userStore.setPassword(props.id, password.value)
    props.close()
  } catch (e) {
    console.error('Failed to set password', e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="key" :footer-button="footerButton" width="28rem">
    <div class="flex flex-col gap-2 p-2 pb-4">
      <CoarFormField :label="t('admin.setPassword.newPassword', {}, 'New Password')">
        <CoarPasswordInput v-model="password" autocomplete="new-password" />
      </CoarFormField>
    </div>
  </ModalLayout>
</template>
