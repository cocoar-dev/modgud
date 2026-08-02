<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import {
  CoarNotice,
  CoarCard,
  CoarButton,
  CoarTextInput,
  CoarSpinner,
} from '@cocoar/vue-ui'
import type { DeviceVerificationInfo } from '@/models/device'
import AuthBrand from '@/components/auth/AuthBrand.vue'

const { t, language } = useI18n()
const localization = useLocalization()!
const route = useRoute()
const router = useRouter()
const infoHttp = useHttpClient('/connect/device-verification')
const codeHttp = useHttpClient('/connect/device-verification/code')

async function toggleLanguage() {
  const next = language.value === 'de' ? 'en' : 'de'
  await localization.setLanguage(next)
  localStorage.setItem('language', next)
}

type Phase = 'loading' | 'needs_code' | 'prompt' | 'approved' | 'denied' | 'error'
const phase = ref<Phase>('loading')
const error = ref('')
const submitting = ref(false)

const ticket = ref<string>((route.query.ticket as string | undefined) ?? '')
const codeInput = ref<string>('')
const codeError = ref('')
const model = ref<DeviceVerificationInfo | null>(null)

const standardScopeFallbacks: Record<string, { label: string; description: string }> = {
  openid: {
    label: t('consent.scope.openid.label', {}, 'Sign-in identity'),
    description: t('consent.scope.openid.description', {}, 'Confirms who you are. Required to sign in.'),
  },
  profile: {
    label: t('consent.scope.profile.label', {}, 'Profile'),
    description: t('consent.scope.profile.description', {}, 'Name, picture, locale.'),
  },
  email: {
    label: t('consent.scope.email.label', {}, 'Email address'),
    description: t('consent.scope.email.description', {}, 'Your email address and whether it is verified.'),
  },
  offline_access: {
    label: t('consent.scope.offline_access.label', {}, 'Stay signed in'),
    description: t('consent.scope.offline_access.description', {}, 'Allows the app to refresh its access without prompting again.'),
  },
  roles: {
    label: t('consent.scope.roles.label', {}, 'Roles'),
    description: t('consent.scope.roles.description', {}, 'Lets the app see which roles you have in this realm.'),
  },
  permissions: {
    label: t('consent.scope.permissions.label', {}, 'Permissions'),
    description: t('consent.scope.permissions.description', {}, 'Lets the app see which fine-grained permissions you have.'),
  },
}

const clientName = computed(() => model.value?.ClientName || t('device.theApp', {}, 'the device'))

onMounted(async () => {
  // No ticket = the user navigated to /device directly (typed the URL the
  // device showed). Prompt for the code; the API call below will 401→login
  // first if they aren't signed in yet.
  if (!ticket.value) {
    await ensureSessionThen(() => { phase.value = 'needs_code' })
    return
  }
  await loadTicket()
})

async function ensureSessionThen(onReady: () => void) {
  // Touch a cookie-protected endpoint to force the login bounce when needed.
  try {
    await infoHttp.setQueryParameter('ticket', '00000000000000000000000000000000').get<DeviceVerificationInfo>()
    onReady()
  } catch (e) {
    if (e instanceof HttpClientError && e.status === 401) {
      router.replace(`/login?redirect=${encodeURIComponent(route.fullPath)}`)
      return
    }
    // Any non-401 (e.g. 404 for the dummy ticket) means we ARE authenticated.
    onReady()
  }
}

async function loadTicket() {
  phase.value = 'loading'
  try {
    const dto = await infoHttp.setQueryParameter('ticket', ticket.value).get<DeviceVerificationInfo>()
    applyInfo(dto)
  } catch (e) {
    handleLoadError(e)
  }
}

function applyInfo(dto: DeviceVerificationInfo) {
  model.value = dto
  if (dto.Status === 'ready') {
    phase.value = 'prompt'
  } else if (dto.Status === 'invalid_code') {
    phase.value = 'needs_code'
    codeError.value = t('device.invalidCode', {}, 'That code is invalid or has expired. Check the code on your device and try again.')
  } else {
    phase.value = 'needs_code'
  }
}

function handleLoadError(e: unknown) {
  phase.value = 'error'
  if (e instanceof HttpClientError) {
    switch (e.status) {
      case 401:
        router.replace(`/login?redirect=${encodeURIComponent(route.fullPath)}`)
        return
      case 403:
        error.value = t('device.forbidden', {}, 'This device request belongs to a different account. Please sign in with the correct account.')
        break
      case 404:
        error.value = t('device.notFound', {}, 'Device request not found or expired. Restart the sign-in on your device.')
        break
      case 409:
        error.value = t('device.alreadyUsed', {}, 'This device request has already been completed.')
        break
      case 400:
        error.value = t('device.expired', {}, 'Device request expired. Restart the sign-in on your device.')
        break
      default:
        error.value = t('device.loadError', {}, 'Failed to load the device request.')
    }
  } else {
    error.value = t('common.connectionError', {}, 'Connection to server failed.')
  }
}

async function submitCode() {
  const code = codeInput.value.trim()
  if (!code || submitting.value) return
  submitting.value = true
  codeError.value = ''
  try {
    // A fresh ticket is created server-side at /connect/verify; when the user
    // typed the URL directly we have none, so mint one by hitting verify with
    // no code first. Simpler: the code-submit endpoint needs a ticket — if we
    // don't have one yet, get it now.
    if (!ticket.value) {
      const created = await fetch('/connect/verify', { credentials: 'include', redirect: 'manual' })
      // /connect/verify (no code) 302s to /device?ticket=… — read it back.
      const loc = created.headers.get('location') ?? ''
      const m = loc.match(/[?&]ticket=([^&]+)/)
      if (m?.[1]) ticket.value = decodeURIComponent(m[1])
    }
    const dto = await codeHttp.post<DeviceVerificationInfo>({ Ticket: ticket.value, UserCode: code })
    applyInfo(dto)
  } catch (e) {
    if (e instanceof HttpClientError && e.status === 401) {
      router.replace(`/login?redirect=${encodeURIComponent(route.fullPath)}`)
      return
    }
    codeError.value = t('device.submitCodeError', {}, 'Could not check that code. Please try again.')
  } finally {
    submitting.value = false
  }
}

async function decide(approve: boolean) {
  const userCode = model.value?.UserCode
  if (!userCode || submitting.value) return
  submitting.value = true
  error.value = ''
  try {
    // The decision goes through the OpenIddict end-user verification endpoint
    // so it binds to the pending device code via the user_code. OpenIddict owns
    // the response (no clean JSON), so treat any non-error / redirect as done.
    const resp = await fetch('/connect/verify', {
      method: 'POST',
      credentials: 'include',
      redirect: 'manual',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ user_code: userCode, decision: approve ? 'approve' : 'deny' }),
    })
    const done = resp.ok || resp.type === 'opaqueredirect' || resp.status === 0 || (resp.status >= 300 && resp.status < 400)
    if (!done && resp.status >= 400) {
      error.value = t('device.decisionError', {}, 'Could not submit your decision. Please try again.')
      return
    }
    phase.value = approve ? 'approved' : 'denied'
  } catch {
    error.value = t('common.connectionError', {}, 'Connection to server failed.')
  } finally {
    submitting.value = false
  }
}

function scopeLabel(name: string, fallback: string): string {
  return standardScopeFallbacks[name]?.label ?? (fallback || name)
}

function scopeDescription(name: string, fallback: string | null | undefined): string | null {
  if (fallback && fallback.trim()) return fallback
  return standardScopeFallbacks[name]?.description ?? null
}
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-surface-50 p-4 relative">
    <button
      class="absolute top-4 right-4 text-xs text-surface-400 hover:text-surface-600 transition"
      @click="toggleLanguage"
    >
      {{ language === 'de' ? 'EN' : 'DE' }}
    </button>
    <div class="w-full max-w-md">
      <div class="mb-8 text-center">
        <AuthBrand spacing="compact" />
      </div>

      <CoarCard elevated>
        <!-- Loading -->
        <div v-if="phase === 'loading'" class="flex flex-col items-center gap-3 p-4 text-center">
          <CoarSpinner />
          <p class="text-sm text-surface-500">{{ t('common.loading', {}, 'Loading...') }}</p>
        </div>

        <!-- Error -->
        <div v-else-if="phase === 'error'" class="space-y-4">
          <CoarNotice variant="error">{{ error }}</CoarNotice>
          <CoarButton full-width @click="router.push('/login')">
            {{ t('consent.toLogin', {}, 'Back to sign-in') }}
          </CoarButton>
        </div>

        <!-- Code entry -->
        <div v-else-if="phase === 'needs_code'" class="space-y-4">
          <div class="text-center">
            <h2 class="text-lg font-semibold text-surface-800">
              {{ t('device.enterCodeTitle', {}, 'Connect your device') }}
            </h2>
            <p class="mt-1 text-sm text-surface-500">
              {{ t('device.enterCodeSubtitle', {}, 'Enter the code shown on your device.') }}
            </p>
          </div>
          <CoarTextInput
            v-model="codeInput"
            :placeholder="t('device.codePlaceholder', {}, 'e.g. WDJB-MJHT')"
            autocapitalize="characters"
            autocomplete="one-time-code"
            @keyup.enter="submitCode"
          />
          <CoarNotice v-if="codeError" variant="error">{{ codeError }}</CoarNotice>
          <CoarButton :loading="submitting" :disabled="!codeInput.trim()" full-width @click="submitCode">
            {{ t('device.continue', {}, 'Continue') }}
          </CoarButton>
        </div>

        <!-- Approve / deny prompt -->
        <div v-else-if="phase === 'prompt'" class="space-y-4">
          <div class="text-center">
            <h2 class="text-lg font-semibold text-surface-800">
              {{ t('device.title', { client: clientName }, 'Connect {client}?') }}
            </h2>
            <p class="mt-1 text-sm text-surface-500">
              {{ t('device.subtitle', {}, 'A device is asking to sign in to your account with the access below.') }}
            </p>
          </div>

          <div v-if="model!.Scopes.length" class="space-y-2">
            <div
              v-for="scope in model!.Scopes"
              :key="scope.Name"
              class="rounded border border-surface-200 bg-white px-3 py-2"
            >
              <p class="text-sm font-medium text-surface-800">{{ scopeLabel(scope.Name, scope.DisplayName) }}</p>
              <p v-if="scopeDescription(scope.Name, scope.Description)" class="mt-0.5 text-xs text-surface-500">
                {{ scopeDescription(scope.Name, scope.Description) }}
              </p>
            </div>
          </div>

          <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>

          <div class="flex gap-2">
            <CoarButton variant="secondary" :disabled="submitting" full-width @click="decide(false)">
              {{ t('device.deny', {}, 'Deny') }}
            </CoarButton>
            <CoarButton :loading="submitting" full-width @click="decide(true)">
              {{ t('device.allow', {}, 'Allow') }}
            </CoarButton>
          </div>
        </div>

        <!-- Approved -->
        <div v-else-if="phase === 'approved'" class="space-y-4 text-center">
          <h2 class="text-lg font-semibold text-surface-800">
            {{ t('device.approvedTitle', {}, 'Device connected') }}
          </h2>
          <p class="text-sm text-surface-600">
            {{ t('device.approvedBody', {}, 'You can return to your device — it is now signed in. You may close this page.') }}
          </p>
        </div>

        <!-- Denied -->
        <div v-else-if="phase === 'denied'" class="space-y-4 text-center">
          <h2 class="text-lg font-semibold text-surface-800">
            {{ t('device.deniedTitle', {}, 'Request denied') }}
          </h2>
          <p class="text-sm text-surface-600">
            {{ t('device.deniedBody', {}, 'The device was not connected. You can close this page.') }}
          </p>
        </div>
      </CoarCard>
    </div>
  </div>
</template>
