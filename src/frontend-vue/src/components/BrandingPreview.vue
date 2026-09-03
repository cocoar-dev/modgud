<script setup lang="ts">
import { computed } from 'vue'

/**
 * Login-shell preview (product name, logo, primary colour). The email preview is
 * NOT mocked up here any more — see EmailPreview.vue, which renders the real
 * templates through the backend.
 */
const props = withDefaults(defineProps<{
  productName?: string | null
  logoUrl?: string | null
  primaryColor?: string | null
}>(), {
  productName: null,
  logoUrl: null,
  primaryColor: null,
})

const buttonContrast = computed(() => {
  const value = props.primaryColor || '#525e76'
  if (!/^#[0-9a-f]{6}$/i.test(value)) return null
  const channels = [1, 3, 5].map((offset) => {
    const c = parseInt(value.slice(offset, offset + 2), 16) / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  })
  const luminance = 0.2126 * channels[0]! + 0.7152 * channels[1]! + 0.0722 * channels[2]!
  return 1.05 / (luminance + 0.05)
})
</script>

<template>
  <div class="grid gap-3" aria-label="Branding preview">
    <p v-if="buttonContrast !== null && buttonContrast < 4.5" class="rounded border border-amber-300 bg-amber-50 p-2 text-xs text-amber-800">
      White button text has only {{ buttonContrast.toFixed(2) }}:1 contrast. Choose a darker primary colour (WCAG AA requires 4.5:1 for normal text).
    </p>
    <div class="rounded-lg border border-surface-200 bg-surface-50 p-5 text-center">
      <p class="mb-4 text-left text-xs font-medium uppercase tracking-wide text-surface-400">Login preview</p>
      <img :src="logoUrl || '/idp-logo.svg'" alt="" class="mx-auto mb-2 h-12 max-w-40 object-contain" />
      <div class="text-xl font-bold text-surface-800">{{ productName || 'Modgud' }}</div>
      <button type="button" class="mt-5 w-full rounded px-4 py-2 text-sm font-semibold text-white"
        :style="{ backgroundColor: primaryColor || '#525e76' }" tabindex="-1">
        Sign in
      </button>
    </div>
  </div>
</template>
