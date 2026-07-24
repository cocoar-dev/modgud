<script setup lang="ts">
/**
 * AppNote — compact, single-line advisory strip.
 *
 * Replaces the tall `CoarNote` block for the common "a short heads-up plus,
 * optionally, the full story on demand" case. One line (13px), variant tint +
 * left accent (same design-system tokens as CoarNote), a leading semantic icon,
 * an optional trailing CTA and an optional `Details` popover that carries the
 * long text — so the surface stays one line and never grows to five.
 *
 * Copy rule: the default slot is ONE short sentence. Anything longer belongs in
 * the `#details` slot (popover), not on the strip.
 */
import { computed } from 'vue'
import { CoarIcon, CoarPopover } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

type Variant = 'info' | 'warning' | 'error' | 'success' | 'neutral' | 'accent'

const props = withDefaults(defineProps<{
  variant?: Variant
  /** Override the leading icon (lucide name). Defaults to a per-variant glyph. */
  icon?: string
  /** Single-line with ellipsis (default). Set false to allow wrapping. */
  truncate?: boolean
}>(), {
  variant: 'info',
  truncate: true,
})

const slots = defineSlots<{
  default(): unknown
  details?(): unknown
  cta?(): unknown
}>()

const { t } = useI18n()

const DEFAULT_ICON: Record<Variant, string> = {
  info: 'info',
  warning: 'alert-triangle',
  error: 'alert-circle',
  success: 'circle-check',
  neutral: 'info',
  accent: 'info',
}

const iconName = computed(() => props.icon ?? DEFAULT_ICON[props.variant])
const hasDetails = computed(() => !!slots.details)
const hasCta = computed(() => !!slots.cta)
</script>

<template>
  <div class="app-note" :class="[`app-note--${variant}`, { 'app-note--wrap': !truncate }]">
    <CoarIcon :name="iconName" size="s" class="app-note__icon" aria-hidden="true" />
    <span class="app-note__text"><slot /></span>

    <span v-if="hasCta" class="app-note__cta"><slot name="cta" /></span>

    <CoarPopover v-if="hasDetails" mode="click">
      <button type="button" class="app-note__details" :aria-label="t('common.details', {}, 'Details')">
        {{ t('common.details', {}, 'Details') }}
      </button>
      <template #content>
        <div class="app-note__popover"><slot name="details" /></div>
      </template>
    </CoarPopover>
  </div>
</template>

<style scoped>
.app-note {
  display: flex;
  align-items: center;
  gap: var(--coar-spacing-s);
  flex-shrink: 0;
  padding: var(--coar-spacing-s) var(--coar-spacing-m);
  font-size: 0.8125rem; /* 13px — deliberately below the 16px body */
  line-height: 1.4;
  border-radius: 0 var(--coar-radius-xs) var(--coar-radius-xs) 0;
  border-left: 3px solid var(--coar-note-border-color);
  background-color: var(--coar-note-bg);
  color: var(--coar-text-primary, #3f3f46);
}

.app-note--info    { --coar-note-bg: var(--coar-background-semantic-info-subtle);    --coar-note-border-color: var(--coar-border-semantic-info-bold); }
.app-note--warning { --coar-note-bg: var(--coar-background-semantic-warning-subtle); --coar-note-border-color: var(--coar-border-semantic-warning-bold); }
.app-note--error   { --coar-note-bg: var(--coar-background-semantic-error-subtle);   --coar-note-border-color: var(--coar-border-semantic-error-bold); }
.app-note--success { --coar-note-bg: var(--coar-background-semantic-success-subtle); --coar-note-border-color: var(--coar-border-semantic-success-bold); }
.app-note--neutral { --coar-note-bg: var(--coar-background-neutral-secondary);       --coar-note-border-color: var(--coar-border-neutral-secondary); }
.app-note--accent  { --coar-note-bg: var(--coar-background-accent-tertiary);         --coar-note-border-color: var(--coar-border-accent-primary); }

.app-note__icon {
  flex: none;
  color: var(--coar-note-border-color);
}

.app-note__text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* Multi-line / rich content: align the icon to the first line and let block
   content (lists, secret panels, buttons) flow naturally instead of being
   vertically centred against a tall body. */
.app-note--wrap {
  align-items: flex-start;
}
.app-note--wrap .app-note__icon {
  margin-top: 0.1rem;
}
.app-note--wrap .app-note__text {
  overflow: visible;
  white-space: normal;
}

.app-note__cta {
  flex: none;
  font-weight: 600;
  white-space: nowrap;
}

.app-note__details {
  flex: none;
  border: 1px solid var(--coar-note-border-color);
  background: transparent;
  color: var(--coar-note-border-color);
  font: inherit;
  font-size: 0.75rem;
  font-weight: 600;
  border-radius: var(--coar-radius-xs);
  padding: 2px var(--coar-spacing-s);
  cursor: pointer;
  transition: background-color 0.12s ease-out, color 0.12s ease-out;
}
.app-note__details:hover {
  background: var(--coar-note-border-color);
  color: var(--coar-background-neutral-primary, #fff);
}

.app-note__popover {
  max-width: 340px;
  font-size: 0.8125rem;
  line-height: 1.5;
  color: var(--coar-text-secondary, #52525b);
}
</style>
