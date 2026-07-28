<script setup lang="ts">
/**
 * Notice — a short, non-blocking advisory. One component, two placements; what
 * differs is REACH, and reach is communicated by POSITION, not by decoration.
 *
 *  • `placement="inline"` (default) — belongs to the field or section it sits
 *    next to. Several may appear in one view. Sits in the content flow with a
 *    hairline border and a small radius.
 *  • `placement="banner"` — states something about EVERYTHING BELOW IT, so it
 *    is pinned directly under a header (the main header or a modal header),
 *    full-bleed with a rule along the bottom only, and there is at most ONE per
 *    scope. Not dismissible: it describes a state, so it goes when the state
 *    goes, not when the reader is annoyed by it. `ModalLayout` exposes a
 *    `#banner` slot that mounts it correctly — outside the scrolling region, or
 *    it would indent and scroll away.
 *
 * Everything else — fill, tinted text, icon, 13px, the bold `label` lead-in —
 * is deliberately identical between the two. A banner a third of the way down a
 * form would be lying about its reach; a note under the header would be too
 * quiet for a statement about the whole surface. Position carries that, and
 * nothing else has to.
 *
 * Copy shape: a two-word bold `label`, then the consequence, then the remedy —
 * borrowed from the storage banner in Cocoar.Atlas. Anything longer than a
 * sentence belongs in `#details` (a popover), not on the strip.
 *
 * Intended to move into @cocoar/vue-ui once it has proven itself here, so it is
 * kept free of app coupling: only Coar design tokens, CoarIcon and CoarPopover.
 * The single thing a library port must change is the i18n key of the details
 * button — `common.details` is an app key; the library namespace would be
 * `coar.ui.notice.details`.
 */
import { computed } from 'vue'
import { CoarIcon, CoarPopover } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

type Variant = 'info' | 'warning' | 'error' | 'success' | 'neutral' | 'accent'

const props = withDefaults(defineProps<{
  variant?: Variant
  /** Override the leading icon (lucide name). Defaults to a per-variant glyph. */
  icon?: string
  /**
   * Bold lead-in, rendered with a trailing colon — names the topic in two words
   * before the explanation starts. Omit for a plain sentence.
   */
  label?: string
  /** Inline (next to a field/section) or pinned under a header. */
  placement?: 'inline' | 'banner'
  /**
   * Clamp to a single line with an ellipsis. Defaults to OFF: of the 105
   * call-sites this component inherited, 82 were switching truncation off, so
   * wrapping is the real default and clamping is the exception. Ignored for
   * `placement="banner"` — a statement about the whole surface must stay
   * readable, and two honest lines beat one ellipsised half-truth.
   */
  truncate?: boolean
}>(), {
  variant: 'info',
  placement: 'inline',
  truncate: false,
})

const slots = defineSlots<{
  default(): unknown
  /** The long version, behind a "Details" popover. */
  details?(): unknown
  /** Trailing call-to-action — leads to where the state gets fixed. */
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
const isBanner = computed(() => props.placement === 'banner')
const clamps = computed(() => props.truncate && !isBanner.value)
const hasDetails = computed(() => !!slots.details)
const hasCta = computed(() => !!slots.cta)
</script>

<template>
  <div
    class="notice"
    :class="[`notice--${variant}`, isBanner ? 'notice--banner' : 'notice--inline', { 'notice--clamp': clamps }]"
  >
    <CoarIcon :name="iconName" size="s" class="notice__icon" aria-hidden="true" />
    <span class="notice__text">
      <strong v-if="label" class="notice__label">{{ label }}:</strong>
      <slot />
    </span>

    <span v-if="hasCta" class="notice__cta"><slot name="cta" /></span>

    <CoarPopover v-if="hasDetails" mode="click">
      <button type="button" class="notice__details" :aria-label="t('common.details', {}, 'Details')">
        {{ t('common.details', {}, 'Details') }}
      </button>
      <template #content>
        <div class="notice__popover"><slot name="details" /></div>
      </template>
    </CoarPopover>
  </div>
</template>

<style scoped>
.notice {
  display: flex;
  align-items: flex-start;
  gap: var(--coar-spacing-s);
  flex-shrink: 0;
  font-size: 0.8125rem; /* 13px — deliberately below the 16px body */
  line-height: 1.4;
  background-color: var(--coar-notice-bg);
  /* Text tinted in the notice's own hue rather than neutral grey — the tint is
     what makes the strip read as one calm surface instead of grey text sitting
     on a coloured patch. Uses the `-bold` BORDER token, not
     `--coar-text-semantic-*-bold`: the latter is #ffffff for the info variant
     and would be invisible here. */
  color: var(--coar-notice-fg);
}

/* Sits in the content flow: hairline all round, slight radius. NOT the old
   3px near-black left bar — that read as a heavy rule bolted onto the text. */
.notice--inline {
  padding: 6px 10px;
  border: 1px solid var(--coar-notice-border);
  border-radius: var(--coar-radius-xs, 2px);
}

/* Pinned under a header: full-bleed, square, and a rule along the bottom only.
   Nothing at the top — the header's own shadow separates. Horizontal padding
   matches .modal-header / .modal-content (20px) so the text lines up with the
   content below. */
.notice--banner {
  align-items: center;
  padding: 6px 20px;
  border-bottom: 1px solid var(--coar-notice-border);
}

/*
 * Colour, derived rather than picked.
 *
 * The `-subtle` background token alone is a fairly deep peach (L 0.92 / C 0.04
 * for warning) and the `-bold` border token is near-black-brown (L 0.29 /
 * C 0.06), which together read as grey text on a coloured patch. The reference
 * banner in Cocoar.Atlas is the opposite balance: a very pale ground (L 0.99)
 * carrying mid-dark, SATURATED text (L 0.47 / C 0.13) — that saturation is what
 * makes it read as "dark yellow" rather than "dark brown".
 *
 * So the ground is lightened and desaturated RELATIVELY off the design-system
 * token — `calc(l + …)`, never a literal lightness. The tokens themselves flip
 * for dark mode (warning bg goes L 0.92 → L 0.34), and a hard-coded L would
 * have produced a glaring near-white strip on a dark UI.
 *
 * The foreground is a straight token swap rather than a derivation:
 * `--coar-background-semantic-warning-bold` is #8f5300, which is within a
 * whisker of the reference's #92400e. It cannot be used in dark mode though —
 * there the ground is already dark (L 0.34) and this ink is only L 0.5, so the
 * pairing collapses; dark mode takes the `-bold` BORDER token (#cc821f, L 0.67)
 * instead. Hence the one `.dark-mode` override below.
 *
 * neutral/accent stay on plain tokens: their sources are greys, and forcing a
 * chroma onto an achromatic hue tints them unpredictably.
 */
.notice--info,
.notice--warning,
.notice--error,
.notice--success {
  --coar-notice-bg: oklch(from var(--coar-notice-tint) calc(l + 0.06) calc(c - 0.018) h);
  --coar-notice-border: oklch(from var(--coar-notice-line) calc(l + 0.05) c h);
  --coar-notice-fg: var(--coar-notice-ink);
}

.notice--info    { --coar-notice-tint: var(--coar-background-semantic-info-subtle);    --coar-notice-line: var(--coar-border-semantic-info-subtle);    --coar-notice-ink: var(--coar-background-semantic-info-bold); }
.notice--warning { --coar-notice-tint: var(--coar-background-semantic-warning-subtle); --coar-notice-line: var(--coar-border-semantic-warning-subtle); --coar-notice-ink: var(--coar-background-semantic-warning-bold); }
.notice--error   { --coar-notice-tint: var(--coar-background-semantic-error-subtle);   --coar-notice-line: var(--coar-border-semantic-error-subtle);   --coar-notice-ink: var(--coar-background-semantic-error-bold); }
.notice--success { --coar-notice-tint: var(--coar-background-semantic-success-subtle); --coar-notice-line: var(--coar-border-semantic-success-subtle); --coar-notice-ink: var(--coar-background-semantic-success-bold); }

.notice--neutral { --coar-notice-bg: var(--coar-background-neutral-secondary); --coar-notice-border: var(--coar-border-neutral-secondary); --coar-notice-fg: var(--coar-text-neutral-primary); }
.notice--accent  { --coar-notice-bg: var(--coar-background-accent-tertiary);   --coar-notice-border: var(--coar-border-accent-primary);    --coar-notice-fg: var(--coar-text-neutral-primary); }

.notice__icon {
  flex: none;
  color: var(--coar-notice-fg);
  margin-top: 0.1rem;
}
.notice--banner .notice__icon { margin-top: 0; }

.notice__text {
  flex: 1;
  min-width: 0;
}

.notice__label {
  font-weight: 600;
  margin-right: 0.25em;
}

/* Single-line clamp — the exception, for short asides where the full story
   lives in #details. */
.notice--clamp {
  align-items: center;
}
.notice--clamp .notice__icon { margin-top: 0; }
.notice--clamp .notice__text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.notice__cta {
  flex: none;
  font-weight: 600;
  white-space: nowrap;
}

.notice__details {
  flex: none;
  align-self: flex-start;
  border: 1px solid var(--coar-notice-border);
  background: transparent;
  color: var(--coar-notice-fg);
  font: inherit;
  font-size: 0.75rem;
  font-weight: 600;
  border-radius: var(--coar-radius-xs, 2px);
  padding: 2px var(--coar-spacing-s);
  cursor: pointer;
  transition: background-color 0.12s ease-out, color 0.12s ease-out;
}
.notice__details:hover {
  background: var(--coar-notice-fg);
  color: var(--coar-background-neutral-primary, #fff);
}

.notice__popover {
  max-width: 340px;
  font-size: 0.8125rem;
  line-height: 1.5;
  color: var(--coar-text-secondary, #52525b);
}
</style>

<!--
  Dark mode, deliberately in an UNSCOPED block.

  It has to reach an ancestor (`.dark-mode` lives on <html>), and the scoped
  compiler mangles that: `:global(.dark-mode) .notice--warning` compiles down to
  plain `.dark-mode`, silently dropping the variant — which would have put the
  LAST variant's ink on the root element and tinted every notice in dark mode
  with it. Leaking is not a real risk here: every selector is a `.notice--*`
  class that exists nowhere else.

  Dark mode reverts to the plain tokens. The lighten-and-desaturate treatment
  above is tuned against a light reference, and the tokens already invert for
  dark (the warning ground drops from L 0.92 to L 0.34) — lightening on top of
  that only eats the contrast the dark palette had budgeted.
-->
<style>
.dark-mode .notice--info,
.dark-mode .notice--warning,
.dark-mode .notice--error,
.dark-mode .notice--success {
  --coar-notice-bg: var(--coar-notice-tint);
  --coar-notice-border: var(--coar-notice-line);
}
.dark-mode .notice--info    { --coar-notice-ink: var(--coar-border-semantic-info-bold); }
.dark-mode .notice--warning { --coar-notice-ink: var(--coar-border-semantic-warning-bold); }
.dark-mode .notice--error   { --coar-notice-ink: var(--coar-border-semantic-error-bold); }
.dark-mode .notice--success { --coar-notice-ink: var(--coar-border-semantic-success-bold); }
</style>
