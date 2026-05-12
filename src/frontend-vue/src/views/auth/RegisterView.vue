<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import {
  CoarCard,
  CoarButton,
  CoarTextInput,
  CoarPasswordInput,
  CoarFormField,
  CoarCheckbox,
  CoarNote,
} from '@cocoar/vue-ui'

interface SelfRegistrationInfoDto {
  Enabled: boolean
  RequireEmailVerification: boolean
  RequireAdminApproval: boolean
  AllowedEmailDomains: string[] | null
  TermsOfServiceUrl: string | null
  PrivacyPolicyUrl: string | null
  CaptchaSiteKey: string | null
}

interface RegisterResponseDto {
  Message: string
}

const { t, language } = useI18n()
const localization = useLocalization()!
const router = useRouter()
const accountHttp = useHttpClient('/api/account')

async function toggleLanguage() {
  const next = language.value === 'de' ? 'en' : 'de'
  await localization.setLanguage(next)
  localStorage.setItem('language', next)
}

const loading = ref(true)
const info = ref<SelfRegistrationInfoDto | null>(null)
const error = ref('')
const submitting = ref(false)
const submitted = ref(false)
const successMessage = ref('')

// Form state. Honeypot is the bot-bait — UI never renders it for humans.
const form = ref({
  UserName: '',
  Email: '',
  Password: '',
  Firstname: '',
  Lastname: '',
  AcceptedTerms: false,
  CaptchaToken: '',
  Honeypot: '',
})

const tosRequired = computed(() => !!info.value?.TermsOfServiceUrl)
const captchaRequired = computed(() => !!info.value?.CaptchaSiteKey)
const canSubmit = computed(() => {
  if (submitting.value || submitted.value) return false
  if (!form.value.UserName.trim()) return false
  if (!form.value.Email.trim() || !form.value.Email.includes('@')) return false
  if (!form.value.Password) return false
  if (tosRequired.value && !form.value.AcceptedTerms) return false
  if (captchaRequired.value && !form.value.CaptchaToken) return false
  return true
})

// Turnstile script injection + widget mount. The CF script auto-renders
// any `.cf-turnstile` div present on load. To make it work with the
// conditional widget below (only mounted when CaptchaSiteKey resolves),
// we render the div first and inject the script after the DOM is ready;
// the script then picks the div up.
const TURNSTILE_SRC = 'https://challenges.cloudflare.com/turnstile/v0/api.js'
const captchaWidgetId = ref<string | null>(null)
let scriptEl: HTMLScriptElement | null = null

function ensureTurnstileScript(): Promise<void> {
  // Already loaded?
  if (typeof (window as any).turnstile !== 'undefined') return Promise.resolve()
  if (scriptEl) return Promise.resolve()
  return new Promise((resolve, reject) => {
    scriptEl = document.createElement('script')
    scriptEl.src = TURNSTILE_SRC
    scriptEl.async = true
    scriptEl.defer = true
    scriptEl.onload = () => resolve()
    scriptEl.onerror = () => reject(new Error('Turnstile load failed'))
    document.head.appendChild(scriptEl)
  })
}

async function mountTurnstile(siteKey: string) {
  await ensureTurnstileScript()
  // The script defines window.turnstile asynchronously; wait a tick.
  const ts = (window as any).turnstile
  if (!ts) return
  const container = document.getElementById('cf-turnstile-container')
  if (!container) return
  container.innerHTML = ''
  captchaWidgetId.value = ts.render(container, {
    sitekey: siteKey,
    callback: (token: string) => { form.value.CaptchaToken = token },
    'error-callback': () => { form.value.CaptchaToken = '' },
    'expired-callback': () => { form.value.CaptchaToken = '' },
  })
}

function resetTurnstile() {
  const ts = (window as any).turnstile
  if (ts && captchaWidgetId.value) ts.reset(captchaWidgetId.value)
  form.value.CaptchaToken = ''
}

onMounted(async () => {
  try {
    info.value = await accountHttp.addPath('self-registration-info').get<SelfRegistrationInfoDto>()
    if (!info.value.Enabled) {
      router.replace('/login')
      return
    }
    if (info.value.CaptchaSiteKey) {
      // Defer until next tick so the v-if container is in the DOM.
      requestAnimationFrame(() => mountTurnstile(info.value!.CaptchaSiteKey!))
    }
  } catch {
    error.value = t('common.connectionError', {}, 'Connection to server failed.')
  } finally {
    loading.value = false
  }
})

onBeforeUnmount(() => {
  const ts = (window as any).turnstile
  if (ts && captchaWidgetId.value) {
    try { ts.remove(captchaWidgetId.value) } catch { /* ignore */ }
  }
})

// Refresh widget if the user changed language (Cloudflare honours
// data-language, but we'd need a re-render — skip for MVP).
watch(language, () => { /* no-op */ })

async function handleSubmit() {
  if (!canSubmit.value) return
  submitting.value = true
  error.value = ''
  try {
    const res = await accountHttp.addPath('register').post<RegisterResponseDto>({
      UserName: form.value.UserName.trim(),
      Email: form.value.Email.trim(),
      Password: form.value.Password,
      Firstname: form.value.Firstname.trim() || null,
      Lastname: form.value.Lastname.trim() || null,
      AcceptedTerms: form.value.AcceptedTerms,
      CaptchaToken: form.value.CaptchaToken || null,
      Honeypot: form.value.Honeypot || null,
    })
    successMessage.value = res.Message
    submitted.value = true
  } catch (e) {
    // Backend uses 200-OK anti-enumeration for the common rejected paths,
    // so reaching this branch means something else: validation 400, rate
    // limit, server error.
    if (e instanceof HttpClientError) {
      const detail = (e.body as any)?.detail
      error.value = detail
        ?? (e.status === 429
          ? t('auth.register.rateLimited', {}, 'Too many attempts. Please try again later.')
          : t('auth.register.error', {}, 'Registration failed.'))
    } else {
      error.value = t('common.connectionError', {}, 'Connection to server failed.')
    }
    resetTurnstile()
  } finally {
    submitting.value = false
  }
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
    <div class="w-full max-w-sm">
      <div class="mb-8 text-center">
        <img src="/td-logo.svg" alt="Cocoar.Auth" class="mx-auto mb-1 h-16 w-auto" />
        <h1 class="text-2xl font-bold tracking-tight text-surface-800">
          Cocoar<span class="text-[#525e76]">.Auth</span>
        </h1>
        <p class="mt-2 text-sm text-surface-500">
          {{ t('auth.register.subtitle', {}, 'Create an account.') }}
        </p>
      </div>

      <CoarCard elevated>
        <div v-if="loading" class="p-2 text-center text-surface-500">
          {{ t('common.loading', {}, 'Loading...') }}
        </div>

        <div v-else-if="submitted" class="space-y-4">
          <CoarNote variant="success">{{ successMessage }}</CoarNote>
          <RouterLink to="/login"
            class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.register.toLogin', {}, 'To login') }}
          </RouterLink>
        </div>

        <form v-else class="space-y-4" @submit.prevent="handleSubmit">
          <CoarFormField :label="t('auth.register.username', {}, 'Username')">
            <CoarTextInput v-model="form.UserName" autocomplete="username" required />
          </CoarFormField>

          <CoarFormField :label="t('auth.register.email', {}, 'Email')">
            <CoarTextInput v-model="form.Email" type="email" autocomplete="email" required />
          </CoarFormField>

          <CoarFormField :label="t('auth.register.password', {}, 'Password')">
            <CoarPasswordInput v-model="form.Password" autocomplete="new-password" required />
          </CoarFormField>

          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('auth.register.firstname', {}, 'First name (optional)')">
              <CoarTextInput v-model="form.Firstname" autocomplete="given-name" />
            </CoarFormField>
            <CoarFormField :label="t('auth.register.lastname', {}, 'Last name (optional)')">
              <CoarTextInput v-model="form.Lastname" autocomplete="family-name" />
            </CoarFormField>
          </div>

          <div v-if="info?.AllowedEmailDomains && info.AllowedEmailDomains.length"
            class="text-xs text-surface-500">
            {{ t('auth.register.allowedDomainsHint', {}, 'Only emails from these domains:') }}
            <span class="font-mono">{{ info.AllowedEmailDomains.join(', ') }}</span>
          </div>

          <div v-if="tosRequired" class="flex flex-col gap-1">
            <CoarCheckbox v-model="form.AcceptedTerms"
              :label="t('auth.register.acceptTerms', {}, 'I accept the Terms of Service')" />
            <a :href="info!.TermsOfServiceUrl!" target="_blank" rel="noopener noreferrer"
              class="ml-7 text-xs text-surface-500 underline hover:text-surface-700">
              {{ t('auth.register.viewTerms', {}, 'View Terms') }}
            </a>
          </div>

          <!-- Captcha widget; mounted by Turnstile script when SiteKey present -->
          <div v-if="captchaRequired" id="cf-turnstile-container" class="flex justify-center"></div>

          <!-- Honeypot — invisible to humans, irresistible to dumb bots. -->
          <div aria-hidden="true" style="position:absolute; left:-10000px; width:1px; height:1px; overflow:hidden;">
            <label>Website
              <input v-model="form.Honeypot" type="text" tabindex="-1" autocomplete="off" />
            </label>
          </div>

          <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>

          <CoarButton
            type="submit"
            :disabled="!canSubmit"
            :loading="submitting"
            full-width
          >
            {{ t('auth.register.submit', {}, 'Create account') }}
          </CoarButton>

          <div class="flex justify-between text-sm">
            <RouterLink to="/login" class="text-surface-500 hover:text-surface-700 hover:underline">
              {{ t('auth.register.haveAccount', {}, 'Already have an account?') }}
            </RouterLink>
            <a v-if="info?.PrivacyPolicyUrl" :href="info.PrivacyPolicyUrl" target="_blank" rel="noopener noreferrer"
              class="text-surface-500 hover:text-surface-700 hover:underline">
              {{ t('auth.register.privacy', {}, 'Privacy') }}
            </a>
          </div>
        </form>
      </CoarCard>
    </div>
  </div>
</template>
