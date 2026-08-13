<script setup lang="ts">
import { computed } from 'vue'
import { useAppConfigStore } from '@/stores/appconfig.store'

withDefaults(defineProps<{
  spacing?: 'compact' | 'normal'
  showName?: boolean
  showLegal?: boolean
}>(), {
  spacing: 'normal',
  showName: true,
  showLegal: true,
})

const appConfig = useAppConfigStore()
const branding = computed(() => appConfig.config.Branding)
</script>

<template>
  <div class="text-center" data-testid="auth-brand">
    <img
      :src="branding.LogoUrl || '/idp-logo.svg'"
      :alt="branding.ProductName || 'Modgud'"
      class="mx-auto h-16 w-auto object-contain"
      :class="spacing === 'compact' ? 'mb-1' : 'mb-4'"
    />
    <h1 v-if="showName" class="text-2xl font-bold tracking-tight text-surface-800">
      {{ branding.ProductName || 'Modgud' }}
    </h1>
    <div v-if="showLegal && (appConfig.config.Legal.TermsOfServiceUrl || appConfig.config.Legal.PrivacyPolicyUrl)" class="mt-2 flex justify-center gap-3 text-xs text-surface-400">
      <a v-if="appConfig.config.Legal.TermsOfServiceUrl" :href="appConfig.config.Legal.TermsOfServiceUrl" target="_blank" rel="noopener noreferrer">Terms</a>
      <a v-if="appConfig.config.Legal.PrivacyPolicyUrl" :href="appConfig.config.Legal.PrivacyPolicyUrl" target="_blank" rel="noopener noreferrer">Privacy</a>
    </div>
  </div>
</template>
