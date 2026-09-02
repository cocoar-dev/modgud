<script setup lang="ts">
/**
 * Selective ("cart") export — pick entities from the current export and
 * download a partial manifest for another realm/instance. The dependency
 * closure is computed live: whatever the checked entities reference
 * (transitively) is pulled in automatically and shown as "required", so the
 * downloaded manifest always applies cleanly. Nothing here writes anywhere —
 * the output is a JSON download; the target imports it as a draft, reviews
 * the plan and applies.
 */
import { computed, ref } from 'vue'
import { CoarButton, CoarCheckbox, CoarIcon, CoarNotice, CoarTag, CoarTextInput } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { SECTION_META, type DraftManifest, type ManifestEntity } from '@/stores/realmDraft.store'
import {
  SELECTABLE_SECTIONS,
  buildSelectiveManifest,
  computeClosure,
  relatedToApp,
  selectionKey,
  type SelectableSection,
  type SelectionKey,
} from './manifestSelection'

const props = defineProps<{
  close: (result?: unknown) => void
  manifest: DraftManifest
}>()

const { t } = useI18n()

const includeUsers = ref(false)
const includeSettings = ref(false)
const targetSlug = ref(String(props.manifest.Realm?.Slug ?? ''))
const selected = ref<Set<SelectionKey>>(new Set())

const SECTION_ICONS: Record<string, string> = {
  apps: 'layout-grid', apis: 'server', scopes: 'tags', clients: 'app-window',
  roles: 'shield', groups: 'users-round', users: 'users',
  loginProviders: 'log-in', positions: 'briefcase',
}

interface Row {
  key: string
  sel: SelectionKey
  info: string | null
}

const sections = computed(() => SELECTABLE_SECTIONS
  .map((section) => {
    const meta = SECTION_META[section]!
    const entities = (props.manifest[meta.collection!] as ManifestEntity[] | undefined) ?? []
    const rows: Row[] = entities.map((e) => {
      const key = meta.key(e)
      return { key, sel: selectionKey(section, key), info: rowInfo(section, e) }
    })
    return { section, rows }
  })
  .filter((s) => s.rows.length > 0))

function rowInfo(section: SelectableSection, e: ManifestEntity): string | null {
  const s = (v: unknown) => (typeof v === 'string' && v.length > 0 ? v : null)
  switch (section) {
    case 'apps': return s(e.DisplayName)
    case 'apis': return s(e.DisplayName) ?? s(e.App)
    case 'scopes': return s(e.DisplayName) ?? s(e.App)
    case 'clients': return s(e.DisplayName) ?? s(e.ClientType)
    case 'roles': return s(e.App) ?? (e.IsRealmAdmin === true ? 'realm:admin' : null)
    case 'groups': return (e.Roles as string[] | undefined)?.join(', ') || null
    case 'users': return s(e.Email)
    case 'loginProviders': return s(e.DisplayName)
    case 'positions': return s(e.Purpose)
    default: return null
  }
}

// ── Selection + closure ──────────────────────────────────────────────────────

const closure = computed(() =>
  computeClosure(props.manifest, selected.value, { includeUsers: includeUsers.value }))

const effectiveSelection = computed<Set<SelectionKey>>(() => {
  const all = new Set(selected.value)
  for (const key of closure.value.keys()) all.add(key)
  return all
})

function isChecked(sel: SelectionKey): boolean {
  return effectiveSelection.value.has(sel)
}

/** Required by the closure but not explicitly checked — locked on. */
function isRequired(sel: SelectionKey): boolean {
  return closure.value.has(sel) && !selected.value.has(sel)
}

function requiredBy(sel: SelectionKey): string {
  const by = closure.value.get(sel)
  if (!by) return ''
  return [...by].map((k) => k.slice(k.indexOf('/') + 1)).join(', ')
}

function toggle(sel: SelectionKey, on: boolean) {
  const next = new Set(selected.value)
  if (on) next.add(sel)
  else next.delete(sel)
  selected.value = next
}

function sectionAllChecked(rows: Row[]): boolean {
  return rows.every((r) => isChecked(r.sel))
}

function toggleSection(rows: Row[], on: boolean) {
  const next = new Set(selected.value)
  for (const r of rows) {
    if (on) next.add(r.sel)
    else next.delete(r.sel)
  }
  selected.value = next
}

/** "Everything belonging to this app": check the app plus its clients, APIs,
 * scopes and roles — their own references then flow through the closure. */
function selectAppBundle(appKey: string) {
  const next = new Set(selected.value)
  next.add(selectionKey('apps', appKey))
  for (const ref of relatedToApp(props.manifest, appKey)) next.add(selectionKey(ref.section, ref.key))
  selected.value = next
}

const totalCount = computed(() => effectiveSelection.value.size + (includeSettings.value ? 1 : 0))
const canDownload = computed(() => totalCount.value > 0 && targetSlug.value.trim().length > 0)

// ── Download ─────────────────────────────────────────────────────────────────

function download() {
  if (!canDownload.value) return
  const partial = buildSelectiveManifest(props.manifest, effectiveSelection.value, {
    includeUsers: includeUsers.value,
    includeSettings: includeSettings.value,
    targetSlug: targetSlug.value.trim(),
  })
  const url = URL.createObjectURL(new Blob(
    [JSON.stringify(partial, null, 2)], { type: 'application/json' }))
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = `realm-${targetSlug.value.trim()}-partial.json`
  anchor.click()
  URL.revokeObjectURL(url)
  props.close()
}

function sectionLabel(name: string): string {
  return t(`admin.realmConfig.section.${name}`, {}, {
    apps: 'Applications', apis: 'OAuth APIs', scopes: 'OAuth scopes',
    clients: 'OAuth clients', loginProviders: 'Login providers',
    roles: 'Roles', users: 'Users', groups: 'Groups', positions: 'Positions',
  }[name] ?? name)
}
</script>

<template>
  <ModalLayout
    :close="close"
    :title="t('admin.realmConfig.selective.title', {}, 'Selective export')"
    :sub-title="t('admin.realmConfig.selective.subTitle', {}, 'Pick entities — references are included automatically')"
    icon="file-json"
    :footer-button="{
      visible: true,
      disabled: !canDownload,
      text: `${t('admin.realmConfig.selective.download', {}, 'Download manifest')} (${totalCount})`,
      onClick: download,
    }">
    <div class="selective-body">
      <CoarNotice variant="info" truncate>
        {{ t('admin.realmConfig.selective.hint', {},
          'Secrets are never exported. Apply the partial manifest on the target WITHOUT prune — with prune it would delete everything not in the file.') }}
      </CoarNotice>

      <div class="selective-options">
        <div class="option-slug">
          <label class="option-label">{{ t('admin.realmConfig.selective.targetSlug', {}, 'Target realm slug') }}</label>
          <CoarTextInput v-model="targetSlug" />
        </div>
        <CoarCheckbox
          v-model="includeSettings"
          :label="t('admin.realmConfig.selective.includeSettings', {}, 'Include realm settings')" />
        <CoarCheckbox
          v-model="includeUsers"
          :label="t('admin.realmConfig.selective.includeUsers', {}, 'Include user references (group members, position grants)')" />
      </div>

      <section v-for="{ section, rows } in sections" :key="section" class="selective-section">
        <div class="section-head">
          <CoarCheckbox
            :model-value="sectionAllChecked(rows)"
            @update:model-value="(v: boolean) => toggleSection(rows, v)" />
          <CoarIcon :name="SECTION_ICONS[section] ?? 'file-json'" size="s" />
          <span class="section-title">{{ sectionLabel(section) }}</span>
          <span class="section-count">{{ rows.filter((r) => isChecked(r.sel)).length }} / {{ rows.length }}</span>
        </div>
        <div v-for="row in rows" :key="row.sel" class="entity-row">
          <CoarCheckbox
            :model-value="isChecked(row.sel)"
            :disabled="isRequired(row.sel)"
            @update:model-value="(v: boolean) => toggle(row.sel, v)" />
          <span class="entity-key">{{ row.key }}</span>
          <span v-if="row.info" class="entity-info">{{ row.info }}</span>
          <CoarTag
            v-if="isRequired(row.sel)"
            size="s" variant="info"
            :title="t('admin.realmConfig.selective.requiredBy', {}, 'Required by: ') + requiredBy(row.sel)">
            {{ t('admin.realmConfig.selective.required', {}, 'Required') }}
          </CoarTag>
          <CoarButton
            v-if="section === 'apps'"
            size="s" variant="ghost"
            @click="selectAppBundle(row.key)">
            {{ t('admin.realmConfig.selective.selectRelated', {}, 'Select related') }}
          </CoarButton>
        </div>
      </section>
    </div>
  </ModalLayout>
</template>

<style scoped>
.selective-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 16px;
  overflow-y: auto;
}
.selective-options {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 16px;
}
.option-slug {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 220px;
}
.option-label {
  font-size: 12px;
  color: var(--coar-text-secondary, #6b7280);
}
.selective-section {
  border: 1px solid var(--coar-border-neutral, #e5e7eb);
  border-radius: 8px;
  padding: 8px 12px;
}
.section-head {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 600;
  padding-bottom: 4px;
}
.section-count {
  margin-left: auto;
  font-size: 12px;
  font-weight: 400;
  color: var(--coar-text-secondary, #6b7280);
}
.entity-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 3px 0 3px 24px;
}
.entity-key {
  font-family: var(--coar-font-mono, monospace);
  font-size: 13px;
}
.entity-info {
  font-size: 12px;
  color: var(--coar-text-secondary, #6b7280);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.entity-row > .coar-button, .entity-row > .coar-tag {
  margin-left: auto;
}
</style>
