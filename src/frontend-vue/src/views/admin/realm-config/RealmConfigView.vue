<script setup lang="ts">
/**
 * Configuration draft workspace (ADR-0005 Phase 1b).
 *
 * Two modes: the DRAFT LIST (create from export / empty / uploaded JSON, open,
 * delete, plus export & schema downloads) and the WORKSPACE for one open draft —
 * every resource as a card with its plan action, conflicts surfaced with
 * resolution paths (per-field "take live" in the entry modal, global rebase for
 * "keep mine"), and Apply as the single commit point, gated on a fresh,
 * conflict-free plan (the server enforces the same gate with 409+plan).
 */
import { computed, ref, watch } from 'vue'
import {
  CoarButton,
  CoarCheckbox,
  CoarIcon,
  CoarNotice,
  CoarPopconfirm,
  CoarSelect,
  CoarSpinner,
  CoarSwitch,
  CoarTag,
  CoarTextInput,
  useToast,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useHttpClient } from '@/composables/useHttpClient'
import { useModalOverlay } from '@/composables/useModalOverlay'
import { MODAL_LG } from '@/router/modal-sizes'
import {
  SECTION_META,
  draftErrorMessage,
  useRealmDraftStore,
  type ManifestEntity,
  type PlanAction,
  type PlanEntry,
} from '@/stores/realmDraft.store'
import DraftEntryModal, { type DraftEntryModalResult } from './DraftEntryModal.vue'

const { t, language } = useI18n()
const ui = useUI()
const toast = useToast()
const store = useRealmDraftStore()
const modal = useModalOverlay()
const configHttp = useHttpClient('/api/admin/realm-config')

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.realmConfig.title', {}, 'Configuration Drafts')
  ctx.header.icon = 'file-json'
  ctx.content.container = false
  ctx.content.hasSubNav = true
}), { immediate: true })

void store.loadDrafts()

// ── Draft list: creation form ─────────────────────────────────────────────────

const newName = ref('')
const newSource = ref<'export' | 'empty' | 'manifest'>('export')
const creating = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)
const uploadedManifest = ref<unknown | null>(null)
const uploadedFileName = ref<string | null>(null)

const sourceOptions = computed(() => [
  { value: 'export', label: t('admin.realmConfig.source.export', {}, 'Current configuration (export)') },
  { value: 'empty', label: t('admin.realmConfig.source.empty', {}, 'Empty') },
  { value: 'manifest', label: t('admin.realmConfig.source.manifest', {}, 'Uploaded JSON (file / AI)') },
])

async function onFileChosen(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  if (!file) return
  try {
    uploadedManifest.value = JSON.parse(await file.text())
    uploadedFileName.value = file.name
    store.error = null
  } catch (e) {
    store.error = t('admin.realmConfig.invalidJson', {}, 'The manifest is not valid JSON: ') + String(e)
  }
  ;(event.target as HTMLInputElement).value = ''
}

const canCreate = computed(() =>
  newName.value.trim().length > 0 &&
  (newSource.value !== 'manifest' || uploadedManifest.value !== null))

async function createDraft() {
  if (!canCreate.value) return
  creating.value = true
  try {
    await store.createDraft(newName.value.trim(), newSource.value,
      newSource.value === 'manifest' ? uploadedManifest.value : undefined)
    newName.value = ''
    uploadedManifest.value = null
    uploadedFileName.value = null
  } catch (e) {
    store.error = draftErrorMessage(e)
  } finally {
    creating.value = false
  }
}

function downloadBlob(name: string, content: string) {
  const url = URL.createObjectURL(new Blob([content], { type: 'application/json' }))
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = name
  anchor.click()
  URL.revokeObjectURL(url)
}

async function downloadExport() {
  try {
    const exported = await configHttp.addPath('export').get<{ Realm?: { Slug?: string } }>()
    downloadBlob(`realm-${exported.Realm?.Slug ?? 'export'}.json`, JSON.stringify(exported, null, 2))
  } catch (e) {
    store.error = draftErrorMessage(e)
  }
}

async function downloadSchema() {
  try {
    const schema = await configHttp.addPath('manifest-schema').get<unknown>()
    downloadBlob('realm-manifest-schema.json', JSON.stringify(schema, null, 2))
  } catch (e) {
    store.error = draftErrorMessage(e)
  }
}

// ── Workspace: cards ──────────────────────────────────────────────────────────

/** Same icons as the admin sidebar's list views. */
const SECTION_ICONS: Record<string, string> = {
  settings: 'sliders-horizontal',
  apps: 'layout-grid',
  apis: 'server',
  scopes: 'tags',
  clients: 'app-window',
  loginProviders: 'log-in',
  roles: 'shield',
  users: 'users',
  groups: 'users-round',
  positions: 'briefcase',
}

const ACTION_VARIANTS: Record<PlanAction, 'neutral' | 'success' | 'warning' | 'error' | 'info' | 'accent'> = {
  create: 'success',
  update: 'info',
  unchanged: 'neutral',
  delete: 'error',
  protected: 'warning',
  error: 'error',
}

function actionLabel(action: PlanAction): string {
  switch (action) {
    case 'create': return t('admin.realmConfig.action.create', {}, 'Create')
    case 'update': return t('admin.realmConfig.action.update', {}, 'Update')
    case 'unchanged': return t('admin.realmConfig.action.unchanged', {}, 'Unchanged')
    case 'delete': return t('admin.realmConfig.action.delete', {}, 'Delete')
    case 'protected': return t('admin.realmConfig.action.protected', {}, 'Protected')
    case 'error': return t('admin.realmConfig.action.error', {}, 'Error')
  }
}

function sectionLabel(name: string): string {
  return t(`admin.realmConfig.section.${name}`, {}, {
    settings: 'Realm settings', apps: 'Applications', apis: 'OAuth APIs',
    scopes: 'OAuth scopes', clients: 'OAuth clients', loginProviders: 'Login providers',
    roles: 'Roles', users: 'Users', groups: 'Groups', positions: 'Positions',
  }[name] ?? name)
}

/** The card's info lines: the entity's most important fields per section. */
function cardInfo(section: string, entry: PlanEntry): string[] {
  const e = store.findEntity(section, entry.Key)
  if (!e) {
    return entry.Action === 'delete' || entry.Action === 'protected'
      ? [t('admin.realmConfig.card.liveOnly', {}, 'Live only — not in this draft')]
      : []
  }
  const s = (v: unknown) => (typeof v === 'string' && v.length > 0 ? v : null)
  const n = (v: unknown) => (Array.isArray(v) ? v.length : 0)
  switch (section) {
    case 'settings':
      return [t('admin.realmConfig.card.settings', {}, 'Realm-wide settings patch')]
    case 'apps':
      return [s(e.DisplayName), `${n(e.Permissions)} ${t('admin.realmConfig.card.permissions', {}, 'permissions')}`].filter(Boolean) as string[]
    case 'apis':
      return [s(e.DisplayName), (e.Scopes as string[] | undefined)?.join(', ') ?? null].filter(Boolean) as string[]
    case 'scopes':
      return [s(e.DisplayName), (e.Resources as string[] | undefined)?.join(', ') ?? null].filter(Boolean) as string[]
    case 'clients':
      return [s(e.ClientType), (e.Scopes as string[] | undefined)?.join(', ') ?? null].filter(Boolean) as string[]
    case 'loginProviders':
      return [s(e.DisplayName), s(e.Flavor)].filter(Boolean) as string[]
    case 'roles':
      return [s(e.App) ?? (e.IsRealmAdmin ? 'realm:admin' : null), `${n(e.Permissions)} ${t('admin.realmConfig.card.permissions', {}, 'permissions')}`].filter(Boolean) as string[]
    case 'users':
      return [[s(e.Firstname), s(e.Lastname)].filter(Boolean).join(' ') || null, s(e.Email)].filter(Boolean) as string[]
    case 'groups':
      return [`${n(e.Members)} ${t('admin.realmConfig.card.members', {}, 'members')}`, (e.Roles as string[] | undefined)?.join(', ') ?? null].filter(Boolean) as string[]
    case 'positions':
      return [s(e.Purpose), `${n(e.Grants)} ${t('admin.realmConfig.card.grants', {}, 'grants')}`].filter(Boolean) as string[]
    default:
      return []
  }
}

const actionCounts = computed(() => {
  const counts: Record<PlanAction, number> = { create: 0, update: 0, delete: 0, protected: 0, unchanged: 0, error: 0 }
  for (const section of store.plan?.Sections ?? [])
    for (const entry of section.Entries) counts[entry.Action]++
  return counts
})

const conflictCount = computed(() =>
  (store.plan?.Sections ?? []).reduce(
    (sum, s) => sum + s.Entries.reduce((n, e) => n + e.Conflicts.length, 0), 0))

const visibleSections = computed(() => (store.plan?.Sections ?? [])
  .filter((s) => s.Entries.length > 0 || s.Name !== 'settings'))

/** Minimal skeletons for "add entity" — the modal's JSON editor fills the rest. */
const NEW_ENTITY_TEMPLATES: Record<string, ManifestEntity> = {
  apps: { Slug: '', DisplayName: '', Permissions: [] },
  apis: { Name: '' },
  scopes: { Name: '' },
  clients: { ClientId: '', ClientType: 'public', Scopes: [], AllowedGrantTypes: [] },
  loginProviders: { Slug: '', Flavor: '', DisplayName: '' },
  roles: { Name: '', Permissions: [] },
  users: { Email: '' },
  groups: { Name: '', Members: [], Roles: [] },
  positions: { AccountName: '' },
}

async function openEntry(section: string, entry: PlanEntry) {
  const entity = store.findEntity(section, entry.Key)
  const result = await modal.open<DraftEntryModalResult>(DraftEntryModal, MODAL_LG, {
    section,
    entryKey: entry.Key,
    icon: SECTION_ICONS[section] ?? 'file-json',
    entity: entity ? (JSON.parse(JSON.stringify(entity)) as ManifestEntity) : null,
    planEntry: JSON.parse(JSON.stringify(entry)),
    secretSlots: [...(store.current?.SecretSlots ?? [])],
  })
  await handleModalResult(section, entry.Key, result)
}

async function addEntity(section: string) {
  const template = NEW_ENTITY_TEMPLATES[section]
  if (!template) return
  const result = await modal.open<DraftEntryModalResult>(DraftEntryModal, MODAL_LG, {
    section,
    entryKey: t('admin.realmConfig.card.new', {}, 'New entry'),
    icon: SECTION_ICONS[section] ?? 'file-json',
    entity: JSON.parse(JSON.stringify(template)) as ManifestEntity,
    planEntry: null,
    secretSlots: [],
  })
  if (result?.action === 'save' && result.entity) {
    const key = SECTION_META[section]?.key(result.entity)
    if (!key) {
      store.error = t('admin.realmConfig.card.keyMissing', {}, 'The entry needs its natural key (slug / name / id) before it can be staged.')
      return
    }
    await store.upsertEntity(section, key, result.entity)
  }
}

async function handleModalResult(section: string, key: string, result?: DraftEntryModalResult) {
  if (!result) return
  if (result.action === 'remove') {
    await store.removeEntity(section, key)
    return
  }
  if (result.entity) {
    // Replace at the ORIGINAL key — if the entity's key field was edited, the
    // staged entity carries the new key and the re-plan shows the rename as
    // create (+ prune delete of the old one).
    await store.upsertEntity(section, key, result.entity)
  }
}

// ── Workspace: header actions ─────────────────────────────────────────────────

async function applyDraft() {
  const ok = await store.apply()
  if (ok) toast.success(t('admin.realmConfig.applied', {}, 'Draft applied.'))
}

const secretEntries = computed(() => Object.entries(store.applyOutcome?.ClientSecrets ?? {}))

async function copySecret(value: string) {
  await navigator.clipboard.writeText(value)
  toast.success(t('common.copied', {}, 'Copied.'))
}

function formatDate(value: string): string {
  return new Date(value).toLocaleString()
}
</script>

<template>
  <div class="realm-config-page">
    <div class="realm-config-shell">
      <CoarNotice v-if="store.error" variant="error">{{ store.error }}</CoarNotice>

      <!-- One-time client secrets from the last apply. -->
      <CoarNotice v-if="store.applyOutcome && secretEntries.length > 0" variant="warning">
        <div class="secrets-block">
          <p class="secrets-title">
            {{ t('admin.realmConfig.secretsTitle', {}, 'New client secrets — shown only once, copy them now:') }}
          </p>
          <div v-for="[clientId, secret] in secretEntries" :key="clientId" class="secret-row">
            <code class="secret-client">{{ clientId }}</code>
            <code class="secret-value">{{ secret }}</code>
            <CoarButton size="s" variant="ghost" icon-start="copy" @click="copySecret(secret)">
              {{ t('common.copy', {}, 'Copy') }}
            </CoarButton>
          </div>
        </div>
      </CoarNotice>
      <CoarNotice v-else-if="store.applyOutcome" variant="success">
        {{ t('admin.realmConfig.appliedNotice', {}, 'The draft was applied to this realm.') }}
      </CoarNotice>

      <!-- ═══ Draft list ═══ -->
      <template v-if="!store.current">
        <CoarNotice variant="info">
          {{ t('admin.realmConfig.intro', {}, 'Configuration drafts stage changes to this realm — create entities, wire them together, review the exact change plan, and nothing takes effect until you apply. Drafts are private to you unless shared.') }}
        </CoarNotice>

        <div class="create-row">
          <CoarTextInput
            v-model="newName"
            size="s"
            class="create-name"
            :placeholder="t('admin.realmConfig.newDraftName', {}, 'Draft name…')" />
          <CoarSelect v-model="newSource" size="s" :options="sourceOptions" class="create-source" />
          <template v-if="newSource === 'manifest'">
            <input ref="fileInput" type="file" accept="application/json,.json" class="hidden-input" @change="onFileChosen" />
            <CoarButton size="s" variant="secondary" icon-start="upload" @click="fileInput?.click()">
              {{ uploadedFileName ?? t('admin.realmConfig.uploadFile', {}, 'Upload JSON') }}
            </CoarButton>
          </template>
          <CoarButton variant="primary" size="s" :loading="creating" :disabled="!canCreate" @click="createDraft">
            {{ t('admin.realmConfig.createDraft', {}, 'Create draft') }}
          </CoarButton>
          <span class="toolbar-spacer" />
          <CoarButton size="s" variant="ghost" icon-start="download" @click="downloadExport">
            {{ t('admin.realmConfig.downloadExport', {}, 'Download export') }}
          </CoarButton>
          <CoarButton size="s" variant="ghost" icon-start="download" @click="downloadSchema">
            {{ t('admin.realmConfig.downloadSchema', {}, 'Download schema') }}
          </CoarButton>
        </div>

        <div class="draft-list">
          <div v-if="store.listLoading" class="muted">{{ t('common.loading', {}, 'Loading...') }}</div>
          <div v-else-if="store.drafts.length === 0" class="muted">
            {{ t('admin.realmConfig.noDrafts', {}, 'No drafts yet — create one above.') }}
          </div>
          <button
            v-for="draft in store.drafts"
            :key="draft.Id"
            type="button"
            class="draft-row"
            @click="store.openDraft(draft.Id)">
            <CoarIcon name="file-json" size="s" />
            <span class="draft-name">{{ draft.Name }}</span>
            <CoarTag v-if="draft.Shared" size="s" variant="info">
              {{ t('admin.realmConfig.shared', {}, 'Shared') }}
            </CoarTag>
            <CoarTag v-else-if="draft.Mine" size="s" variant="neutral">
              {{ t('admin.realmConfig.private', {}, 'Private') }}
            </CoarTag>
            <span class="draft-meta">
              {{ t('admin.realmConfig.modifiedBy', { name: draft.LastModifiedByName }, `by ${draft.LastModifiedByName}`) }}
              · {{ formatDate(draft.LastModifiedAt) }} · v{{ draft.Version }}
            </span>
            <CoarPopconfirm
              :title="t('admin.realmConfig.deleteDraftTitle', {}, 'Delete draft?')"
              :message="t('admin.realmConfig.deleteDraftConfirm', {}, 'The staged changes are discarded. The realm itself is untouched.')"
              confirm-variant="danger"
              @confirmed="store.deleteDraft(draft.Id)">
              <CoarButton size="s" variant="ghost" @click.stop>
                {{ t('common.delete', {}, 'Delete') }}
              </CoarButton>
            </CoarPopconfirm>
          </button>
        </div>
      </template>

      <!-- ═══ Workspace ═══ -->
      <template v-else>
        <div class="workspace-header">
          <CoarButton size="s" variant="ghost" icon-start="chevron-right" class="back-button" @click="store.closeDraft()">
            {{ t('admin.realmConfig.parkToList', {}, 'Park & back to list') }}
          </CoarButton>
          <CoarIcon name="file-json" size="s" />
          <span class="draft-name">{{ store.current.Name }}</span>
          <span class="draft-meta">
            v{{ store.current.Version }} ·
            {{ t('admin.realmConfig.modifiedBy', { name: store.current.LastModifiedByName }, `by ${store.current.LastModifiedByName}`) }}
          </span>
          <span class="toolbar-spacer" />
          <CoarSwitch
            :model-value="store.current.Shared"
            :label="t('admin.realmConfig.shareToggle', {}, 'Share with realm admins')"
            @update:model-value="store.updateDraft({ Shared: $event })" />
        </div>

        <div class="plan-bar">
          <template v-if="store.planning">
            <CoarSpinner size="s" />
            <span class="muted">{{ t('admin.realmConfig.planning', {}, 'Computing the change plan…') }}</span>
          </template>
          <template v-else-if="store.plan">
            <CoarTag v-if="actionCounts.create" variant="success" size="s">{{ actionCounts.create }} {{ t('admin.realmConfig.summary.create', {}, 'create') }}</CoarTag>
            <CoarTag v-if="actionCounts.update" variant="info" size="s">{{ actionCounts.update }} {{ t('admin.realmConfig.summary.update', {}, 'update') }}</CoarTag>
            <CoarTag v-if="actionCounts.delete" variant="error" size="s">{{ actionCounts.delete }} {{ t('admin.realmConfig.summary.delete', {}, 'delete') }}</CoarTag>
            <CoarTag v-if="actionCounts.protected" variant="warning" size="s">{{ actionCounts.protected }} {{ t('admin.realmConfig.summary.protected', {}, 'protected') }}</CoarTag>
            <CoarTag v-if="actionCounts.error" variant="error" size="s">{{ actionCounts.error }} {{ t('admin.realmConfig.summary.error', {}, 'error') }}</CoarTag>
            <CoarTag variant="neutral" size="s">{{ actionCounts.unchanged }} {{ t('admin.realmConfig.summary.unchanged', {}, 'unchanged') }}</CoarTag>
            <CoarTag v-if="conflictCount > 0" variant="warning" size="s">
              <CoarIcon name="shield-alert" size="s" />
              {{ conflictCount }} {{ t('admin.realmConfig.summary.conflicts', {}, 'conflicts') }}
            </CoarTag>
          </template>
          <span class="toolbar-spacer" />
          <CoarCheckbox
            v-model="store.prune"
            :label="t('admin.realmConfig.prune', {}, 'Prune — also delete entities missing from the draft')"
            @update:model-value="store.replan()" />
          <CoarPopconfirm
            :title="t('admin.realmConfig.applyConfirmTitle', {}, 'Apply this draft?')"
            :message="store.prune
              ? t('admin.realmConfig.applyConfirmPrune', {}, 'The staged changes are applied AND missing entities are deleted (full sync). One transaction — all or nothing.')
              : t('admin.realmConfig.applyConfirm', {}, 'The staged changes are applied to this realm in one transaction — all or nothing.')"
            :confirm-variant="store.prune ? 'danger' : 'primary'"
            @confirmed="applyDraft">
            <CoarButton
              :variant="store.prune ? 'danger' : 'primary'"
              size="s"
              :loading="store.applying"
              :disabled="!store.canApply || store.pendingCount === 0">
              {{ store.pendingCount > 0
                ? t('admin.realmConfig.applyCount', { count: store.pendingCount }, `Apply draft (${store.pendingCount})`)
                : t('admin.realmConfig.apply', {}, 'Apply draft') }}
            </CoarButton>
          </CoarPopconfirm>
        </div>

        <CoarNotice v-if="store.prune" variant="warning">
          {{ t('admin.realmConfig.pruneHint', {}, 'Prune turns the apply into a full sync: everything in this realm that is missing from the draft gets deleted (system entities and realm admins are protected). Review the plan carefully.') }}
        </CoarNotice>
        <CoarNotice v-if="conflictCount > 0" variant="warning">
          <div class="conflict-banner">
            <span>
              {{ t('admin.realmConfig.conflictBanner', {}, 'Live configuration changed while this draft was open. Resolve each conflict in its card (take live), or confirm the remaining differences as intentional:') }}
            </span>
            <CoarButton size="s" variant="secondary" :loading="store.saving" @click="store.rebase()">
              {{ t('admin.realmConfig.rebase', {}, 'Confirm remaining differences (rebase)') }}
            </CoarButton>
          </div>
        </CoarNotice>
        <CoarNotice v-if="store.planHasErrors" variant="error">
          {{ t('admin.realmConfig.planErrors', {}, 'The plan contains entries the apply would fail on (marked Error). Fix them before applying.') }}
        </CoarNotice>
        <CoarNotice v-for="(warning, i) in store.plan?.Warnings ?? []" :key="i" variant="warning">
          {{ warning }}
        </CoarNotice>

        <div class="workspace-scroll">
          <section v-for="section in visibleSections" :key="section.Name" class="plan-section">
            <h2 class="section-title">
              <CoarIcon :name="SECTION_ICONS[section.Name] ?? 'file-json'" size="s" />
              {{ sectionLabel(section.Name) }}
              <CoarButton
                v-if="NEW_ENTITY_TEMPLATES[section.Name]"
                size="s" variant="ghost"
                @click="addEntity(section.Name)">
                + {{ t('admin.realmConfig.card.add', {}, 'Add') }}
              </CoarButton>
            </h2>
            <div class="card-grid">
              <button
                v-for="entry in section.Entries"
                :key="entry.Key"
                type="button"
                class="entity-card"
                :class="{ 'is-unchanged': entry.Action === 'unchanged' && entry.Conflicts.length === 0 }"
                @click="openEntry(section.Name, entry)">
                <div class="card-head">
                  <CoarIcon :name="SECTION_ICONS[section.Name] ?? 'file-json'" size="s" class="card-icon" />
                  <span class="card-key">{{ entry.Key }}</span>
                </div>
                <div class="card-info">
                  <span v-for="(line, i) in cardInfo(section.Name, entry)" :key="i" class="card-info-line">{{ line }}</span>
                </div>
                <div class="card-tags">
                  <CoarTag :variant="ACTION_VARIANTS[entry.Action]" size="s">{{ actionLabel(entry.Action) }}</CoarTag>
                  <CoarTag v-if="entry.Conflicts.length > 0" variant="warning" size="s">
                    <CoarIcon name="shield-alert" size="s" />
                    {{ entry.Conflicts.length }}
                  </CoarTag>
                  <span v-if="entry.Changes.length > 0" class="card-changes">
                    {{ t('admin.realmConfig.fieldCount', { count: entry.Changes.length }, `${entry.Changes.length} field(s)`) }}
                  </span>
                </div>
              </button>
            </div>
          </section>

          <div class="workspace-footer">
            <CoarPopconfirm
              :title="t('admin.realmConfig.deleteDraftTitle', {}, 'Delete draft?')"
              :message="t('admin.realmConfig.deleteDraftConfirm', {}, 'The staged changes are discarded. The realm itself is untouched.')"
              confirm-variant="danger"
              @confirmed="store.deleteDraft(store.current!.Id)">
              <CoarButton size="s" variant="ghost">
                {{ t('admin.realmConfig.discardDraft', {}, 'Discard draft') }}
              </CoarButton>
            </CoarPopconfirm>
          </div>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.realm-config-page {
  display: flex;
  flex: 1;
  min-height: 0;
  min-width: 0;
  overflow: hidden;
  padding: 1rem;
}

.realm-config-shell {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  min-width: 0;
  max-width: 78rem;
  overflow: hidden;
  gap: 0.75rem;
}

.muted {
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.8rem;
}

.hidden-input {
  display: none;
}

.toolbar-spacer {
  flex: 1;
}

/* ── Draft list ── */

.create-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.create-name {
  width: 16rem;
}

.create-source {
  width: 16rem;
}

.draft-list {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  border-radius: 0.5rem;
  overflow-y: auto;
}

.draft-row {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  width: 100%;
  padding: 0.6rem 0.8rem;
  background: transparent;
  border: none;
  text-align: left;
  font: inherit;
  color: inherit;
  cursor: pointer;
}

.draft-row + .draft-row {
  border-top: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
}

.draft-row:hover {
  background: var(--coar-background-neutral-secondary, #f7f8fa);
}

.draft-name {
  font-weight: 600;
  font-size: 0.85rem;
}

.draft-meta {
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.74rem;
}

.draft-list .draft-meta {
  margin-left: auto;
}

/* ── Workspace ── */

.workspace-header {
  display: flex;
  align-items: center;
  gap: 0.6rem;
}

.back-button :deep(svg) {
  transform: rotate(180deg);
}

.plan-bar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.4rem;
}

.conflict-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  min-width: 0;
}

.workspace-scroll {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  gap: 1.1rem;
  overflow-y: auto;
  padding-right: 0.25rem;
  scrollbar-gutter: stable;
}

.plan-section {
  display: flex;
  flex-direction: column;
  gap: 0.45rem;
}

.section-title {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  margin: 0;
  color: var(--coar-text-neutral-secondary, #525e76);
  font-size: 0.75rem;
  font-weight: 650;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(15rem, 1fr));
  gap: 0.55rem;
}

.entity-card {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  padding: 0.6rem 0.7rem;
  border: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  border-radius: 0.55rem;
  background: var(--coar-background-neutral-primary, #fff);
  text-align: left;
  font: inherit;
  color: inherit;
  cursor: pointer;
  transition: border-color 120ms ease, box-shadow 120ms ease;
}

.entity-card:hover {
  border-color: var(--coar-border-accent, #6366f1);
  box-shadow: 0 1px 3px rgb(0 0 0 / 8%);
}

.entity-card.is-unchanged {
  opacity: 0.72;
}

.card-head {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  min-width: 0;
}

.card-icon {
  color: var(--coar-text-neutral-secondary, #6b7280);
  flex-shrink: 0;
}

.card-key {
  font-weight: 600;
  font-size: 0.82rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-info {
  display: flex;
  flex-direction: column;
  gap: 0.1rem;
  min-height: 1rem;
}

.card-info-line {
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.74rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-tags {
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.card-changes {
  margin-left: auto;
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.7rem;
}

.workspace-footer {
  display: flex;
  justify-content: flex-end;
  padding-bottom: 0.5rem;
}

/* ── Apply secrets notice ── */

.secrets-block {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  min-width: 0;
}

.secrets-title {
  margin: 0;
  font-weight: 600;
}

.secret-row {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  min-width: 0;
}

.secret-client {
  font-weight: 600;
  white-space: nowrap;
}

.secret-value {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}
</style>
