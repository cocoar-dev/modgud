<script setup lang="ts">
/**
 * ADR 0019 — one editor for the multi-dimensional auth rate limits, used by the realm
 * settings (baseline = shipped defaults) and by the Application settings (baseline =
 * the realm's effective values). The model is SPARSE: a cell is either `null`
 * (inherit the baseline) or an explicit rule. Dimensions that do not apply to a policy
 * (no baseline) are rendered as "—" and can never be set.
 */
import { computed } from 'vue'
import { CoarCheckbox, CoarTextInput } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import type { PolicyLimitsDto, RateLimitRuleDto } from '@/models/realmSettings'
import {
  RATE_LIMIT_DIMENSIONS, RATE_LIMIT_POLICIES,
  type RateLimitDimensionKey, type RateLimitOverrides,
} from '@/models/realmSettings'

const props = withDefaults(defineProps<{
  modelValue: RateLimitOverrides
  /** What "inherit" resolves to, per policy and dimension. */
  baseline: Record<string, PolicyLimitsDto>
  disabled?: boolean
}>(), { disabled: false })

const emit = defineEmits<{ (e: 'update:modelValue', v: RateLimitOverrides): void }>()
const { t } = useI18n()

const dimensionLabel = (d: RateLimitDimensionKey) => t(`admin.rateLimits.dimension.${d}`, {}, RATE_LIMIT_DIMENSIONS.find((x) => x.key === d)?.fallback ?? d)

function baselineOf(policy: string, dim: RateLimitDimensionKey): RateLimitRuleDto | null {
  return (props.baseline[policy]?.[dim] as RateLimitRuleDto | null | undefined) ?? null
}
function ruleOf(policy: string, dim: RateLimitDimensionKey): RateLimitRuleDto | null {
  return props.modelValue[policy]?.[dim] ?? null
}
function isOverridden(policy: string, dim: RateLimitDimensionKey) {
  return ruleOf(policy, dim) !== null
}
/** ADR 0020: a signal-only cell (the login spray threshold) can be tuned but never switched off. */
function isSignalOnly(policy: string, dim: RateLimitDimensionKey) {
  return baselineOf(policy, dim)?.SignalOnly === true
}

function set(policy: string, dim: RateLimitDimensionKey, rule: RateLimitRuleDto | null) {
  const row = props.modelValue[policy] ?? { Source: null, SourceRegistration: null, Target: null, Client: null, App: null, Device: null }
  const next: RateLimitOverrides = { ...props.modelValue, [policy]: { ...row, [dim]: rule } }
  emit('update:modelValue', next)
}
function toggle(policy: string, dim: RateLimitDimensionKey, on: boolean) {
  if (!on) { set(policy, dim, null); return }
  const base = baselineOf(policy, dim)
  set(policy, dim, base ? { ...base } : { PermitLimit: 1, WindowMinutes: 60, Burst: null, Enabled: true })
}
function patch(policy: string, dim: RateLimitDimensionKey, part: Partial<RateLimitRuleDto>) {
  const cur = ruleOf(policy, dim)
  if (!cur) return
  set(policy, dim, { ...cur, ...part })
}
function int(v: string, min = 1): number {
  const n = parseInt(v, 10)
  return Number.isFinite(n) ? Math.max(min, n) : min
}

const summary = (r: RateLimitRuleDto | null) => {
  if (!r) return '—'
  if (r.Enabled === false) return t('admin.rateLimits.off', {}, 'off')
  const base = `${r.PermitLimit} / ${r.WindowMinutes} min`
  const withBurst = r.Burst ? `${base} · ${t('admin.rateLimits.burstShort', {}, 'burst')} ${r.Burst}` : base
  return r.SignalOnly ? `${withBurst} · ${t('admin.rateLimits.signalOnly', {}, 'signal only')}` : withBurst
}

const policies = computed(() => RATE_LIMIT_POLICIES.filter((p) => props.baseline[p.key]))
</script>

<template>
  <div class="rl-editor">
    <div class="rl-editor__scroll">
      <table class="rl-table">
        <thead>
          <tr>
            <th class="rl-table__policy">{{ t('admin.rateLimits.flow', {}, 'Flow') }}</th>
            <th v-for="d in RATE_LIMIT_DIMENSIONS" :key="d.key">
              <span>{{ dimensionLabel(d.key) }}</span>
              <small>{{ t(`admin.rateLimits.dimensionHint.${d.key}`, {}, d.hint) }}</small>
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="p in policies" :key="p.key">
            <th class="rl-table__policy" scope="row">
              <span>{{ t(p.labelKey, {}, p.fallback) }}</span>
              <small>{{ p.key }}</small>
            </th>
            <td v-for="d in RATE_LIMIT_DIMENSIONS" :key="d.key" :class="{ 'rl-cell--na': !baselineOf(p.key, d.key) }">
              <template v-if="baselineOf(p.key, d.key)">
                <CoarCheckbox
                  :model-value="isOverridden(p.key, d.key)"
                  :disabled="disabled"
                  :label="isOverridden(p.key, d.key) ? t('admin.rateLimits.override', {}, 'Override') : summary(baselineOf(p.key, d.key))"
                  :aria-label="`${t(p.labelKey, {}, p.fallback)} – ${dimensionLabel(d.key)}`"
                  @update:model-value="(v: boolean) => toggle(p.key, d.key, v)" />
                <div v-if="isOverridden(p.key, d.key)" class="rl-cell__inputs">
                  <label class="rl-field">
                    <span>{{ t('admin.rateLimits.limit', {}, 'Max.') }}</span>
                    <CoarTextInput class="rl-num" :model-value="String(ruleOf(p.key, d.key)!.PermitLimit)" :disabled="disabled"
                      @update:model-value="(v: string) => patch(p.key, d.key, { PermitLimit: int(v) })" />
                  </label>
                  <label class="rl-field">
                    <span>{{ t('admin.rateLimits.window', {}, 'Window (min)') }}</span>
                    <CoarTextInput class="rl-num" :model-value="String(ruleOf(p.key, d.key)!.WindowMinutes)" :disabled="disabled"
                      @update:model-value="(v: string) => patch(p.key, d.key, { WindowMinutes: int(v) })" />
                  </label>
                  <label v-if="d.key === 'Source'" class="rl-field">
                    <span>{{ t('admin.rateLimits.burst', {}, 'Burst') }}</span>
                    <CoarTextInput class="rl-num" :model-value="ruleOf(p.key, d.key)!.Burst ? String(ruleOf(p.key, d.key)!.Burst) : ''" :disabled="disabled"
                      placeholder="—"
                      @update:model-value="(v: string) => patch(p.key, d.key, { Burst: v.trim() ? int(v) : null })" />
                  </label>
                  <CoarCheckbox
                    v-if="!isSignalOnly(p.key, d.key)"
                    :model-value="ruleOf(p.key, d.key)!.Enabled !== false"
                    :disabled="disabled"
                    :label="t('admin.rateLimits.enabled', {}, 'Active')"
                    @update:model-value="(v: boolean) => patch(p.key, d.key, { Enabled: v })" />
                  <small v-else class="rl-signal">{{ t('admin.rateLimits.signalOnlyHint', {}, 'Signal only: counted and reported, never rejects.') }}</small>
                </div>
              </template>
              <span v-else class="rl-cell__na">—</span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<style scoped>
.rl-editor {
  border: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  border-radius: var(--coar-radius-m, 4px);
  overflow: hidden;
  /* Inside a flex-column modal body the editor must keep its natural height and let the
     body scroll — otherwise flex-shrink + overflow:hidden squeeze the rows away. */
  flex: none;
}
.rl-editor__scroll { overflow-x: auto; }
.rl-table { width: 100%; min-width: 64rem; border-collapse: collapse; font-size: 0.8125rem; }
.rl-table th, .rl-table td {
  padding: 0.45rem 0.6rem;
  vertical-align: top;
  border-bottom: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  text-align: left;
}
.rl-table thead th {
  background: var(--coar-background-neutral-secondary, #f8fafc);
  color: var(--coar-text-neutral-secondary, #525e76);
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.rl-table thead th small, .rl-table__policy small {
  display: block;
  font-weight: 400;
  text-transform: none;
  letter-spacing: 0;
  color: var(--coar-text-neutral-tertiary, #7b8497);
  font-size: 0.7rem;
}
.rl-table__policy { width: 14rem; font-weight: 500; }
.rl-cell--na { color: var(--coar-text-neutral-tertiary, #7b8497); }
.rl-cell__na { display: inline-block; padding-top: 0.35rem; }
.rl-cell__inputs { display: flex; flex-wrap: wrap; gap: 0.4rem 0.6rem; margin-top: 0.35rem; align-items: flex-end; }
.rl-field { display: flex; flex-direction: column; gap: 0.15rem; font-size: 0.7rem; color: var(--coar-text-neutral-secondary, #525e76); }
.rl-num { width: 5.5rem; }
.rl-signal { flex-basis: 100%; color: var(--coar-text-neutral-tertiary, #7b8497); font-size: 0.7rem; }
</style>
