<script setup lang="ts">
import { ref, computed } from 'vue'
import { useI18n } from '@cocoar/vue-localization'
import { CoarButton, CoarIcon } from '@cocoar/vue-ui'
import ModalLayout from '@/components/ModalLayout.vue'
import { useAuthStore } from '@/stores/auth.store'

const { t } = useI18n()

const props = defineProps<{
  close: () => void
}>()

const authStore = useAuthStore()
const submitting = ref(false)

const idpName = computed(() => authStore.user?.IdpDisplayName ?? 'IdP')

async function logoutLocalOnly() {
  if (submitting.value) return
  submitting.value = true
  await authStore.logout(false)
}

async function logoutEverywhere() {
  if (submitting.value) return
  submitting.value = true
  await authStore.logout(true)
}
</script>

<template>
  <ModalLayout
    :close="() => close()"
    :title="t('logout.title', {}, 'Sign out')"
    icon="log-out"
    width="32rem"
  >
    <div class="space-y-4">
      <p class="text-sm">
        {{ t('logout.federatedPrompt', {}, 'You signed in via') }}
        <strong>{{ idpName }}</strong>.
        {{ t('logout.federatedQuestion', {}, 'Do you want to end your session everywhere, or only in this app?') }}
      </p>

      <div class="flex flex-col gap-3 pt-2">
        <button
          type="button"
          class="logout-choice"
          :disabled="submitting"
          @click="logoutEverywhere"
        >
          <CoarIcon name="log-out" size="m" class="logout-choice-icon" />
          <div class="flex flex-col text-left">
            <span class="font-semibold">
              {{ t('logout.everywhere', {}, 'Sign out everywhere') }}
            </span>
            <span class="text-xs opacity-70">
              {{ t('logout.everywhereHint', {}, 'Also end your session at') }} {{ idpName }}
            </span>
          </div>
        </button>

        <button
          type="button"
          class="logout-choice"
          :disabled="submitting"
          @click="logoutLocalOnly"
        >
          <CoarIcon name="door-open" size="m" class="logout-choice-icon" />
          <div class="flex flex-col text-left">
            <span class="font-semibold">
              {{ t('logout.localOnly', {}, 'Only from TimeToDo') }}
            </span>
            <span class="text-xs opacity-70">
              {{ t('logout.localOnlyHint', {}, 'Your {idp} session stays active', { idp: idpName }) }}
            </span>
          </div>
        </button>
      </div>

      <div class="flex justify-end pt-2">
        <CoarButton variant="ghost" size="s" :disabled="submitting" @click="close()">
          {{ t('common.cancel', {}, 'Cancel') }}
        </CoarButton>
      </div>
    </div>
  </ModalLayout>
</template>

<style scoped>
.logout-choice {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 16px;
  border-radius: var(--coar-radius-m, 4px);
  border: 1px solid var(--coar-border-neutral-subtle, #e3e3e3);
  background: var(--coar-background-neutral-primary, #fff);
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
  width: 100%;
  text-align: left;
}

.logout-choice:hover:not(:disabled) {
  background: var(--coar-background-neutral-secondary, #f7f7f7);
  border-color: var(--coar-border-neutral-bold, #c8c8c8);
}

.logout-choice:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.logout-choice-icon {
  flex-shrink: 0;
  color: var(--coar-text-neutral-secondary, #525e76);
}
</style>
