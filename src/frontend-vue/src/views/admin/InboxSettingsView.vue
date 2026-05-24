<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useI18n } from '@cocoar/vue-localization'
import {
  CoarCard, CoarTextInput, CoarFormField, CoarButton, CoarIcon,
} from '@cocoar/vue-ui'
import { useUI } from '@/composables/useUI'
import { useInboxSettingsStore } from '@/stores/inboxSettings.store'
import type { InboxRetentionSettings } from '@/models/inboxSettings'

const { t, language } = useI18n()
const ui = useUI()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Plattform')
  ctx.header.subTitle = t('admin.inboxSettings.title', {}, 'Inbox-Einstellungen')
  ctx.header.icon = 'inbox'
  ctx.content.container = false
}), { immediate: true })

const store = useInboxSettingsStore()
const form = ref<InboxRetentionSettings | null>(null)
const saving = ref(false)
const saveResult = ref<{ ok: boolean; message: string } | null>(null)

onMounted(async () => {
  await store.load()
  form.value = JSON.parse(JSON.stringify(store.settings))
})

/**
 * Number-input v-model adapter: empty input → null, valid number → int.
 * Each section uses these wrappers so the "leave blank = never" semantic
 * round-trips cleanly with the backend's nullable<int> fields.
 */
function numModel(getter: () => number | null, setter: (v: number | null) => void) {
  return {
    get: () => (getter() == null ? '' : String(getter())),
    set: (v: string) => {
      const trimmed = v.trim()
      if (trimmed === '') { setter(null); return }
      const n = Number(trimmed)
      setter(Number.isFinite(n) ? n : null)
    },
  }
}

async function save() {
  if (!form.value) return
  saving.value = true
  saveResult.value = null
  try {
    await store.save(form.value)
    saveResult.value = { ok: true, message: t('admin.inboxSettings.saved', {}, 'Einstellungen gespeichert.') }
  } catch (e: any) {
    saveResult.value = { ok: false, message: e?.data?.Message || t('admin.inboxSettings.saveFailed', {}, 'Speichern fehlgeschlagen.') }
  } finally {
    saving.value = false
    setTimeout(() => saveResult.value = null, 5000)
  }
}
</script>

<template>
  <div class="flex flex-col flex-1 min-h-0 p-4 gap-4 overflow-y-auto">
    <div v-if="!form" class="p-8 text-center text-surface-400">
      {{ t('common.loading', {}, 'Loading…') }}
    </div>

    <template v-else-if="form">
      <!-- ── Admin-Änderungsanträge (Event-driven) ─────────── -->
      <CoarCard>
        <template #header>
          <div class="flex items-center gap-2">
            <CoarIcon name="clipboard-list" size="s" />
            <span class="font-semibold">{{ t('admin.inboxSettings.adminCr.title', {}, 'Änderungsanträge (Admin-Inbox)') }}</span>
          </div>
        </template>
        <p class="text-xs text-surface-500 mb-4">
          {{ t('admin.inboxSettings.adminCr.description', {},
            'Offene Anträge bleiben so lange in der Admin-Inbox, bis ein Admin sie approved oder rejected. Diese Einstellung steuert nur, wie lange erledigte Items für die Nachvollziehbarkeit bestehen bleiben.') }}
        </p>
        <div class="grid grid-cols-2 gap-4">
          <CoarFormField :label="t('admin.inboxSettings.hardDeleteDaysAfterDismissed', {}, 'Hard-delete N Tage nach Erledigung')">
            <CoarTextInput
              type="number"
              :model-value="numModel(() => form!.AdminChangeRequest.HardDeleteDaysAfterDismissed, v => form!.AdminChangeRequest.HardDeleteDaysAfterDismissed = v).get()"
              @update:model-value="numModel(() => form!.AdminChangeRequest.HardDeleteDaysAfterDismissed, v => form!.AdminChangeRequest.HardDeleteDaysAfterDismissed = v).set($event)"
              placeholder="leer = nie"
            />
            <template #help>
              <span class="text-xs text-surface-500">
                {{ t('admin.inboxSettings.help.hardDeleteDaysAfterDismissed', {},
                  'Items, die durch Approve/Reject erledigt wurden, werden nach so vielen Tagen endgültig gelöscht. Leer = nie löschen.') }}
              </span>
            </template>
          </CoarFormField>
        </div>
      </CoarCard>

      <!-- ── Änderungsantrag-Feedback (User-Inbox) ─────────── -->
      <CoarCard>
        <template #header>
          <div class="flex items-center gap-2">
            <CoarIcon name="circle-check" size="s" />
            <span class="font-semibold">{{ t('admin.inboxSettings.feedback.title', {}, 'Antrags-Feedback (User-Inbox)') }}</span>
          </div>
        </template>
        <p class="text-xs text-surface-500 mb-4">
          {{ t('admin.inboxSettings.feedback.description', {}, 'Bestätigungen / Ablehnungen, die der Antragsteller bekommt.') }}
        </p>
        <div class="grid grid-cols-2 gap-4">
          <CoarFormField :label="t('admin.inboxSettings.maxUnreadDays', {}, 'Max. Tage ungelesen')">
            <CoarTextInput
              type="number"
              :model-value="numModel(() => form!.ChangeRequestFeedback.MaxUnreadDays, v => form!.ChangeRequestFeedback.MaxUnreadDays = v).get()"
              @update:model-value="numModel(() => form!.ChangeRequestFeedback.MaxUnreadDays, v => form!.ChangeRequestFeedback.MaxUnreadDays = v).set($event)"
              placeholder="leer = nie"
            />
          </CoarFormField>
          <CoarFormField :label="t('admin.inboxSettings.autoExpireDaysAfterRead', {}, 'Max. Tage nach Lesen')">
            <CoarTextInput
              type="number"
              :model-value="numModel(() => form!.ChangeRequestFeedback.AutoExpireDaysAfterRead, v => form!.ChangeRequestFeedback.AutoExpireDaysAfterRead = v).get()"
              @update:model-value="numModel(() => form!.ChangeRequestFeedback.AutoExpireDaysAfterRead, v => form!.ChangeRequestFeedback.AutoExpireDaysAfterRead = v).set($event)"
              placeholder="leer = nie"
            />
          </CoarFormField>
        </div>
      </CoarCard>

      <!-- ── Scheduled-Job-Feedback (Operational) ─────────── -->
      <CoarCard>
        <template #header>
          <div class="flex items-center gap-2">
            <CoarIcon name="clock" size="s" />
            <span class="font-semibold">{{ t('admin.inboxSettings.jobFeedback.title', {}, 'Scheduled-Job-Feedback') }}</span>
          </div>
        </template>
        <p class="text-xs text-surface-500 mb-4">
          {{ t('admin.inboxSettings.jobFeedback.description', {},
            'Operative Job-Signale (Failures an Admins, manuelle Trigger-Bestätigungen an den auslösenden User). Kürzere Defaults, weil operative Signale schneller veralten.') }}
        </p>
        <div class="grid grid-cols-2 gap-4">
          <CoarFormField :label="t('admin.inboxSettings.maxUnreadDays', {}, 'Max. Tage ungelesen')">
            <CoarTextInput
              type="number"
              :model-value="numModel(() => form!.ScheduledJobFeedback.MaxUnreadDays, v => form!.ScheduledJobFeedback.MaxUnreadDays = v).get()"
              @update:model-value="numModel(() => form!.ScheduledJobFeedback.MaxUnreadDays, v => form!.ScheduledJobFeedback.MaxUnreadDays = v).set($event)"
              placeholder="leer = nie"
            />
          </CoarFormField>
          <CoarFormField :label="t('admin.inboxSettings.autoExpireDaysAfterRead', {}, 'Max. Tage nach Lesen')">
            <CoarTextInput
              type="number"
              :model-value="numModel(() => form!.ScheduledJobFeedback.AutoExpireDaysAfterRead, v => form!.ScheduledJobFeedback.AutoExpireDaysAfterRead = v).get()"
              @update:model-value="numModel(() => form!.ScheduledJobFeedback.AutoExpireDaysAfterRead, v => form!.ScheduledJobFeedback.AutoExpireDaysAfterRead = v).set($event)"
              placeholder="leer = nie"
            />
          </CoarFormField>
        </div>
      </CoarCard>

      <div class="flex items-center gap-3 mt-2">
        <CoarButton variant="primary" :loading="saving" @click="save">
          {{ t('common.save', {}, 'Save') }}
        </CoarButton>
        <span v-if="saveResult" :class="saveResult.ok ? 'text-green-700' : 'text-red-700'" class="text-sm">
          {{ saveResult.message }}
        </span>
      </div>
    </template>
  </div>
</template>
