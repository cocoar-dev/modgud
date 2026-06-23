<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useInviteCodeStore } from '@/stores/inviteCode.store'
import { useApplicationsStore } from '@/stores/applications.store'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useInviteCodeStore()
const applicationsStore = useApplicationsStore()

// The grid loaded the realm-wide list, so the row is already in the store.
const code = computed(() => store.codes.find((c) => c.Id === props.id) ?? null)
const appName = computed(() => {
  const c = code.value
  if (!c) return ''
  return applicationsStore.apps.find((a) => a.Id === c.AppId)?.DisplayName ?? c.AppId
})

function fmt(d: string | null): string {
  return d ? new Date(d).toLocaleString() : '—'
}

const footerButton = computed(() => ({
  visible: true,
  text: t('common.close', {}, 'Close'),
  onClick: () => props.close(),
}))
</script>

<template>
  <ModalLayout :close="close" :title="t('admin.inviteCodes.details.title', {}, 'Invite code')"
    :sub-title="appName" icon="ticket" :footer-button="footerButton">
    <div class="flex flex-col min-w-0 min-h-0 flex-1">
      <div v-if="!code" class="flex flex-1 items-center justify-center p-8 text-gray-400">
        {{ store.loaded
          ? t('admin.inviteCodes.details.notFound', {}, 'This invite code no longer exists.')
          : t('common.loading', {}, 'Loading…') }}
      </div>
      <div v-else class="modal-form">
        <section class="form-section">
          <dl class="detail-list">
            <dt>{{ t('admin.inviteCodes.app', {}, 'App') }}</dt>
            <dd>{{ appName }}</dd>

            <dt>{{ t('admin.inviteCodes.status', {}, 'Status') }}</dt>
            <dd>{{ code.Status }}</dd>

            <dt>{{ t('admin.inviteCodes.boundEmail', {}, 'Bound to') }}</dt>
            <dd>{{ code.BoundEmail ?? t('admin.inviteCodes.bearer', {}, 'Bearer (anyone)') }}</dd>

            <dt>{{ t('admin.inviteCodes.createdAt', {}, 'Created') }}</dt>
            <dd>{{ fmt(code.CreatedAt) }}</dd>

            <dt>{{ t('admin.inviteCodes.expiresAt', {}, 'Expires') }}</dt>
            <dd>{{ fmt(code.ExpiresAt) }}</dd>

            <dt>{{ t('admin.inviteCodes.createdBy', {}, 'Created by') }}</dt>
            <dd class="font-mono text-sm break-all">{{ code.CreatedBySubject }}</dd>

            <template v-if="code.UsedAt">
              <dt>{{ t('admin.inviteCodes.usedAt', {}, 'Used') }}</dt>
              <dd>{{ fmt(code.UsedAt) }}</dd>

              <dt>{{ t('admin.inviteCodes.usedBy', {}, 'Used by (user)') }}</dt>
              <dd class="font-mono text-sm break-all">{{ code.UsedByUserId ?? '—' }}</dd>
            </template>
          </dl>
          <p class="field-hint mt-3">
            {{ t('admin.inviteCodes.details.hint', {}, 'The code itself is shown only once at mint time — only its hash is stored, so it cannot be displayed here.') }}
          </p>
        </section>
      </div>
    </div>
  </ModalLayout>
</template>

<style scoped>
.detail-list {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 0.5rem 1.25rem;
  align-items: baseline;
}
.detail-list dt {
  font-weight: 600;
  opacity: 0.75;
}
.detail-list dd {
  margin: 0;
}
</style>
