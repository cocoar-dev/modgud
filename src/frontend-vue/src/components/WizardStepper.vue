<script setup lang="ts">
import { computed } from 'vue'
import { CoarButton, CoarIcon } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

/**
 * Reusable, entity-agnostic wizard/stepper. Owns the step indicator, the
 * step body (one slot per step key), and a footer with Back / Next /
 * Finish, so it drops into any container (modal, page) without depending on
 * an ambient footer context.
 *
 * <para>Usage: bind <c>v-model</c> to the current step index, pass the
 * <c>steps</c> array (each step's <c>valid</c> flag gates "Next"), and
 * render one <c>#step-&lt;key&gt;</c> slot per step. The footer emits
 * <c>finish</c> on the last step and <c>cancel</c> from the ghost button.</para>
 */
export interface WizardStep {
  /** Stable key — also the slot name suffix (`#step-<key>`). */
  key: string
  /** Short label shown under the indicator node. */
  title: string
  /** When false, "Next"/"Finish" is disabled on this step. Default: true. */
  valid?: boolean
}

const props = withDefaults(defineProps<{
  modelValue: number
  steps: WizardStep[]
  submitting?: boolean
  finishLabel?: string
  nextLabel?: string
  backLabel?: string
  cancelLabel?: string
  showCancel?: boolean
}>(), {
  submitting: false,
  showCancel: true,
})

const emit = defineEmits<{
  (e: 'update:modelValue', value: number): void
  (e: 'finish'): void
  (e: 'cancel'): void
}>()

const { t } = useI18n()

const index = computed(() => Math.min(Math.max(props.modelValue, 0), props.steps.length - 1))
const currentStep = computed(() => props.steps[index.value])
const isFirst = computed(() => index.value === 0)
const isLast = computed(() => index.value === props.steps.length - 1)
const canAdvance = computed(() => currentStep.value?.valid !== false)

const nextText = computed(() =>
  isLast.value
    ? (props.finishLabel ?? t('common.finish', {}, 'Finish'))
    : (props.nextLabel ?? t('common.next', {}, 'Next')))

function isComplete(i: number): boolean {
  return i < index.value
}

function canJumpTo(target: number): boolean {
  if (target === index.value) return false
  if (target < index.value) return true // back is always allowed
  // Forward jump only if every step between here and the target is valid.
  for (let i = index.value; i < target; i++) {
    if (props.steps[i]?.valid === false) return false
  }
  return true
}

function goTo(target: number) {
  if (canJumpTo(target)) emit('update:modelValue', target)
}

function back() {
  if (!isFirst.value) emit('update:modelValue', index.value - 1)
}

function next() {
  if (!canAdvance.value) return
  if (isLast.value) emit('finish')
  else emit('update:modelValue', index.value + 1)
}
</script>

<template>
  <div class="wizard">
    <!-- Step indicator -->
    <ol class="wizard-steps" role="list">
      <li
        v-for="(step, i) in steps"
        :key="step.key"
        class="wizard-step"
        :class="{
          'is-active': i === index,
          'is-complete': isComplete(i),
          'is-clickable': canJumpTo(i),
        }"
      >
        <button
          type="button"
          class="wizard-step-node"
          :disabled="!canJumpTo(i) && i !== index"
          :aria-current="i === index ? 'step' : undefined"
          @click="goTo(i)"
        >
          <span class="wizard-step-marker">
            <CoarIcon v-if="isComplete(i)" name="check" size="s" />
            <template v-else>{{ i + 1 }}</template>
          </span>
          <span class="wizard-step-title">{{ step.title }}</span>
        </button>
        <span v-if="i < steps.length - 1" class="wizard-step-line" aria-hidden="true" />
      </li>
    </ol>

    <!-- Active step body -->
    <div class="wizard-body">
      <slot :name="`step-${currentStep?.key}`" :index="index" />
    </div>

    <!-- Footer nav — mirrors ModalLayout's footer (ghost / secondary / primary, right-aligned) -->
    <div class="wizard-footer">
      <div class="flex-1"></div>
      <div class="flex items-center gap-1">
        <CoarButton
          v-if="showCancel"
          variant="ghost"
          size="s"
          :disabled="submitting"
          @click="emit('cancel')"
        >
          {{ cancelLabel ?? t('common.cancel', {}, 'Cancel') }}
        </CoarButton>
        <CoarButton
          v-if="!isFirst"
          variant="secondary"
          size="s"
          :disabled="submitting"
          @click="back"
        >
          {{ backLabel ?? t('common.back', {}, 'Back') }}
        </CoarButton>
        <CoarButton
          variant="primary"
          size="s"
          :disabled="!canAdvance || submitting"
          :loading="isLast && submitting"
          @click="next"
        >
          {{ nextText }}
        </CoarButton>
      </div>
    </div>
  </div>
</template>

<style scoped>
.wizard {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  min-width: 0;
}

/* ── Step indicator ──────────────────────────────────────────────── */
.wizard-steps {
  display: flex;
  align-items: flex-start;
  list-style: none;
  margin: 0 0 16px 0;
  padding: 0;
  flex-shrink: 0;
}
.wizard-step {
  display: flex;
  align-items: center;
  flex: 1;
  min-width: 0;
}
.wizard-step-node {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  background: none;
  border: none;
  padding: 0;
  flex-shrink: 0;
  cursor: default;
  color: var(--coar-text-neutral-secondary, #6b7280);
}
.wizard-step.is-clickable .wizard-step-node {
  cursor: pointer;
}
.wizard-step-marker {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 50%;
  border: 2px solid var(--coar-border-neutral-secondary, #d1d5db);
  background: var(--coar-background-neutral-primary, #fff);
  font-size: 0.8rem;
  font-weight: 600;
  transition: background 0.15s, border-color 0.15s, color 0.15s;
}
.wizard-step-title {
  font-size: 0.72rem;
  font-weight: 500;
  text-align: center;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 9rem;
}
.wizard-step-line {
  flex: 1;
  height: 2px;
  margin: 0 8px;
  /* aligned to the marker centre (28px node, line sits at ~top 14px) */
  margin-bottom: 1.4rem;
  background: var(--coar-border-neutral-secondary, #e5e7eb);
  transition: background 0.15s;
}

/* Active */
.wizard-step.is-active .wizard-step-node { color: var(--coar-text-neutral-primary, #1f2937); }
.wizard-step.is-active .wizard-step-marker {
  border-color: var(--coar-accent, #1077be);
  color: var(--coar-accent, #1077be);
}
/* Complete */
.wizard-step.is-complete .wizard-step-marker {
  border-color: var(--coar-accent, #1077be);
  background: var(--coar-accent, #1077be);
  color: #fff;
}
.wizard-step.is-complete .wizard-step-line {
  background: var(--coar-accent, #1077be);
}

/* ── Body ────────────────────────────────────────────────────────── */
.wizard-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
}

/* ── Footer ──────────────────────────────────────────────────────── */
.wizard-footer {
  display: flex;
  align-items: center;
  flex-shrink: 0;
  margin: 12px -20px -20px; /* bleed to the modal-content edges */
  padding: 12px 20px;
  background: var(--coar-background-neutral-secondary, #f7f7f7);
  border-top: 1px solid #e9e9e9;
}
</style>
