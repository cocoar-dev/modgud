<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useI18n } from '@cocoar/vue-localization'
import { CoarCard, CoarButton, CoarIcon } from '@cocoar/vue-ui'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const profileHttp = useHttpClient('/api/account/profile')

type State = 'verifying' | 'success' | 'error'
const state = ref<State>('verifying')
const errorMessage = ref('')

onMounted(async () => {
  const id = route.query.id as string | undefined
  const token = route.query.token as string | undefined
  if (!id || !token) {
    state.value = 'error'
    errorMessage.value = t('verifyEmail.missing', {}, 'Invalid confirmation link.')
    return
  }
  try {
    await profileHttp.addPath('request', 'verify-email').post({ RequestId: id, Token: token })
    state.value = 'success'
  } catch (e: unknown) {
    state.value = 'error'
    errorMessage.value = e instanceof HttpClientError && e.status === 400
      ? t('verifyEmail.invalid', {}, 'Link is invalid or expired.')
      : t('verifyEmail.error', {}, 'Confirmation failed.')
  }
})
</script>

<template>
  <div class="flex min-h-screen items-center justify-center p-4">
    <CoarCard elevated class="w-full max-w-md">
      <div class="p-8 text-center space-y-4">
        <template v-if="state === 'verifying'">
          <h1 class="text-xl font-semibold">{{ t('verifyEmail.verifying', {}, 'Confirming email…') }}</h1>
        </template>

        <template v-else-if="state === 'success'">
          <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-green-100">
            <CoarIcon name="check" class="text-green-600" size="l" />
          </div>
          <h1 class="text-xl font-semibold">{{ t('verifyEmail.successTitle', {}, 'Email confirmed') }}</h1>
          <p class="text-sm text-surface-600">
            {{ t('verifyEmail.successBody', {}, 'Thanks! Your new email address has been confirmed. An administrator will review and approve the change before it is applied.') }}
          </p>
          <CoarButton full-width @click="router.push('/login')">
            {{ t('verifyEmail.toLogin', {}, 'To login') }}
          </CoarButton>
        </template>

        <template v-else>
          <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-red-100">
            <CoarIcon name="x" class="text-red-600" size="l" />
          </div>
          <h1 class="text-xl font-semibold">{{ t('verifyEmail.errorTitle', {}, 'Confirmation failed') }}</h1>
          <p class="text-sm text-surface-600">{{ errorMessage }}</p>
          <CoarButton full-width @click="router.push('/login')">
            {{ t('verifyEmail.toLogin', {}, 'To login') }}
          </CoarButton>
        </template>
      </div>
    </CoarCard>
  </div>
</template>
