<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import { CoarSpinner, CoarTag } from '@cocoar/vue-ui'
import { useHttpClient } from '@/composables/useHttpClient'
import { useAuthStore } from '@/stores/auth.store'
import WidgetCard from './WidgetCard.vue'

const { t } = useI18n()
const router = useRouter()
const authStore = useAuthStore()

// Has2FA on the signed-in user covers TOTP / e-mail / passkey-as-second-factor;
// the checklist shows a registered passkey as its own, passwordless capability.
const passkeyCount = ref<number | null>(null)
onMounted(async () => {
  try {
    passkeyCount.value = (await useHttpClient('/api/account/passkey').get<unknown[]>()).length
  } catch {
    passkeyCount.value = 0
  }
})

const items = computed(() => {
  const ok = t('dashboard.security.statusOk', {}, 'OK')
  const missing = t('dashboard.security.statusMissing', {}, 'missing')
  const row = (key: string, label: string, done: boolean, pending = false) => ({
    key,
    label,
    pending,
    strong: !done && !pending,
    variant: done ? 'success' as const : 'warning' as const,
    tag: done ? ok : missing,
  })
  return [
    row('email', t('dashboard.security.emailLabel', {}, 'E-mail address on file'), !!authStore.user?.Email),
    row('mfa', t('dashboard.security.mfaLabel', {}, 'Two-factor authentication'), authStore.user?.Has2FA === true),
    row('passkey', t('dashboard.security.passkeyLabel', {}, 'Passkey registered'),
      (passkeyCount.value ?? 0) > 0, passkeyCount.value === null),
  ]
})
</script>

<template>
  <WidgetCard
    :title="t('dashboard.security.title', {}, 'Account security')"
    icon="shield-check"
    :cta="t('dashboard.security.cta', {}, 'To profile →')"
    @open="router.push('/profile')"
  >
    <button
      v-for="item in items" :key="item.key"
      type="button" class="widget-row" :class="{ 'widget-row--strong': item.strong }"
      @click="router.push('/profile')"
    >
      <span class="widget-row__label">{{ item.label }}</span>
      <span class="widget-row__meta">
        <CoarSpinner v-if="item.pending" size="s" />
        <CoarTag v-else :variant="item.variant" size="s">{{ item.tag }}</CoarTag>
      </span>
    </button>
  </WidgetCard>
</template>
