<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useI18n } from '@cocoar/vue-localization'
import { CoarCard, CoarButton, CoarIcon } from '@cocoar/vue-ui'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const gdprHttp = useHttpClient('/api/auth')

type State = 'confirming' | 'success' | 'error'
const state = ref<State>('confirming')
const errorMessage = ref('')

onMounted(async () => {
  const token = route.query.token as string | undefined
  if (!token) {
    state.value = 'error'
    errorMessage.value = t('confirmDeletion.missing', {}, 'Bestätigungs-Link unvollständig.')
    return
  }
  try {
    // Backend marks the deletion as confirmed; the actual masking + GDPR flow runs
    // out-of-band. Surface the success cleanly and let the user log out manually.
    await gdprHttp.addPath('confirm-deletion').post({ Token: token })
    state.value = 'success'
  } catch (e: unknown) {
    state.value = 'error'
    errorMessage.value = e instanceof HttpClientError && e.status === 400
      ? t('confirmDeletion.invalid', {}, 'Link ist ungültig oder abgelaufen.')
      : t('confirmDeletion.error', {}, 'Bestätigung fehlgeschlagen.')
  }
})
</script>

<template>
  <div class="flex flex-1 items-center justify-center p-4">
    <CoarCard elevated class="w-full max-w-md">
      <div class="p-8 text-center space-y-4">
        <template v-if="state === 'confirming'">
          <h1 class="text-xl font-semibold">{{ t('confirmDeletion.processing', {}, 'Wird verarbeitet…') }}</h1>
        </template>

        <template v-else-if="state === 'success'">
          <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-green-100">
            <CoarIcon name="check" class="text-green-600" size="l" />
          </div>
          <h1 class="text-xl font-semibold">{{ t('confirmDeletion.successTitle', {}, 'Löschung bestätigt') }}</h1>
          <p class="text-sm text-surface-600">
            {{ t('confirmDeletion.successBody', {}, 'Dein Konto wird in den nächsten Minuten gelöscht. Aus Audit-Gründen bleibt der Event-Stream maskiert erhalten.') }}
          </p>
          <CoarButton full-width @click="router.push('/profile')">
            {{ t('confirmDeletion.toProfile', {}, 'Zum Profil') }}
          </CoarButton>
        </template>

        <template v-else>
          <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-red-100">
            <CoarIcon name="x" class="text-red-600" size="l" />
          </div>
          <h1 class="text-xl font-semibold">{{ t('confirmDeletion.errorTitle', {}, 'Bestätigung fehlgeschlagen') }}</h1>
          <p class="text-sm text-surface-600">{{ errorMessage }}</p>
          <CoarButton full-width @click="router.push('/profile')">
            {{ t('confirmDeletion.toProfile', {}, 'Zum Profil') }}
          </CoarButton>
        </template>
      </div>
    </CoarCard>
  </div>
</template>
