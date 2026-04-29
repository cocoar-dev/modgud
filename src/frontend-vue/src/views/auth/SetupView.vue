<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth.store'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import {
  CoarCard,
  CoarButton,
  CoarTextInput,
  CoarPasswordInput,
  CoarFormField,
  CoarCheckbox,
  CoarNote,
  CoarDivider,
} from '@cocoar/vue-ui'

const router = useRouter()
const authStore = useAuthStore()
const { t, language } = useI18n()
const localization = useLocalization()!

async function toggleLanguage() {
  const next = language.value === 'de' ? 'en' : 'de'
  await localization.setLanguage(next)
  localStorage.setItem('language', next)
}

const loading = ref(true)
const submitting = ref(false)
const errorMessage = ref('')
const hasDemoSeed = ref(false)
const loadDemoData = ref(false)

const userName = ref('')
const password = ref('')
const passwordConfirm = ref('')

// Backend Identity policy (Program.cs): RequiredLength=8, RequireDigit,
// RequireLowercase, RequireUppercase. Mirrored here so the UI can show live
// feedback — keep in sync if the backend policy changes.
const passwordRules = computed(() => {
  const v = password.value
  return {
    length: v.length >= 8,
    digit: /[0-9]/.test(v),
    lower: /[a-z]/.test(v),
    upper: /[A-Z]/.test(v),
  }
})

const passwordValid = computed(() => {
  const r = passwordRules.value
  return r.length && r.digit && r.lower && r.upper
})

const passwordsMatch = computed(() =>
  password.value.length > 0 && password.value === passwordConfirm.value,
)

const showMismatch = computed(() =>
  passwordConfirm.value.length > 0 && password.value !== passwordConfirm.value,
)

const canSubmit = computed(() =>
  Boolean(userName.value.trim()) &&
  passwordValid.value &&
  passwordsMatch.value &&
  !submitting.value,
)

onMounted(async () => {
  try {
    const status = await authStore.fetchSetupStatus()
    if (!status.NeedsSetup) {
      router.replace('/login')
      return
    }
    hasDemoSeed.value = status.HasDemoSeed
  } catch {
    errorMessage.value = t('auth.setup.loadError', {}, 'Error loading setup status.')
  } finally {
    loading.value = false
  }
})

async function createAdmin() {
  errorMessage.value = ''

  if (!userName.value.trim()) {
    errorMessage.value = t('auth.setup.usernameRequired', {}, 'Username is required.')
    return
  }
  if (!password.value || password.value.length < 8) {
    errorMessage.value = t('auth.setup.passwordMinLength', {}, 'Password must be at least 8 characters long.')
    return
  }
  if (password.value !== passwordConfirm.value) {
    errorMessage.value = t('auth.setup.passwordMismatch', {}, 'Passwords do not match.')
    return
  }

  submitting.value = true

  try {
    await authStore.createAdmin({
      UserName: userName.value.trim(),
      Password: password.value,
      LoadDemoData: loadDemoData.value || undefined,
    })
    router.replace('/dashboard')
  } catch (e: any) {
    errorMessage.value = e?.response?.data?.detail
      ?? e?.response?.data?.Detail
      ?? t('auth.setup.createError', {}, 'Error creating administrator.')
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
    <div class="w-full max-w-md">
      <!-- Logo + Title (same as LoginView) -->
      <div class="mb-8 text-center">
        <img src="/td-logo.svg" alt="Cocoar.Auth" class="mx-auto mb-1 h-16 w-auto" />
        <h1 class="text-2xl font-bold tracking-tight text-surface-800">
          Cocoar<span class="text-[#525e76]">.Auth</span>
        </h1>
        <p class="mt-2 text-sm text-surface-500">
          {{ t('auth.setup.subtitle', {}, 'Initial Setup') }}
        </p>
      </div>

      <!-- Loading -->
      <div v-if="loading" class="flex items-center justify-center p-12">
        <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
      </div>

      <!-- Form -->
      <CoarCard v-else elevated>
        <form class="space-y-4" @submit.prevent="createAdmin">
          <CoarFormField :label="t('auth.setup.username', {}, 'Username')">
            <CoarTextInput
              v-model="userName"
              :placeholder="t('auth.setup.usernamePlaceholder', {}, 'Username')"
              autocomplete="username"
              :disabled="submitting"
            />
          </CoarFormField>

          <CoarFormField :label="t('auth.setup.password', {}, 'Password')">
            <CoarPasswordInput
              v-model="password"
              :placeholder="t('auth.setup.password', {}, 'Password')"
              autocomplete="new-password"
              :disabled="submitting"
            />
            <ul class="policy-list mt-2">
              <li :class="{ ok: passwordRules.length }">
                <span class="policy-icon">{{ passwordRules.length ? '✓' : '•' }}</span>
                {{ t('auth.setup.policy.length', {}, 'At least 8 characters') }}
              </li>
              <li :class="{ ok: passwordRules.upper }">
                <span class="policy-icon">{{ passwordRules.upper ? '✓' : '•' }}</span>
                {{ t('auth.setup.policy.upper', {}, 'An uppercase letter (A–Z)') }}
              </li>
              <li :class="{ ok: passwordRules.lower }">
                <span class="policy-icon">{{ passwordRules.lower ? '✓' : '•' }}</span>
                {{ t('auth.setup.policy.lower', {}, 'A lowercase letter (a–z)') }}
              </li>
              <li :class="{ ok: passwordRules.digit }">
                <span class="policy-icon">{{ passwordRules.digit ? '✓' : '•' }}</span>
                {{ t('auth.setup.policy.digit', {}, 'A digit (0–9)') }}
              </li>
            </ul>
          </CoarFormField>

          <CoarFormField :label="t('auth.setup.confirmPassword', {}, 'Confirm Password')">
            <CoarPasswordInput
              v-model="passwordConfirm"
              :placeholder="t('auth.setup.confirmPassword', {}, 'Confirm Password')"
              autocomplete="new-password"
              :disabled="submitting"
            />
            <p v-if="showMismatch" class="mismatch mt-1">
              {{ t('auth.setup.passwordMismatch', {}, 'Passwords do not match.') }}
            </p>
          </CoarFormField>

          <!-- Demo data -->
          <template v-if="hasDemoSeed">
            <CoarDivider variant="subtle" />
            <CoarCheckbox v-model="loadDemoData" :label="t('auth.setup.demoCheckbox', {}, 'Load demo data')" :disabled="submitting" />
            <p class="text-xs text-surface-400 -mt-2 ml-7">
              {{ t('auth.setup.demoDescription', {}, 'Creates demo users, groups, and permission scenarios. Demo user password: Demo1234!') }}
            </p>
          </template>

          <CoarNote v-if="errorMessage" variant="error">{{ errorMessage }}</CoarNote>

          <CoarButton
            type="submit"
            :disabled="!canSubmit"
            :loading="submitting"
            full-width
          >
            {{ t('auth.setup.createAdmin', {}, 'Create Administrator') }}
          </CoarButton>
        </form>
      </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.policy-list {
  list-style: none;
  padding: 0;
  margin: 0;
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #525e76);
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.policy-list li {
  display: flex;
  align-items: center;
  gap: 6px;
  opacity: 0.7;
}

.policy-list li.ok {
  color: var(--coar-text-semantic-success, #2e7d32);
  opacity: 1;
}

.policy-icon {
  display: inline-block;
  width: 0.9em;
  text-align: center;
  font-weight: 600;
}

.mismatch {
  font-size: 0.75rem;
  color: var(--coar-text-semantic-error, #c62828);
}
</style>
