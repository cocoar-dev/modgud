<script setup lang="ts">
/**
 * Generic draft-entry editor (ADR-0005 Phase 1b) — the interim modal behind every
 * resource card until the type-specific draft modals (Phase 1c) replace it, one
 * resource type at a time. Same ModalLayout frame as the admin modals.
 *
 * It shows the entry's plan (field changes, notes, conflicts with per-field
 * "take live" resolution) and edits the manifest entity as JSON. Saving only
 * RETURNS the staged entity — the workspace owns the draft mutation + re-plan,
 * so nothing here ever writes anywhere. Secrets typed here (e.g. a Password)
 * are extracted server-side into write-only slots on save.
 */
import { computed, ref } from 'vue'
import { CoarButton, CoarIcon, CoarNotice, CoarPasswordInput, CoarTag } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import type { ManifestEntity, PlanConflict, PlanEntry } from '@/stores/realmDraft.store'

export interface DraftEntryModalResult {
  action: 'save' | 'remove'
  entity?: ManifestEntity
}

const props = defineProps<{
  // Untyped on purpose — ModalLayout's close contract is `(result?: unknown)`;
  // the opener types the result via modal.open<DraftEntryModalResult>().
  close: (result?: unknown) => void
  section: string
  entryKey: string
  icon: string
  /** null for prune candidates — they exist live, not in the draft. */
  entity: ManifestEntity | null
  planEntry: PlanEntry | null
  secretSlots: string[]
}>()

const { t } = useI18n()

const entityJson = ref(props.entity ? JSON.stringify(props.entity, null, 2) : '')
const jsonError = ref<string | null>(null)
const password = ref('')

const isUserEntry = computed(() => props.section === 'users' && props.entity !== null)
const secretSlotPrefix = computed(() => `${props.section}/${props.entryKey}/`)
const stagedSecretSlots = computed(() =>
  props.secretSlots.filter((s) => s.startsWith(secretSlotPrefix.value)))

const fieldConflicts = computed(() =>
  (props.planEntry?.Conflicts ?? []).filter((c) => c.Field !== null))
const entityConflicts = computed(() =>
  (props.planEntry?.Conflicts ?? []).filter((c) => c.Field === null))

/** A manifest reference ({ Key, Id }) reads as its key — the id is for the apply. */
function isRef(v: unknown): v is { Key?: string; Id?: string } {
  return !!v && typeof v === 'object' && !Array.isArray(v) &&
    ('Key' in v || 'Id' in v) && Object.keys(v).every((k) => k === 'Key' || k === 'Id')
}

function formatValue(value: unknown): string {
  if (value === null || value === undefined) return t('admin.realmConfig.valueEmpty', {}, '(empty)')
  if (typeof value === 'string') return value
  if (isRef(value)) return value.Key ?? value.Id ?? ''
  if (Array.isArray(value) && value.length > 0 && value.every(isRef))
    return JSON.stringify(value.map((v) => v.Key ?? v.Id ?? ''))
  return JSON.stringify(value)
}

function conflictLabel(kind: PlanConflict['Kind']): string {
  switch (kind) {
    case 'staleOverwrite': return t('admin.realmConfig.conflict.staleOverwrite', {}, 'Changed live — your draft would revert it')
    case 'bothChanged': return t('admin.realmConfig.conflict.bothChanged', {}, 'Changed live AND in the draft')
    case 'deletedLive': return t('admin.realmConfig.conflict.deletedLive', {}, 'Deleted live while the draft still stages it')
    case 'createdLive': return t('admin.realmConfig.conflict.createdLive', {}, 'Appeared live after the draft was started')
  }
}

/** Writes a (possibly dotted) field path into the edited entity JSON. */
function setByPath(target: ManifestEntity, path: string, value: unknown) {
  const parts = path.split('.')
  const last = parts.pop()!
  let node: Record<string, unknown> = target
  for (const part of parts) {
    const next = node[part]
    if (typeof next !== 'object' || next === null) {
      node[part] = {}
    }
    node = node[part] as Record<string, unknown>
  }
  node[last] = value
}

function parseEntity(): ManifestEntity | null {
  try {
    const parsed = JSON.parse(entityJson.value) as ManifestEntity
    jsonError.value = null
    return parsed
  } catch (e) {
    jsonError.value = String(e)
    return null
  }
}

function takeLive(conflict: PlanConflict) {
  if (conflict.Field === null) return
  const entity = parseEntity()
  if (!entity) return
  setByPath(entity, conflict.Field, conflict.Live ?? null)
  entityJson.value = JSON.stringify(entity, null, 2)
}

function save() {
  const entity = parseEntity()
  if (!entity) return
  if (isUserEntry.value && password.value)
    entity.Password = password.value
  props.close({ action: 'save', entity })
}

function removeFromDraft() {
  props.close({ action: 'remove' })
}
</script>

<template>
  <ModalLayout
    :close="close"
    :title="entryKey"
    :sub-title="t(`admin.realmConfig.section.${section}`, {}, section)"
    :icon="icon"
    :footer-button="entity ? {
      visible: true,
      text: t('admin.realmConfig.entry.save', {}, 'Stage into draft'),
      onClick: save,
    } : undefined">
    <div class="entry-modal-body">
      <CoarNotice v-if="jsonError" variant="error">{{ jsonError }}</CoarNotice>

      <p v-for="(note, i) in planEntry?.Notes ?? []" :key="i" class="entry-note">{{ note }}</p>

      <CoarNotice v-for="(conflict, i) in entityConflicts" :key="`ec-${i}`" variant="warning">
        {{ conflictLabel(conflict.Kind) }}
      </CoarNotice>

      <!-- Field-level three-way conflicts with inline "take live" resolution. -->
      <section v-if="fieldConflicts.length > 0" class="conflict-block">
        <h3 class="block-title">
          <CoarIcon name="shield-alert" size="s" />
          {{ t('admin.realmConfig.entry.conflicts', {}, 'Conflicts') }}
        </h3>
        <div v-for="conflict in fieldConflicts" :key="conflict.Field!" class="conflict-row">
          <div class="conflict-info">
            <span class="conflict-field">{{ conflict.Field }}</span>
            <CoarTag size="s" variant="warning">{{ conflictLabel(conflict.Kind) }}</CoarTag>
            <div class="conflict-values">
              <span>{{ t('admin.realmConfig.conflict.live', {}, 'Live') }}: <code>{{ formatValue(conflict.Live) }}</code></span>
              <span>{{ t('admin.realmConfig.conflict.draft', {}, 'Draft') }}: <code>{{ formatValue(conflict.Draft) }}</code></span>
            </div>
          </div>
          <CoarButton size="s" variant="secondary" @click="takeLive(conflict)">
            {{ t('admin.realmConfig.conflict.takeLive', {}, 'Take live value') }}
          </CoarButton>
        </div>
        <p class="conflict-hint">
          {{ t('admin.realmConfig.conflict.keepMineHint', {}, '“Keep mine” = leave the value and use “Confirm remaining differences” (rebase) in the workspace.') }}
        </p>
      </section>

      <!-- Staged changes of this entry. -->
      <section v-if="(planEntry?.Changes.length ?? 0) > 0" class="changes-block">
        <h3 class="block-title">{{ t('admin.realmConfig.entry.changes', {}, 'Staged changes') }}</h3>
        <table class="changes-table">
          <thead>
            <tr>
              <th>{{ t('admin.realmConfig.col.field', {}, 'Field') }}</th>
              <th>{{ t('admin.realmConfig.col.current', {}, 'Current') }}</th>
              <th>{{ t('admin.realmConfig.col.desired', {}, 'New') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="change in planEntry!.Changes" :key="change.Field">
              <td class="change-field">{{ change.Field }}</td>
              <td><code class="change-value">{{ formatValue(change.Current) }}</code></td>
              <td><code class="change-value is-desired">{{ formatValue(change.Desired) }}</code></td>
            </tr>
          </tbody>
        </table>
      </section>

      <!-- Write-only secrets: what is staged + (for users) staging a new one. -->
      <section v-if="stagedSecretSlots.length > 0 || isUserEntry" class="secret-block">
        <h3 class="block-title">{{ t('admin.realmConfig.entry.secrets', {}, 'Secrets') }}</h3>
        <p v-for="slot in stagedSecretSlots" :key="slot" class="secret-set">
          <CoarIcon name="circle-check" size="s" />
          {{ t('admin.realmConfig.entry.secretSet', { slot: slot.split('/').pop() ?? slot }, `${slot.split('/').pop()} staged (value not shown)`) }}
        </p>
        <div v-if="isUserEntry" class="password-row">
          <CoarPasswordInput
            :model-value="password"
            size="s"
            :placeholder="t('admin.realmConfig.passwordPlaceholder', {}, 'New password…')"
            @update:model-value="password = $event" />
        </div>
      </section>

      <!-- The entity itself — generic JSON editing until the typed modal exists. -->
      <section v-if="entity" class="json-block">
        <h3 class="block-title">{{ t('admin.realmConfig.entry.json', {}, 'Entry (JSON)') }}</h3>
        <textarea v-model="entityJson" class="entity-editor" spellcheck="false" />
      </section>
      <CoarNotice v-else variant="info">
        {{ t('admin.realmConfig.entry.notInDraft', {}, 'This entity exists live but is not part of the draft — with prune enabled, applying deletes it.') }}
      </CoarNotice>

      <div v-if="entity" class="danger-row">
        <CoarButton size="s" variant="ghost" @click="removeFromDraft">
          {{ t('admin.realmConfig.entry.remove', {}, 'Remove from draft') }}
        </CoarButton>
      </div>
    </div>
  </ModalLayout>
</template>

<style scoped>
.entry-modal-body {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  width: 100%;
  height: 100%;
  min-height: 0;
  overflow-y: auto;
}

.block-title {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  margin: 0 0 0.4rem;
  color: var(--coar-text-neutral-secondary, #525e76);
  font-size: 0.72rem;
  font-weight: 650;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.entry-note {
  margin: 0;
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.78rem;
}

.conflict-block {
  border: 1px solid var(--coar-border-semantic-warning, #f59e0b);
  border-radius: 0.5rem;
  padding: 0.6rem 0.75rem;
}

.conflict-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  padding: 0.35rem 0;
}

.conflict-row + .conflict-row {
  border-top: 1px solid var(--coar-border-neutral-subtle, #eef0f3);
}

.conflict-info {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
  min-width: 0;
}

.conflict-field {
  font-weight: 600;
  font-size: 0.8rem;
}

.conflict-values {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.74rem;
}

.conflict-hint {
  margin: 0.4rem 0 0;
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.72rem;
}

.changes-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.76rem;
  table-layout: fixed;
}

.changes-table th {
  text-align: left;
  padding: 0.25rem 0.5rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-weight: 600;
  border-bottom: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
}

.changes-table td {
  padding: 0.3rem 0.5rem;
  vertical-align: top;
  border-bottom: 1px solid var(--coar-border-neutral-subtle, #eef0f3);
}

.change-field {
  font-weight: 600;
  overflow-wrap: anywhere;
}

.change-value {
  display: block;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-size: 0.72rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
}

.change-value.is-desired {
  color: var(--coar-text-semantic-success, #047857);
}

.secret-set {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  margin: 0;
  font-size: 0.78rem;
}

.password-row {
  max-width: 20rem;
}

.entity-editor {
  width: 100%;
  min-height: 14rem;
  resize: vertical;
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  border-radius: 0.5rem;
  background: var(--coar-background-neutral-primary, #fff);
  color: var(--coar-text-neutral-primary, #111827);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.76rem;
  line-height: 1.5;
}

.danger-row {
  display: flex;
  justify-content: flex-end;
}
</style>
