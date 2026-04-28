<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth.store'
import { useI18n, useLocalization } from '@cocoar/vue-localization'

const route = useRoute()
const router = useRouter()
const authStore = useAuthStore()
const { t, language } = useI18n()
const localization = useLocalization()!

async function toggleLanguage() {
  const next = language.value === 'de' ? 'en' : 'de'
  await localization.setLanguage(next)
  localStorage.setItem('language', next)
}

const status = ref<'loading' | 'success' | 'error'>('loading')
const errorMessage = ref('')

onMounted(async () => {
  const userId = route.query.userId as string
  const token = route.query.token as string

  if (!userId || !token) {
    status.value = 'error'
    errorMessage.value = t('auth.magicLogin.invalidLink', {}, 'Invalid login link.')
    return
  }

  try {
    await authStore.magicLinkLogin(userId, token)
    status.value = 'success'
    router.replace('/todos')
  } catch {
    status.value = 'error'
    errorMessage.value = t('auth.magicLogin.expiredLink', {}, 'This login link is invalid or expired.')
  }
})
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-surface-50 p-4 relative">
    <button
      class="absolute top-4 right-4 text-xs text-surface-400 hover:text-surface-600 transition"
      @click="toggleLanguage"
    >
      {{ language === 'de' ? 'EN' : 'DE' }}
    </button>
    <div class="w-full max-w-sm text-center">
      <div class="mb-8">
        <h1 class="text-2xl font-bold tracking-tight text-surface-800">
          Time<span class="text-[#525e76]">ToDo</span>
        </h1>
      </div>

      <!-- Loading -->
      <div v-if="status === 'loading'" class="space-y-4">
        <p class="text-surface-500">{{ t('auth.magicLogin.loggingIn', {}, 'Logging in...') }}</p>
      </div>

      <!-- Error -->
      <div v-else-if="status === 'error'" class="space-y-4">
        <p class="text-red-600">{{ errorMessage }}</p>
        <RouterLink
          to="/login"
          class="inline-block rounded bg-[#525e76] px-4 py-2 text-sm font-medium text-white transition hover:bg-[#434d61]"
        >
          {{ t('auth.magicLogin.goToLogin', {}, 'Go to login') }}
        </RouterLink>
      </div>
    </div>
  </div>
</template>
