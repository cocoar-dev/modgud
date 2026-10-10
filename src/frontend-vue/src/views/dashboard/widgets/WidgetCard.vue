<script setup lang="ts">
import { CoarCard, CoarIcon, CoarNotice, CoarSpinner } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

/** The shared frame of a dashboard widget: heading, body states, footer link. */
defineProps<{
  title: string
  icon: string
  /** Quiet line under the heading — what the numbers cover. */
  subtitle?: string
  loading?: boolean
  failed?: boolean
  /** Shown instead of the body when there is nothing to show. */
  empty?: string | null
  /** Footer link label; emits `open` when clicked. */
  cta?: string
}>()

defineEmits<{ open: [] }>()

const { t } = useI18n()
</script>

<template>
  <CoarCard elevated class="widget-card">
    <div class="widget-card__inner">
      <header class="widget-card__header">
        <h3 class="widget-card__title">
          <CoarIcon :name="icon" size="s" />
          {{ title }}
        </h3>
        <slot name="aside" />
      </header>
      <p v-if="subtitle" class="widget-card__subtitle">{{ subtitle }}</p>

      <div class="widget-card__body">
        <div v-if="loading" class="widget-card__state">
          <CoarSpinner size="m" />
        </div>
        <CoarNotice v-else-if="failed" variant="error">
          {{ t('dashboard.errors.loadFailed', {}, 'Failed to load the data.') }}
        </CoarNotice>
        <div v-else-if="empty" class="widget-card__state widget-card__state--empty">{{ empty }}</div>
        <slot v-else />
      </div>

      <footer v-if="cta" class="widget-card__footer">
        <button type="button" class="widget-card__cta" @click="$emit('open')">{{ cta }}</button>
      </footer>
    </div>
  </CoarCard>
</template>

<style scoped>
.widget-card {
  height: 100%;
}
.widget-card__inner {
  display: flex;
  flex-direction: column;
  height: 100%;
  padding: 1rem;
}
.widget-card__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
}
.widget-card__title {
  display: flex;
  align-items: center;
  gap: 0.375rem;
  margin: 0;
  font-size: 0.875rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--coar-text-neutral-secondary, #6b7280);
}
.widget-card__subtitle {
  margin: 0.125rem 0 0;
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
}
.widget-card__body {
  flex: 1;
  min-width: 0;
  margin-top: 0.75rem;
}
.widget-card__state {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 4rem;
}
.widget-card__state--empty {
  font-size: 0.875rem;
  color: var(--coar-text-neutral-secondary, #9ca3af);
  text-align: center;
}
.widget-card__footer {
  margin-top: 0.75rem;
}
.widget-card__cta {
  padding: 0;
  border: 0;
  background: transparent;
  font-size: 0.8125rem;
  color: var(--coar-text-link, #2563eb);
  cursor: pointer;
}
.widget-card__cta:hover {
  text-decoration: underline;
}

/* Rows shared by the list-style widgets. */
:deep(.widget-row) {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  width: 100%;
  padding: 0.5rem;
  border: none;
  border-radius: 0.375rem;
  background: none;
  color: inherit;
  font-size: 0.875rem;
  text-align: left;
  cursor: pointer;
  transition: background-color 0.1s;
}
:deep(.widget-row:hover) {
  background-color: var(--coar-background-neutral-tertiary, rgba(0, 0, 0, 0.04));
}
:deep(.widget-row--strong .widget-row__label) {
  font-weight: 600;
}
:deep(.widget-row__label) {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  overflow: hidden;
}
:deep(.widget-row__title) {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
:deep(.widget-row__sub) {
  flex-shrink: 0;
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
}
:deep(.widget-row__meta) {
  display: inline-flex;
  align-items: center;
  gap: 0.375rem;
  flex-shrink: 0;
  font-size: 0.75rem;
}
:deep(.widget-row__more) {
  padding: 0.25rem 0.5rem;
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #9ca3af);
}
</style>
