<script setup lang="ts">
/**
 * Color input: a hex text field paired with a swatch that doubles as a live
 * preview and an OS color-picker trigger, plus inline hex validation (UI/UX
 * wave 3, findings #39/#49). Drop-in replacement for a raw hex CoarTextInput
 * inside a CoarFormField.
 *
 * Empty is treated as VALID — the parent decides whether the field is required.
 */
import { computed } from 'vue'
import { CoarTextInput } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

const props = defineProps<{
  modelValue: string
  placeholder?: string
  disabled?: boolean
}>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()

const { t } = useI18n()

const value = computed({
  get: () => props.modelValue ?? '',
  set: (v: string) => emit('update:modelValue', v),
})

// Accept any valid CSS color (hex, rgb(), or a named colour like "rebeccapurple")
// — the field is documented as a CSS color. Empty is valid; the parent decides
// requiredness. `CSS.supports` is the browser's own colour parser.
function cssColorOk(v: string): boolean {
  return typeof CSS !== 'undefined' && CSS.supports ? CSS.supports('color', v) : /^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/.test(v)
}
const isValid = computed(() => {
  const v = value.value.trim()
  return v === '' || cssColorOk(v)
})

// Background for the preview swatch — the raw value when it's a usable colour.
const previewBg = computed(() => {
  const v = value.value.trim()
  return v !== '' && cssColorOk(v) ? v : ''
})

// The native <input type="color"> only accepts #rrggbb; expand a #rgb shorthand,
// else default — it's only the picker's starting point, the typed value wins.
const nativeColor = computed(() => {
  const v = value.value.trim()
  if (/^#[0-9a-fA-F]{6}$/.test(v)) return v
  if (/^#[0-9a-fA-F]{3}$/.test(v)) {
    return '#' + v.slice(1).split('').map((c) => c + c).join('')
  }
  return '#000000'
})

function onPick(e: Event) {
  emit('update:modelValue', (e.target as HTMLInputElement).value)
}
</script>

<template>
  <div class="color-field">
    <div class="color-field__row">
      <CoarTextInput
        v-model="value"
        :placeholder="placeholder ?? '#5A6478'"
        :disabled="disabled"
        clearable
        class="color-field__hex"
      />
      <label
        class="color-field__swatch"
        :class="{ 'color-field__swatch--empty': !previewBg, 'color-field__swatch--invalid': !isValid, 'color-field__swatch--disabled': disabled }"
        :style="previewBg ? { background: previewBg } : undefined"
        :title="t('common.colorPicker', {}, 'Pick a color')"
      >
        <input
          type="color"
          class="color-field__native"
          :value="nativeColor"
          :disabled="disabled"
          @input="onPick"
        />
      </label>
    </div>
    <span v-if="!isValid" class="color-field__error">
      {{ t('common.colorInvalid', {}, 'Enter a valid color, e.g. #5A6478.') }}
    </span>
  </div>
</template>

<style scoped>
.color-field {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.color-field__row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.color-field__hex {
  max-width: 9rem;
}
.color-field__swatch {
  position: relative;
  flex-shrink: 0;
  width: 28px;
  height: 28px;
  border-radius: var(--coar-radius-m, 4px);
  border: 1px solid var(--coar-border-neutral-secondary, #d1d5db);
  cursor: pointer;
  overflow: hidden;
}
/* Empty / no usable colour yet → a neutral checker-ish chip. */
.color-field__swatch--empty {
  background:
    linear-gradient(45deg, #e5e7eb 25%, transparent 25%, transparent 75%, #e5e7eb 75%),
    linear-gradient(45deg, #e5e7eb 25%, #fff 25%, #fff 75%, #e5e7eb 75%);
  background-size: 12px 12px;
  background-position: 0 0, 6px 6px;
}
.color-field__swatch--invalid {
  border-color: var(--coar-text-semantic-error, #dc2626);
}
.color-field__swatch--disabled {
  cursor: not-allowed;
  opacity: 0.5;
}
/* The native picker is the click target but visually hidden behind the swatch. */
.color-field__native {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  opacity: 0;
  cursor: inherit;
  border: none;
  padding: 0;
}
.color-field__error {
  font-size: 0.75rem;
  color: var(--coar-text-semantic-error, #dc2626);
}
</style>
