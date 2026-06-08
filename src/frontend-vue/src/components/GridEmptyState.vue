<script setup lang="ts">
/**
 * Reusable onboarding empty-state for admin list grids (UI/UX wave 2).
 *
 * Rendered as a sibling to <CoarDataGrid> and shown only when the view's
 * *source* data is genuinely empty (zero rows after the store has loaded) —
 * NOT when a search or filter merely yields no matches (that case keeps the
 * grid and its localized "Keine Einträge vorhanden" overlay). So this always
 * speaks the onboarding voice: what the screen is for, plus a primary action.
 *
 * For abstract concepts (OAuth API, Service Account) this doubles as the
 * teaching moment. Read-only log/queue views pass no CTA.
 */
import { CoarIcon, CoarButton } from '@cocoar/vue-ui'

defineProps<{
  /** lucide icon name — usually the view's own header icon */
  icon: string
  /** the concept name, e.g. "Service Accounts" */
  title: string
  /** one-line definition of the concept */
  description: string
  /** primary CTA label (e.g. "Erstellen"); omit for read-only views */
  ctaLabel?: string
}>()

defineEmits<{ cta: [] }>()
</script>

<template>
  <div class="grid-empty-state">
    <CoarIcon :name="icon" size="l" class="grid-empty-state__icon" />
    <p class="grid-empty-state__title">{{ title }}</p>
    <p class="grid-empty-state__desc">{{ description }}</p>
    <CoarButton
      v-if="ctaLabel"
      size="s"
      icon-start="plus"
      class="grid-empty-state__cta"
      @click="$emit('cta')"
    >
      {{ ctaLabel }}
    </CoarButton>
  </div>
</template>

<style scoped>
.grid-empty-state {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.5rem;
  padding: 2rem 1rem;
  text-align: center;
  color: var(--coar-text-neutral-secondary, #6b7280);
}

.grid-empty-state__icon {
  color: var(--coar-text-neutral-tertiary, #9ca3af);
  margin-bottom: 0.25rem;
}

.grid-empty-state__title {
  margin: 0;
  font-size: 1rem;
  font-weight: 600;
  color: var(--coar-text-neutral-primary, #111827);
}

.grid-empty-state__desc {
  margin: 0;
  max-width: 440px;
  line-height: 1.45;
}

.grid-empty-state__cta {
  margin-top: 0.5rem;
}
</style>
