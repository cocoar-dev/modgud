<script setup lang="ts">
/**
 * The global staging bar (ADR-0017 Increment A) — the pendant of the OPNsense
 * "apply changes" strip, rendered footer-positioned by MainLayout across the
 * whole admin area whenever a draft is checked out. It is the ONLY footer bar
 * (the export selection lives in a header chip). Shows the active draft and
 * its counters — name + counters are one link to the workspace diff — and the
 * branch verbs: discard, park, apply.
 *
 * The bar only appears once a draft exists — the first staged change creates one
 * implicitly, so an admin who never stages anything never sees it.
 */
import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { CoarButton, CoarIcon, CoarPopconfirm, CoarPopover, CoarSpinner, CoarTag, useToast } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { draftDisplayName, draftErrorMessage, isAutoDraftName, useRealmDraftStore } from '@/stores/realmDraft.store'

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

const draftName = computed(() => (store.current ? draftDisplayName(store.current, t) : ''))
// An auto-named draft already reads "Draft by …"; a "Draft:" prefix would say it twice.
const isAutoNamed = computed(() => !!store.current && isAutoDraftName(store.current.Name))

const applyDisabled = computed(() => !store.canApply || store.pendingCount === 0)

/** Why Apply is disabled — the button's tooltip, so a greyed-out apply explains itself. */
const applyBlockedReason = computed(() => {
  if (!applyDisabled.value || store.planning) return ''
  if (store.planHasErrors) return t('admin.realmConfig.bar.fixErrorsFirst', {}, 'Fix the plan errors first.')
  if (store.plan?.HasConflicts) return t('admin.realmConfig.bar.fixConflictsFirst', {}, 'Resolve the plan conflicts first.')
  if (store.pendingCount === 0) return t('admin.realmConfig.bar.nothingToApply', {}, 'Nothing is staged yet.')
  return ''
})

const discardTitle = computed(() => t('admin.realmConfig.bar.discardTitleEmpty', {}, 'Discard this draft?'))

const discardMessage = computed(() => {
  const count = store.pendingCount
  const untouched = t('admin.realmConfig.bar.discardMessage', {}, 'The realm itself is untouched.')
  if (count === 0) return untouched
  const lost = count === 1
    ? t('admin.realmConfig.bar.discardLostOne', {}, '1 staged change is dropped.')
    : t('admin.realmConfig.bar.discardLost', { count }, `${count} staged changes are dropped.`)
  return `${lost} ${untouched}`
})

// CoarPopover exposes no close() — re-keying the overflow popover closes it.
const overflowKey = ref(0)

async function discardDraft() {
  if (!store.current) return
  overflowKey.value++
  try {
    await store.deleteDraft(store.current.Id)
    toast.success(t('admin.realmConfig.bar.discarded', {}, 'Draft discarded.'))
  } catch (e) {
    toast.error(draftErrorMessage(e))
  }
}

async function parkDraft() {
  overflowKey.value++
  await store.closeDraft()
}

async function applyDraft() {
  const ok = await store.apply()
  if (!ok) return
  toast.success(t('admin.realmConfig.applied', {}, 'Draft applied.'))
  // A credential or client created by the apply comes back with its secret ONCE,
  // and only the drafts workspace renders it. Applied from any other admin page,
  // the secret would vanish behind the toast — so take the admin there.
  if (Object.keys(store.applyOutcome?.ClientSecrets ?? {}).length > 0)
    await router.push('/admin/realm-config')
}
</script>

<template>
  <div v-if="store.current" class="staging-bar" data-testid="staging-bar">
    <!-- Name + counters are ONE target: the workspace with the full diff. -->
    <RouterLink to="/admin/realm-config" class="bar-link" data-testid="staging-bar-link"
      :title="t('admin.realmConfig.bar.view', {}, 'Review')">
      <CoarIcon name="pencil" size="s" class="bar-icon" />
      <span v-if="!isAutoNamed" class="bar-prefix">{{ t('admin.realmConfig.bar.prefix', {}, 'Draft:') }}</span>
      <span class="bar-name">{{ draftName }}</span>
      <span class="bar-tags">
        <CoarTag v-if="store.planning" variant="neutral" size="s">
          <CoarSpinner size="s" />
        </CoarTag>
        <template v-else>
          <CoarTag v-if="store.pendingCount > 0" variant="info" size="s">
            {{ t('admin.realmConfig.bar.pending', { count: store.pendingCount }, `${store.pendingCount} staged`) }}
          </CoarTag>
          <!-- Plan errors (e.g. a staged deletion of a lockout-protected entity)
               block the apply — without this tag the bar would read "no changes". -->
          <CoarTag v-if="store.errorCount > 0" variant="error" size="s">
            <CoarIcon name="circle-alert" size="s" />
            {{ t('admin.realmConfig.bar.errorCount', { count: store.errorCount },
              `${store.errorCount} error${store.errorCount === 1 ? '' : 's'}`) }}
          </CoarTag>
          <CoarTag v-if="store.pendingCount === 0 && store.errorCount === 0" variant="neutral" size="s">
            {{ t('admin.realmConfig.bar.clean', {}, 'no changes') }}
          </CoarTag>
        </template>
        <CoarTag v-if="store.plan?.HasConflicts" variant="warning" size="s">
          <CoarIcon name="shield-alert" size="s" />
          {{ t('admin.realmConfig.bar.conflicts', {}, 'conflicts') }}
        </CoarTag>
      </span>
    </RouterLink>

    <span class="bar-spacer" />

    <!-- Wide: discard + park inline. Narrow: both move into the ⋯ popover. -->
    <div class="bar-secondary">
      <CoarPopconfirm
        placement="top"
        :title="discardTitle"
        :message="discardMessage"
        :confirm-text="t('admin.realmConfig.discardVerb', {}, 'Discard')"
        :cancel-text="t('common.cancel', {}, 'Cancel')"
        confirm-variant="danger"
        @confirmed="discardDraft">
        <CoarButton size="s" variant="ghost" class="bar-discard" data-testid="staging-bar-discard">
          {{ t('admin.realmConfig.discardVerb', {}, 'Discard') }}
        </CoarButton>
      </CoarPopconfirm>
      <CoarButton size="s" variant="ghost" :loading="store.saving" data-testid="staging-bar-park" @click="parkDraft">
        {{ t('admin.realmConfig.bar.park', {}, 'Park') }}
      </CoarButton>
    </div>

    <CoarPopover :key="overflowKey" mode="click" :offset="6" class="bar-overflow">
      <CoarButton size="s" variant="ghost" icon-start="ellipsis" data-testid="staging-bar-more"
        :aria-label="t('admin.realmConfig.bar.more', {}, 'More draft actions')" />
      <template #content>
        <div class="overflow-panel" data-testid="staging-bar-more-panel">
          <CoarButton size="s" variant="ghost" full-width :loading="store.saving" data-testid="staging-bar-more-park" @click="parkDraft">
            {{ t('admin.realmConfig.bar.park', {}, 'Park') }}
          </CoarButton>
          <CoarPopconfirm
        placement="top"
            :title="discardTitle"
            :message="discardMessage"
            :confirm-text="t('admin.realmConfig.discardVerb', {}, 'Discard')"
            :cancel-text="t('common.cancel', {}, 'Cancel')"
            confirm-variant="danger"
            @confirmed="discardDraft">
            <CoarButton size="s" variant="ghost" full-width class="bar-discard" data-testid="staging-bar-more-discard">
              {{ t('admin.realmConfig.discardVerb', {}, 'Discard') }}
            </CoarButton>
          </CoarPopconfirm>
        </div>
      </template>
    </CoarPopover>

    <!-- The wrapper carries the tooltip: a disabled button fires no hover events. -->
    <span :title="applyBlockedReason || undefined" class="bar-apply">
      <CoarPopconfirm
        placement="top"
        :title="t('admin.realmConfig.applyConfirmTitle', {}, 'Apply the changes to the realm now?')"
        :message="t('admin.realmConfig.applyConfirm', {}, 'The staged changes are applied to this realm in one transaction — all or nothing.')"
        :confirm-text="t('admin.realmConfig.applyVerb', {}, 'Apply')"
        :cancel-text="t('common.cancel', {}, 'Cancel')"
        :disabled="applyDisabled"
        confirm-variant="primary"
        @confirmed="applyDraft">
        <CoarButton
          size="s"
          variant="primary"
          icon-start="check"
          data-testid="staging-bar-apply"
          :loading="store.applying"
          :disabled="applyDisabled">
          {{ store.pendingCount > 0
            ? t('admin.realmConfig.bar.applyCount', { count: store.pendingCount }, `Apply (${store.pendingCount})`)
            : t('admin.realmConfig.applyVerb', {}, 'Apply') }}
        </CoarButton>
      </CoarPopconfirm>
    </span>
  </div>
</template>

<style scoped>
.staging-bar {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  min-width: 0;
  padding: 0.4rem 1rem 0.4rem calc(1rem - 3px);
  border-top: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  /* The accent stripe marks "you are working in a draft" at a glance. */
  border-left: 3px solid var(--coar-border-accent-primary, #2563eb);
  background: var(--coar-background-neutral-secondary, #f7f8fa);
  flex-shrink: 0;
}

.bar-link {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  min-width: 0;
  padding: 2px 6px;
  border-radius: 6px;
  color: inherit;
  text-decoration: none;
}

.bar-link:hover {
  background: var(--coar-background-neutral-tertiary, #eceef1);
}

.bar-link:hover .bar-name {
  text-decoration: underline;
}

.bar-icon {
  flex-shrink: 0;
  color: var(--coar-text-accent-primary, #2563eb);
}

.bar-prefix {
  flex-shrink: 0;
  font-size: 0.8rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
}

.bar-name {
  min-width: 0;
  font-weight: 600;
  font-size: 0.8rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bar-tags {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  flex-shrink: 0;
}

.bar-spacer {
  flex: 1;
}

.bar-secondary {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  flex-shrink: 0;
}

.bar-discard:hover {
  color: var(--coar-text-semantic-error, #dc2626);
}

.bar-overflow {
  display: none;
}

.bar-apply {
  display: inline-flex;
  flex-shrink: 0;
}

.overflow-panel {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 10rem;
  padding: 4px;
}

@media (max-width: 900px) {
  .bar-secondary {
    display: none;
  }

  .bar-overflow {
    display: inline-flex;
    flex-shrink: 0;
  }
}
</style>
