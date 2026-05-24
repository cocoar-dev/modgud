<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  CoarButton,
  CoarFormField,
  CoarTextInput,
  CoarCheckbox,
  CoarTag,
  CoarSpinner,
  useToast,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useScheduledJobStore } from '@/stores/scheduledJob.store'
import type { ScheduledJobDto, ScheduledJobHistoryDto, ScheduledJobUpdateDto } from '@/models/ScheduledJob'

const { t } = useI18n()
const store = useScheduledJobStore()
const toast = useToast()

// Routed-fragment modals receive the path param as a prop. The :id slot
// in the router carries the job's Key (e.g. "dcr-gc").
const props = defineProps<{ id: string; close: (result?: unknown) => void }>()
const jobKey = computed(() => props.id)

const job = ref<ScheduledJobDto | null>(null)
const history = ref<ScheduledJobHistoryDto[]>([])
const loading = ref(false)
const saving = ref(false)
const triggering = ref(false)

// Editable form state (initialised from `job` on load).
const cronOverride = ref<string>('')
const enabled = ref<boolean>(true)
const paramValues = ref<Record<string, unknown>>({})

async function load() {
  loading.value = true
  try {
    const [j, h] = await Promise.all([
      store.loadOne(jobKey.value),
      store.loadHistory(jobKey.value, 20),
    ])
    job.value = j
    history.value = h
    if (j) {
      cronOverride.value = j.HasOverride ? j.EffectiveCron : ''
      enabled.value = j.Enabled
      // Start from persisted params, fall back to schema defaults for missing keys.
      const seed: Record<string, unknown> = {}
      for (const f of j.ParameterSchema) {
        seed[f.Key] = j.Parameters[f.Key] ?? f.Default ?? null
      }
      paramValues.value = seed
    }
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(jobKey, load)

async function save() {
  if (!job.value) return
  saving.value = true
  try {
    const dto: ScheduledJobUpdateDto = {
      // Empty string = clear override (use registration default cron).
      CronOverride: cronOverride.value.trim() === '' ? null : cronOverride.value.trim(),
      Enabled: enabled.value,
      Parameters: paramValues.value,
    }
    await store.update(jobKey.value, dto)
    toast.success(t('admin.scheduledJobs.savedToast', {}, 'Job configuration saved'))
    await load()
  } catch (e: any) {
    toast.error(e?.message ?? String(e))
  } finally {
    saving.value = false
  }
}

async function trigger() {
  triggering.value = true
  try {
    await store.triggerNow(jobKey.value)
    toast.success(t('admin.scheduledJobs.triggeredToast', {}, 'Job triggered — refreshing in a moment'))
    // Quick refresh so the new history entry shows up.
    setTimeout(load, 1500)
  } catch (e: any) {
    toast.error(e?.message ?? String(e))
  } finally {
    triggering.value = false
  }
}

function fmtDuration(ms: number): string {
  if (ms < 1000) return `${ms} ms`
  const s = ms / 1000
  return s < 60 ? `${s.toFixed(2)} s` : `${(s / 60).toFixed(1)} min`
}

function fmtDate(iso: string | null | undefined): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleString()
}
</script>

<template>
  <ModalLayout
    :title="job?.Name ?? jobKey"
    :subtitle="job?.Description ?? undefined"
    icon="clock"
  >
    <div v-if="loading && !job" class="loading-block">
      <CoarSpinner />
    </div>

    <div v-else-if="!job" class="empty-block">
      {{ t('admin.scheduledJobs.notFound', {}, 'Job not registered (the backend may have been redeployed without it).') }}
    </div>

    <div v-else class="details">
      <!-- ── Configuration ───────────────────────────────────────── -->
      <section class="card">
        <h3>{{ t('admin.scheduledJobs.section.config', {}, 'Configuration') }}</h3>

        <div class="grid-2">
          <CoarFormField :label="t('admin.scheduledJobs.cron', {}, 'Cron')">
            <CoarTextInput
              v-model="cronOverride"
              :placeholder="job.DefaultCron"
            />
            <template #help>
              {{ t('admin.scheduledJobs.cronHelp', {}, 'Leave blank to use the registration default. Quartz cron format (7 fields).') }}
            </template>
          </CoarFormField>

          <CoarFormField :label="t('admin.scheduledJobs.enabled', {}, 'Enabled')">
            <CoarCheckbox v-model="enabled" />
          </CoarFormField>
        </div>

        <div v-if="job.ParameterSchema.length > 0" class="params">
          <h4>{{ t('admin.scheduledJobs.parameters', {}, 'Parameters') }}</h4>
          <div class="grid-2">
            <CoarFormField
              v-for="field in job.ParameterSchema"
              :key="field.Key"
              :label="field.Label"
            >
              <CoarTextInput
                v-if="field.Type === 'Number' || field.Type === 'String'"
                :model-value="paramValues[field.Key] == null ? '' : String(paramValues[field.Key])"
                :placeholder="field.Placeholder"
                @update:model-value="(v: string) => {
                  if (v.trim() === '') paramValues[field.Key] = null
                  else if (field.Type === 'Number') {
                    const n = Number(v)
                    paramValues[field.Key] = Number.isNaN(n) ? v : n
                  } else paramValues[field.Key] = v
                }"
              />
              <CoarCheckbox
                v-else-if="field.Type === 'Boolean'"
                :model-value="Boolean(paramValues[field.Key])"
                @update:model-value="(v: boolean) => paramValues[field.Key] = v"
              />
              <template v-if="field.Description" #help>{{ field.Description }}</template>
            </CoarFormField>
          </div>
        </div>

        <div class="actions">
          <CoarButton variant="primary" :loading="saving" @click="save">
            {{ t('admin.scheduledJobs.save', {}, 'Save') }}
          </CoarButton>
          <CoarButton variant="secondary" icon-start="play" :loading="triggering" @click="trigger">
            {{ t('admin.scheduledJobs.triggerNow', {}, 'Trigger now') }}
          </CoarButton>
        </div>
      </section>

      <!-- ── Schedule overview ───────────────────────────────────── -->
      <section class="card">
        <h3>{{ t('admin.scheduledJobs.section.schedule', {}, 'Schedule') }}</h3>
        <dl class="kv">
          <dt>{{ t('admin.scheduledJobs.key', {}, 'Key') }}</dt>
          <dd><code>{{ job.Key }}</code></dd>
          <dt>{{ t('admin.scheduledJobs.defaultCron', {}, 'Default cron') }}</dt>
          <dd><code>{{ job.DefaultCron }}</code></dd>
          <dt>{{ t('admin.scheduledJobs.effectiveCron', {}, 'Effective cron') }}</dt>
          <dd><code>{{ job.EffectiveCron }}</code></dd>
          <dt>{{ t('admin.scheduledJobs.nextFire', {}, 'Next fire') }}</dt>
          <dd>{{ fmtDate(job.NextFireAt) }}</dd>
        </dl>
      </section>

      <!-- ── Run history ─────────────────────────────────────────── -->
      <section class="card">
        <h3>
          {{ t('admin.scheduledJobs.section.history', {}, 'Run history') }}
          <span class="count">({{ history.length }})</span>
        </h3>
        <div v-if="history.length === 0" class="empty-block">
          {{ t('admin.scheduledJobs.noHistory', {}, 'No runs recorded yet.') }}
        </div>
        <table v-else class="history">
          <thead>
            <tr>
              <th>{{ t('admin.scheduledJobs.history.startedAt', {}, 'Started') }}</th>
              <th>{{ t('admin.scheduledJobs.history.duration', {}, 'Duration') }}</th>
              <th>{{ t('admin.scheduledJobs.history.status', {}, 'Status') }}</th>
              <th>{{ t('admin.scheduledJobs.history.trigger', {}, 'Trigger') }}</th>
              <th>{{ t('admin.scheduledJobs.history.result', {}, 'Result') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="h in history" :key="h.Id">
              <td>{{ fmtDate(h.StartedAt) }}</td>
              <td>{{ fmtDuration(h.DurationMs) }}</td>
              <td>
                <CoarTag :variant="h.Success ? 'success' : 'danger'">
                  {{ h.Success ? t('common.success', {}, 'OK') : t('common.failed', {}, 'Failed') }}
                </CoarTag>
              </td>
              <td>
                <CoarTag v-if="h.ManualTrigger" variant="info">
                  {{ t('admin.scheduledJobs.history.manual', {}, 'manual') }}
                </CoarTag>
                <span v-else>—</span>
              </td>
              <td class="result-cell">
                <div v-if="h.ResultSummary">{{ h.ResultSummary }}</div>
                <div v-if="h.ErrorMessage" class="error">{{ h.ErrorMessage }}</div>
              </td>
            </tr>
          </tbody>
        </table>
      </section>
    </div>
  </ModalLayout>
</template>

<style scoped>
.loading-block,
.empty-block {
  padding: 2rem;
  text-align: center;
  color: var(--coar-text-neutral-secondary);
}

.details {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  padding: 1rem;
}

.card {
  background: var(--coar-background-neutral-secondary);
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: 8px;
  padding: 1rem 1.25rem;
}

.card h3 {
  margin: 0 0 0.75rem 0;
  font-size: 0.95rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--coar-text-neutral-secondary);
}

.card h3 .count {
  font-weight: 400;
  text-transform: none;
  letter-spacing: normal;
  margin-left: 0.25rem;
}

.card h4 {
  margin: 1rem 0 0.5rem 0;
  font-size: 0.85rem;
  font-weight: 600;
}

.grid-2 {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 1rem;
}

.actions {
  display: flex;
  gap: 0.5rem;
  margin-top: 1rem;
}

.kv {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 0.5rem 1rem;
  margin: 0;
}

.kv dt {
  font-weight: 500;
  color: var(--coar-text-neutral-secondary);
}

.kv dd {
  margin: 0;
}

.history {
  width: 100%;
  border-collapse: collapse;
}

.history th,
.history td {
  text-align: left;
  padding: 0.5rem 0.75rem;
  border-bottom: 1px solid var(--coar-border-neutral-secondary);
}

.history th {
  font-size: 0.85rem;
  font-weight: 600;
  color: var(--coar-text-neutral-secondary);
}

.history .result-cell {
  font-size: 0.9rem;
}

.history .error {
  color: var(--coar-text-error, #b91c1c);
  font-size: 0.85rem;
  margin-top: 0.25rem;
}

code {
  font-family: var(--coar-font-mono, monospace);
  font-size: 0.85em;
}
</style>
