<script setup lang="ts">
/**
 * AppBanner — scope-level advisory strip, pinned directly under a header.
 *
 * The counterpart to `AppNote`, and the distinction is SCOPE, not styling:
 *
 *  • `AppBanner` states something about EVERYTHING BELOW IT. It therefore
 *    belongs immediately under a header — the main header or a modal header —
 *    is full-bleed (no indent, no left accent bar, no rounding) so it reads as
 *    part of the frame rather than as content, and there is at most ONE per
 *    scope. It is not dismissible: it describes a state, so it goes away when
 *    the state does, not when the reader is annoyed by it.
 *  • `AppNote` belongs to a single field or section, is indented with a left
 *    accent bar, and several may appear in the same view.
 *
 * A banner placed a third of the way down a form would be lying about its own
 * reach; a note under the header would be too quiet for a statement about the
 * whole surface.
 *
 * Copy shape (borrowed from Cocoar.Atlas' storage banner, which is the pattern
 * this component was modelled on): a two-word bold `label`, then the
 * consequence, then the remedy — and, when there IS a remedy the reader can act
 * on, an `#action` in the trailing slot that leads straight to it.
 *
 * Mounting: render it OUTSIDE the scrolling content region, or it indents and
 * scrolls away. `ModalLayout` exposes a `#banner` slot that does this correctly.
 */
import { computed } from 'vue'
import { CoarIcon } from '@cocoar/vue-ui'

type Variant = 'info' | 'warning' | 'error' | 'success' | 'neutral'

const props = withDefaults(defineProps<{
  variant?: Variant
  /** Override the leading icon (lucide name). Defaults to a per-variant glyph. */
  icon?: string
  /**
   * Bold lead-in, rendered with a trailing colon — names the topic in two words
   * before the explanation starts. Omit for a plain sentence.
   */
  label?: string
}>(), {
  variant: 'info',
})

defineSlots<{
  default(): unknown
  /** Trailing call-to-action — a link/button leading to where the state is fixed. */
  action?(): unknown
}>()

const DEFAULT_ICON: Record<Variant, string> = {
  info: 'info',
  warning: 'alert-triangle',
  error: 'alert-circle',
  success: 'circle-check',
  neutral: 'info',
}

const iconName = computed(() => props.icon ?? DEFAULT_ICON[props.variant])
</script>

<template>
  <div class="app-banner" :class="`app-banner--${variant}`">
    <CoarIcon :name="iconName" size="s" class="app-banner__icon" aria-hidden="true" />
    <span class="app-banner__text">
      <strong v-if="label" class="app-banner__label">{{ label }}:</strong>
      <slot />
    </span>
    <span v-if="$slots.action" class="app-banner__action"><slot name="action" /></span>
  </div>
</template>

<style scoped>
.app-banner {
  display: flex;
  align-items: center;
  gap: var(--coar-spacing-s);
  flex-shrink: 0;
  /* Horizontal padding matches .modal-header / .modal-content (20px) so the
     text lines up with the content below; vertical padding stays tight — the
     banner is a frame element, not a card. */
  padding: 6px 20px;
  font-size: 0.8125rem; /* 13px — same register as AppNote */
  line-height: 1.4;
  /* Deliberately square and full-bleed: it belongs to the frame, and a rounded
     inset strip would read as content scoped to one section. */
  border-top: 1px solid var(--coar-banner-border);
  border-bottom: 1px solid var(--coar-banner-border);
  background-color: var(--coar-banner-bg);
  color: var(--coar-text-primary, #3f3f46);
}

.app-banner--info    { --coar-banner-bg: var(--coar-background-semantic-info-subtle);    --coar-banner-border: var(--coar-border-semantic-info-bold); }
.app-banner--warning { --coar-banner-bg: var(--coar-background-semantic-warning-subtle); --coar-banner-border: var(--coar-border-semantic-warning-bold); }
.app-banner--error   { --coar-banner-bg: var(--coar-background-semantic-error-subtle);   --coar-banner-border: var(--coar-border-semantic-error-bold); }
.app-banner--success { --coar-banner-bg: var(--coar-background-semantic-success-subtle); --coar-banner-border: var(--coar-border-semantic-success-bold); }
.app-banner--neutral { --coar-banner-bg: var(--coar-background-neutral-secondary);       --coar-banner-border: var(--coar-border-neutral-secondary); }

.app-banner__icon {
  flex: none;
  color: var(--coar-banner-border);
}

/* Wraps rather than truncates: a statement about the whole surface must stay
   readable. Two honest lines beat one ellipsised half-truth — which is exactly
   why this is NOT AppNote with its single-line + details-popover contract. */
.app-banner__text {
  flex: 1;
  min-width: 0;
}

.app-banner__label {
  font-weight: 600;
  margin-right: 0.25em;
}

.app-banner__action {
  flex: none;
  font-weight: 600;
  white-space: nowrap;
}
</style>
