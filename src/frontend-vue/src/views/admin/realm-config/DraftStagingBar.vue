<script setup lang="ts">
/**
 * The global staging bar (ADR-0005 Increment A) — the pendant of the OPNsense
 * "apply changes" strip, rendered footer-positioned by MainLayout across the
 * whole admin area whenever a draft is checked out. Shows the active draft,
 * its pending-change count, and the branch verbs: view (workspace diff),
 * park, apply.
 *
 * The bar only appears once a draft exists — the first staged change creates one
 * implicitly, so an admin who never stages anything never sees it.
 */
import { onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { CoarButton, CoarIcon, CoarPopconfirm, CoarSpinner, CoarTag, useToast } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useRealmDraftStore } from '@/stores/realmDraft.store'

const { t } = useI18n()
const router = useRouter()
const toast = useToast()
const store = useRealmDraftStore()

onMounted(() => {
  if (!store.current) void store.loadActive()
})

// The draft store swallows mutation failures into `store.error`, which only
// the drafts WORKSPACE renders — a failed stage/stage-delete from a normal
// admin list would otherwise be perfectly silent ("I clicked delete and
// nothing happened"). This bar is mounted on every admin page, so surface
// every draft error as a toast here.
watch(() => store.error, (message) => {
  if (message) toast.error(message)
})

async function applyDraft() {
  const ok = await store.apply()
  if (ok) toast.success(t('admin.realmConfig.applied', {}, 'Draft applied.'))
}
</script>

<template>
  <div v-if="store.current" class="staging-bar">
    <CoarIcon name="file-json" size="s" class="bar-icon" />
    <span class="bar-name">{{ store.current.Name }}</span>
    <CoarTag v-if="store.planning" variant="neutral" size="s">
      <CoarSpinner size="s" />
    </CoarTag>
    <CoarTag v-else-if="store.pendingCount > 0" variant="info" size="s">
      {{ t('admin.realmConfig.bar.pending', { count: store.pendingCount }, `${store.pendingCount} staged`) }}
    </CoarTag>
    <CoarTag v-else-if="!store.planHasErrors" variant="neutral" size="s">
      {{ t('admin.realmConfig.bar.clean', {}, 'no changes') }}
    </CoarTag>
    <CoarTag v-if="store.plan?.HasConflicts" variant="warning" size="s">
      <CoarIcon name="shield-alert" size="s" />
      {{ t('admin.realmConfig.bar.conflicts', {}, 'conflicts') }}
    </CoarTag>
    <!-- Plan errors (e.g. a staged deletion of a lockout-protected entity)
         block the apply — without this tag the bar would read "no changes". -->
    <CoarTag v-if="!store.planning && store.planHasErrors" variant="error" size="s">
      <CoarIcon name="circle-alert" size="s" />
      {{ t('admin.realmConfig.bar.errors', {}, 'plan errors') }}
    </CoarTag>

    <span class="bar-spacer" />

    <CoarButton size="s" variant="ghost" @click="router.push('/admin/realm-config')">
      {{ t('admin.realmConfig.bar.view', {}, 'Review') }}
    </CoarButton>
    <CoarButton size="s" variant="ghost" :loading="store.saving" @click="store.closeDraft()">
      {{ t('admin.realmConfig.bar.park', {}, 'Park') }}
    </CoarButton>
    <CoarPopconfirm
      :title="t('admin.realmConfig.applyConfirmTitle', {}, 'Apply this draft?')"
      :message="t('admin.realmConfig.applyConfirm', {}, 'The staged changes are applied to this realm in one transaction — all or nothing.')"
      confirm-variant="primary"
      @confirmed="applyDraft">
      <CoarButton
        size="s"
        variant="primary"
        :loading="store.applying"
        :disabled="!store.canApply || store.pendingCount === 0">
        {{ store.pendingCount > 0
          ? t('admin.realmConfig.applyCount', { count: store.pendingCount }, `Apply (${store.pendingCount})`)
          : t('admin.realmConfig.apply', {}, 'Apply draft') }}
      </CoarButton>
    </CoarPopconfirm>
  </div>
</template>

<style scoped>
.staging-bar {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.4rem 1rem;
  border-top: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  background: var(--coar-background-neutral-secondary, #f7f8fa);
  flex-shrink: 0;
}

.bar-icon {
  color: var(--coar-text-neutral-secondary, #6b7280);
}

.bar-name {
  font-weight: 600;
  font-size: 0.8rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bar-spacer {
  flex: 1;
}
</style>
