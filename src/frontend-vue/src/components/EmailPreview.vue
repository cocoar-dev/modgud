<script setup lang="ts">
/**
 * Live preview of the built-in transactional emails — one tab per template.
 * Nothing here is a mock-up: the backend renders the REAL template through the
 * real template store with the effective branding, overlaid with the form's
 * unsaved values, and this component shows subject, sender and the rendered
 * body in a sandboxed iframe. That makes it the natural seat for a future
 * per-template editor: same renderer, same model.
 */
import { computed, ref, watch } from 'vue'
import { CoarTabGroup, CoarTab, CoarSelect, CoarIcon } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useRealmSettingsStore, type EmailPreviewOverlay, type EmailPreviewResult } from '@/stores/realmSettings.store'

const props = withDefaults(defineProps<{
  /** Unsaved form values overlaid on the effective branding. */
  overlay: EmailPreviewOverlay
  /** Preview as this Application (realm + App override). Omit for the realm. */
  applicationId?: string | null
}>(), { applicationId: null })

const { t } = useI18n()
const store = useRealmSettingsStore()

// Order = how often an admin will care. The rest of the built-ins follow.
const TEMPLATES = [
  { id: 'EmailOtp', label: 'E-Mail-Code (OTP)', labelEn: 'Email code (OTP)' },
  { id: 'MagicLink', label: 'Anmelde-Link', labelEn: 'Sign-in link' },
  { id: 'PasswordReset', label: 'Passwort zurücksetzen', labelEn: 'Password reset' },
  { id: 'EmailVerification', label: 'E-Mail bestätigen', labelEn: 'Email verification' },
  { id: 'LoginBlocked', label: 'Anmeldeversuche blockiert', labelEn: 'Sign-in attempts blocked' },
  { id: 'RealmAdminBootstrap', label: 'Admin-Einladung', labelEn: 'Admin invite' },
  { id: 'AdminChangeRequestNotification', label: 'Änderungsantrag (Admin)', labelEn: 'Change request (admin)' },
  { id: 'ChangeRequestApproved', label: 'Antrag genehmigt', labelEn: 'Request approved' },
  { id: 'ChangeRequestRejected', label: 'Antrag abgelehnt', labelEn: 'Request rejected' },
] as const

const activeTemplate = ref<string>(TEMPLATES[0].id)
const language = ref<'de' | 'en'>('de')
const languageOptions = [
  { value: 'de', label: 'Deutsch' },
  { value: 'en', label: 'English' },
]

const result = ref<EmailPreviewResult | null>(null)
const error = ref('')
const loading = ref(false)

// Debounced: the overlay changes on every keystroke in the form.
let timer: ReturnType<typeof setTimeout> | undefined
let requestSeq = 0
async function render() {
  const seq = ++requestSeq
  loading.value = true
  error.value = ''
  try {
    const r = await store.previewEmail({
      template: activeTemplate.value,
      language: language.value,
      applicationId: props.applicationId ?? undefined,
      ...props.overlay,
    })
    if (seq === requestSeq) result.value = r
  } catch (e) {
    if (seq === requestSeq) error.value = (e as Error)?.message || String(e)
  } finally {
    if (seq === requestSeq) loading.value = false
  }
}
function scheduleRender() {
  clearTimeout(timer)
  timer = setTimeout(render, 250)
}
watch([activeTemplate, language, () => props.applicationId], scheduleRender, { immediate: true })
watch(() => props.overlay, scheduleRender, { deep: true })

// srcdoc keeps the rendered HTML in its own document; the sandbox blocks scripts
// and the inert "#" action links cannot navigate anywhere.
const srcdoc = computed(() => result.value?.HtmlBody ?? '')
const tabLabel = (tpl: (typeof TEMPLATES)[number]) => t(`admin.emailPreview.templates.${tpl.id}`, {}, tpl.labelEn)
</script>

<template>
  <div class="email-preview rounded-lg border border-surface-200 bg-white">
    <div class="flex items-center gap-3 border-b border-surface-200 px-4 pt-3">
      <!-- Eight templates never fit one row at form width: the bar scrolls sideways
           inside its slot instead of spilling out of the card. -->
      <div class="email-preview-tabs flex-1 min-w-0">
        <CoarTabGroup v-model="activeTemplate">
          <CoarTab v-for="tpl in TEMPLATES" :id="tpl.id" :key="tpl.id">{{ tabLabel(tpl) }}</CoarTab>
        </CoarTabGroup>
      </div>
      <CoarSelect v-model="language" :options="languageOptions" class="w-32 shrink-0" />
    </div>

    <div class="email-preview-head px-4 py-3 text-sm">
      <div class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
        <span class="text-surface-400">{{ t('admin.emailPreview.from', {}, 'From') }}</span>
        <span class="truncate font-medium text-surface-700">{{ result?.From ?? '…' }}</span>
        <template v-if="result?.ReplyTo">
          <span class="text-surface-400">{{ t('admin.emailPreview.replyTo', {}, 'Reply-to') }}</span>
          <span class="truncate text-surface-700">{{ result.ReplyTo }}</span>
        </template>
        <span class="text-surface-400">{{ t('admin.emailPreview.subject', {}, 'Subject') }}</span>
        <span class="truncate font-semibold text-surface-800">{{ result?.Subject ?? '…' }}</span>
      </div>
    </div>

    <div class="relative border-t border-surface-100">
      <div v-if="loading" class="absolute right-3 top-3 z-10 text-surface-400">
        <CoarIcon name="loader" size="s" class="animate-spin" />
      </div>
      <p v-if="error" class="p-4 text-sm text-red-700">{{ error }}</p>
      <iframe
        v-else
        :srcdoc="srcdoc"
        sandbox=""
        :title="t('admin.emailPreview.title', {}, 'Email preview')"
        class="email-preview-frame block w-full bg-white"
      />
    </div>
  </div>
</template>

<style scoped>
.email-preview-tabs {
  overflow-x: auto;
  overflow-y: hidden;
  scrollbar-width: thin;
}
/* The tab list must be allowed to be wider than the slot for the scroll to exist. */
.email-preview-tabs :deep(.coar-tab-list) {
  flex-wrap: nowrap;
  width: max-content;
  min-width: 100%;
}
.email-preview-frame {
  height: 26rem;
  border: 0;
}
</style>
