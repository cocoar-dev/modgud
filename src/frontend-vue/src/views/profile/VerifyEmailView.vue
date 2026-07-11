<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useI18n } from '@cocoar/vue-localization'
import { CoarCard, CoarButton, CoarIcon } from '@cocoar/vue-ui'

interface SelfRegVerifyResponse {
  UserName: string
  Email: string
  RequiresAdminApproval: boolean
}

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const profileHttp = useHttpClient('/api/account/profile')
const accountHttp = useHttpClient('/api/account')

// Three flows share the /verify-email route, dispatched by query params:
// - profile email-change-verify: ?id=<requestId>&token=<token>
// - account email re-verify:     ?type=account&token=<plaintext>
// - self-registration verify:    ?token=<plaintext>           (default)
// The self-reg flow surfaces RequiresAdminApproval so the UI can tell
// the user "wait for admin" vs "go log in".
type State = 'verifying' | 'success' | 'success-pending-approval' | 'error'
const state = ref<State>('verifying')
const errorMessage = ref('')
const flow = ref<'profile-change' | 'account' | 'self-reg' | null>(null)

onMounted(async () => {
  const id = route.query.id as string | undefined
  const token = route.query.token as string | undefined
  const type = route.query.type as string | undefined

  if (!token) {
    state.value = 'error'
    errorMessage.value = t('verifyEmail.missing', {}, 'Invalid confirmation link.')
    return
  }

  if (id) {
    flow.value = 'profile-change'
    try {
      await profileHttp.addPath('request', 'verify-email').post({ RequestId: id, Token: token })
      state.value = 'success'
    } catch (e: unknown) {
      state.value = 'error'
      errorMessage.value = e instanceof HttpClientError && e.status === 400
        ? t('verifyEmail.invalid', {}, 'Link is invalid or expired.')
        : t('verifyEmail.error', {}, 'Confirmation failed.')
    }
    return
  }

  if (type === 'account') {
    flow.value = 'account'
    try {
      await accountHttp.addPath('email', 'verify').post({ Token: token })
      state.value = 'success'
    } catch (e: unknown) {
      state.value = 'error'
      errorMessage.value = e instanceof HttpClientError && e.status === 400
        ? t('verifyEmail.account.invalid', {}, 'Verification link is invalid or expired.')
        : t('verifyEmail.error', {}, 'Confirmation failed.')
    }
    return
  }

  flow.value = 'self-reg'
  try {
    const res = await accountHttp.addPath('register', 'verify-email').post<SelfRegVerifyResponse>({ Token: token })
    state.value = res.RequiresAdminApproval ? 'success-pending-approval' : 'success'
  } catch (e: unknown) {
    state.value = 'error'
    // Backend emits { error: "<English description>" } via ErrorOrExtensions.
    // We can't reliably i18n on the description alone, so fall back to a
    // generic "invalid or expired" copy that covers the common cases
    // (TokenUnknown / TokenUsed / TokenExpired).
    errorMessage.value = e instanceof HttpClientError && e.status === 400
      ? t('verifyEmail.selfReg.invalid', {}, 'Confirmation link is invalid or expired. Please register again.')
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
            <template v-if="flow === 'self-reg'">
              {{ t('verifyEmail.selfReg.success', {}, 'Your account has been confirmed. You can now sign in.') }}
            </template>
            <template v-else-if="flow === 'account'">
              {{ t('verifyEmail.account.success', {}, 'Your email address has been verified. You can sign in again.') }}
            </template>
            <template v-else>
              {{ t('verifyEmail.successBody', {}, 'Thanks! Your new email address has been confirmed. An administrator will review and approve the change before it is applied.') }}
            </template>
          </p>
          <CoarButton full-width @click="router.push({ path: '/login', query: { redirect: route.query.redirect } })">
            {{ t('verifyEmail.toLogin', {}, 'To login') }}
          </CoarButton>
        </template>

        <template v-else-if="state === 'success-pending-approval'">
          <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-yellow-100">
            <CoarIcon name="hourglass" class="text-yellow-600" size="l" />
          </div>
          <h1 class="text-xl font-semibold">
            {{ t('verifyEmail.selfReg.pendingTitle', {}, 'Email confirmed — pending admin approval') }}
          </h1>
          <p class="text-sm text-surface-600">
            {{ t('verifyEmail.selfReg.pendingBody', {}, 'Your email address has been confirmed. An administrator still needs to approve your account — you will be notified.') }}
          </p>
          <CoarButton full-width @click="router.push({ path: '/login', query: { redirect: route.query.redirect } })">
            {{ t('verifyEmail.toLogin', {}, 'To login') }}
          </CoarButton>
        </template>

        <template v-else>
          <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-red-100">
            <CoarIcon name="x" class="text-red-600" size="l" />
          </div>
          <h1 class="text-xl font-semibold">{{ t('verifyEmail.errorTitle', {}, 'Confirmation failed') }}</h1>
          <p class="text-sm text-surface-600">{{ errorMessage }}</p>
          <CoarButton full-width @click="router.push({ path: '/login', query: { redirect: route.query.redirect } })">
            {{ t('verifyEmail.toLogin', {}, 'To login') }}
          </CoarButton>
        </template>
      </div>
    </CoarCard>
  </div>
</template>
