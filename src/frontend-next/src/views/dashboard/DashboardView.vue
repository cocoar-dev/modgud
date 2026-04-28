<script setup lang="ts">
import { watch } from 'vue'
import { useI18n } from '@cocoar/vue-localization'
import { CoarCard } from '@cocoar/vue-ui'
import { useUI } from '@/composables/useUI'
import { useAuthStore } from '@/stores/auth.store'

const { t, language } = useI18n()
const authStore = useAuthStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('dashboard.title', {}, 'Dashboard')
  ctx.header.icon = 'layout-dashboard'
  ctx.content.container = true
}), { immediate: true })
</script>

<template>
  <div class="w-full py-6 space-y-6">
    <CoarCard elevated>
      <div class="p-6 space-y-2">
        <h2 class="text-lg font-semibold">
          {{ t('dashboard.welcomeTitle', {}, 'Welcome to Cocoar.Auth') }}
        </h2>
        <p class="text-sm text-surface-500">
          {{ t('dashboard.welcomeBody', {}, 'You are signed in as') }}
          <strong>{{ authStore.user?.UserName }}</strong>.
        </p>
      </div>
    </CoarCard>
  </div>
</template>
