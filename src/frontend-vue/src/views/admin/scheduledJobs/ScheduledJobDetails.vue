<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from '@cocoar/vue-localization'
import {
  CoarTextInput,
  CoarNumberInput,
  CoarCheckbox,
  CoarFormField,
  CoarButton,
  CoarIcon,
  CoarTag,
  CoarTabGroup,
  CoarTab,
} from '@cocoar/vue-ui'
import ModalLayout from '@/components/ModalLayout.vue'
import { useScheduledJobStore } from '@/stores/scheduledJob.store'
import type {
  ScheduledJobDto,
  ScheduledJobHistoryDto,
  JobParameterField,
} from '@/models/ScheduledJob'

const { t, language } = useI18n()
const store = useScheduledJobStore()

const locale = computed(() => language.value === 'de' ? 'de-DE' : 'en-US')

// Routed-fragment modals receive the path param as `id`. The :id slot in
// the router carries the job's Key (e.g. "dcr-gc").
const props = defineProps<{ id: string; close: (result?: unknown) => void }>()
const jobKey = computed(() => props.id)

const job = ref<ScheduledJobDto | null>(null)
const history = ref<ScheduledJobHistoryDto[]>([])
const loading = ref(true)
const saving = ref(false)
const triggering = ref(false)

const activeTab = ref<'schedule' | 'config' | 'history'>('schedule')

// Editable form state — separated from `job` so a user can edit and discard.
const form = ref({
  cronOverride: '',
  enabled: true,
  // Parameter values keyed by ParameterField.Key. NumberInput keeps numbers
  // numeric; null/empty means "use default" for the backend.
  params: {} as Record<string, string | number | boolean | null>,
})

async function load() {
  if (!jobKey.value) return
  loading.value = true
  try {
    const [j, h] = await Promise.all([
      store.getByKey(jobKey.value),
      store.getHistory(jobKey.value, 50),
    ])
    job.value = j
    history.value = h
    if (j) {
      form.value.cronOverride = j.HasOverride ? j.EffectiveCron : ''
      form.value.enabled = j.Enabled
      form.value.params = seedParams(j)
    }
  } finally {
    loading.value = false
  }
}

/** Seed form.params from persisted Parameters + schema defaults. */
function seedParams(j: ScheduledJobDto): Record<string, string | number | boolean | null> {
  const out: Record<string, string | number | boolean | null> = {}
  for (const field of j.ParameterSchema) {
    const v = j.Parameters[field.Key]
    if (field.Type === 'Boolean') {
      out[field.Key] = typeof v === 'boolean' ? v : Boolean(field.Default)
    } else if (field.Type === 'Number') {
      const numberValue = v == null || v === '' ? field.Default : v
      out[field.Key] = numberValue == null || numberValue === '' ? null : Number(numberValue)
    } else {
      out[field.Key] = v == null || v === '' ? '' : String(v)
    }
  }
  return out
}

/** Build the Parameters payload — coerce strings to numbers per schema. */
function buildParamsPayload(j: ScheduledJobDto): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  for (const field of j.ParameterSchema) {
    const raw = form.value.params[field.Key]
    if (field.Type === 'Boolean') {
      out[field.Key] = !!raw
    } else if (field.Type === 'Number') {
      if (raw === '' || raw == null) {
        out[field.Key] = null
      } else {
        const n = Number(raw)
        out[field.Key] = Number.isFinite(n) ? n : null
      }
    } else {
      out[field.Key] = raw === '' ? null : raw
    }
  }
  return out
}

async function save() {
  if (!jobKey.value || !job.value) return
  saving.value = true
  try {
    await store.update(jobKey.value, {
      CronOverride: form.value.cronOverride.trim() || null,
      Enabled: form.value.enabled,
      Parameters: buildParamsPayload(job.value),
    })
    await load()
  } finally {
    saving.value = false
  }
}

async function triggerNow() {
  if (!jobKey.value) return
  triggering.value = true
  try {
    await store.triggerNow(jobKey.value)
    await new Promise((r) => setTimeout(r, 800))
    history.value = await store.getHistory(jobKey.value, 50)
    job.value = await store.getByKey(jobKey.value)
    await store.loadAll()
  } finally {
    triggering.value = false
  }
}

function fmtDate(iso: string) {
  return new Date(iso).toLocaleString(locale.value, {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit',
  })
}

function fmtDuration(ms: number) {
  if (ms < 1000) return `${ms} ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`
  return `${(ms / 60_000).toFixed(1)} min`
}

/** Group schema fields by Section, preserving declaration order. */
const sectionedFields = computed<Array<{ name: string | null; fields: JobParameterField[] }>>(() => {
  if (!job.value) return []
  const groups: Array<{ name: string | null; fields: JobParameterField[] }> = []
  for (const field of job.value.ParameterSchema) {
    const sectionName = field.Section ?? null
    const last = groups[groups.length - 1]
    if (last && last.name === sectionName) {
      last.fields.push(field)
    } else {
      groups.push({ name: sectionName, fields: [field] })
    }
  }
  return groups
})

const hasParams = computed(() => (job.value?.ParameterSchema.length ?? 0) > 0)

const footerButton = computed(() => ({
  visible: !!job.value,
  text: t('common.save', {}, 'Save'),
  loading: saving.value,
  onClick: save,
}))

onMounted(load)
watch(jobKey, load)
</script>

<template>
  <ModalLayout
    :title="job?.Name ?? jobKey"
    :sub-title="job?.Key"
    icon="clock"
    :close="close"
    :footer-button="footerButton"
  >
    <div class="flex flex-col min-h-0 flex-1">
      <div v-if="loading" class="p-8 text-center text-gray-400">
        {{ t('common.loading', {}, 'Loading…') }}
      </div>
      <div v-else-if="!job" class="p-8 text-center text-gray-400">
        {{ t('admin.scheduledJobs.notFound', {}, 'Job not found') }}
      </div>
      <div v-else class="flex flex-col gap-4 min-h-0 flex-1">
        <p v-if="job.Description" class="text-sm text-surface-500">{{ job.Description }}</p>

        <CoarTabGroup v-model="activeTab">
          <CoarTab id="schedule">{{ t('admin.scheduledJobs.tabSchedule', {}, 'Schedule') }}</CoarTab>
          <CoarTab v-if="hasParams" id="config">{{ t('admin.scheduledJobs.tabConfig', {}, 'Configuration') }}</CoarTab>
          <CoarTab id="history">{{ t('admin.scheduledJobs.tabHistory', {}, 'History') }}</CoarTab>
        </CoarTabGroup>

        <!-- Schedule tab -->
        <div v-show="activeTab === 'schedule'" class="flex flex-col gap-4 pt-2">
          <div class="grid grid-cols-2 gap-4">
            <CoarFormField :label="t('admin.scheduledJobs.cron', {}, 'Cron expression')">
              <CoarTextInput
                v-model="form.cronOverride"
                :placeholder="job.DefaultCron"
                spellcheck="false"
              />
              <template #help>
                <span class="text-xs text-surface-500">
                  {{ t('admin.scheduledJobs.cronHelp', { default: job.DefaultCron },
                    'Quartz 7-field cron. Leave blank to use default: {default}') }}
                </span>
              </template>
            </CoarFormField>

            <CoarFormField :label="t('admin.scheduledJobs.enabled', {}, 'Enabled')">
              <CoarCheckbox v-model="form.enabled" :label="t('admin.scheduledJobs.enabledHelp', {}, 'Run on schedule')" />
            </CoarFormField>
          </div>

          <div class="flex items-center gap-3">
            <CoarButton size="s" variant="primary" icon-start="play" :loading="triggering" @click="triggerNow">
              {{ t('admin.scheduledJobs.triggerNow', {}, 'Run now') }}
            </CoarButton>
            <span class="text-sm text-surface-500">
              {{ t('admin.scheduledJobs.nextRun', {}, 'Next run') }}:
              <strong>{{ job.NextFireAt ? fmtDate(job.NextFireAt) : '—' }}</strong>
            </span>
          </div>
        </div>

        <!-- Configuration tab -->
        <div v-show="activeTab === 'config'" class="flex flex-col gap-5 pt-2 overflow-y-auto">
          <div v-for="group in sectionedFields" :key="group.name ?? '__none'" class="flex flex-col gap-3">
            <h3 v-if="group.name"
                class="text-xs font-semibold uppercase tracking-wider text-surface-500 border-b border-surface-200 pb-1">
              {{ group.name }}
            </h3>
            <div class="grid grid-cols-2 gap-4">
              <CoarFormField
                v-for="field in group.fields"
                :key="field.Key"
                :label="field.Label"
              >
                <CoarCheckbox
                  v-if="field.Type === 'Boolean'"
                  v-model="(form.params[field.Key] as boolean)"
                  :label="field.Label"
                />
                <CoarNumberInput
                  v-else-if="field.Type === 'Number'"
                  v-model="(form.params[field.Key] as number | null)"
                  :placeholder="field.Placeholder ?? (field.Default != null ? String(field.Default) : '')"
                />
                <CoarTextInput
                  v-else
                  v-model="(form.params[field.Key] as string)"
                  :placeholder="field.Placeholder ?? (field.Default != null ? String(field.Default) : '')"
                />
                <template v-if="field.Description" #help>
                  <span class="text-xs text-surface-500">{{ field.Description }}</span>
                </template>
              </CoarFormField>
            </div>
          </div>
        </div>

        <!-- History tab -->
        <div v-show="activeTab === 'history'" class="flex-1 flex flex-col min-h-0 pt-2">
          <div v-if="history.length === 0" class="text-sm text-surface-500 p-4">
            {{ t('admin.scheduledJobs.noHistory', {}, 'No runs yet') }}
          </div>
          <div v-else class="history-list overflow-y-auto min-h-0">
            <div
              v-for="entry in history"
              :key="entry.Id"
              class="history-entry"
              :class="{ 'history-entry--failed': !entry.Success }"
            >
              <div class="flex items-baseline gap-2">
                <CoarIcon
                  :name="entry.Success ? 'circle-check' : 'shield-alert'"
                  size="s"
                  :color="entry.Success ? '#16a34a' : '#dc2626'"
                />
                <span class="font-mono text-xs">{{ fmtDate(entry.StartedAt) }}</span>
                <span class="text-xs text-surface-500">({{ fmtDuration(entry.DurationMs) }})</span>
                <CoarTag v-if="entry.ManualTrigger" variant="info" size="s">
                  {{ t('admin.scheduledJobs.manualTrigger', {}, 'manual') }}
                </CoarTag>
              </div>
              <div v-if="entry.ResultSummary" class="text-sm mt-1">{{ entry.ResultSummary }}</div>
              <div v-if="!entry.Success && entry.ErrorMessage" class="text-sm text-red-700 mt-1">
                {{ entry.ErrorMessage }}
              </div>
              <details v-if="entry.ExceptionDetail" class="mt-1">
                <summary class="text-xs text-surface-500 cursor-pointer">
                  {{ t('admin.scheduledJobs.stacktrace', {}, 'Stack trace') }}
                </summary>
                <pre class="text-xs mt-1 p-2 bg-surface-100 overflow-x-auto">{{ entry.ExceptionDetail }}</pre>
              </details>
            </div>
          </div>
        </div>
      </div>
    </div>
  </ModalLayout>
</template>

<style scoped>
.history-list {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.history-entry {
  padding: 0.5rem 0.75rem;
  border-radius: 0.375rem;
  background: var(--coar-background-neutral-secondary, #f9fafb);
  border-left: 3px solid var(--coar-background-semantic-success-bold, #16a34a);
}

.history-entry--failed {
  border-left-color: var(--coar-background-semantic-error-bold, #dc2626);
}
</style>
