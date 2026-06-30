<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { CoarTextInput, CoarFormField, CoarNote, CoarButton, CoarTabGroup, CoarTab } from '@cocoar/vue-ui'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import AppSettingsSections from './AppSettingsSections.vue'
import { useApplicationsStore } from '@/stores/applications.store'
import type {
  ApplicationDto,
  ApplicationPermissionInputDto,
} from '@/models/application'

const { t } = useI18n()

// `id` from the routed modal is the App's Id (or "create" for a new one).
const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const id = computed(() => props.id)
const store = useApplicationsStore()
const isCreate = computed(() => id.value === 'create')
const loading = ref(false)
const error = ref<string | null>(null)

/** Per-permission reference info from a 409 conflict response. */
interface CatalogBlocker {
  PermissionId: string
  Permission: string
  ReferencedByRoles: string[]
  ReferencedByResourceServers: string[]
}

const catalogBlockers = ref<CatalogBlocker[]>([])

/**
 * One row in the catalog editor. Existing entries keep their server-issued
 * Id so role-grants and RS-subsets keep their FK targets stable across
 * resource/action renames; new rows use a transient `null` Id until first
 * save. The optional `originalKey` records the resource:action shape the
 * row started life with, so the rename-warning surfaces when the admin
 * edits an existing row's key.
 *
 * <c>_uid</c> is the grid's row-tracking key — always set, never sent to
 * the server. Existing rows reuse their server id; new rows get a
 * generated transient string. Without a stable id AG Grid loses scroll
 * position and editor focus on every reactive update.
 */
interface CatalogRow {
  _uid: string
  id: string | null
  resource: string
  action: string
  description: string
  originalKey: string | null
}

let catalogUidCounter = 0
const newCatalogUid = () => `catalog-${Date.now()}-${catalogUidCounter++}`

const catalog = ref<CatalogRow[]>([])

const SEGMENT_REGEX = /^[a-z0-9-]+$/

interface FormState {
  Slug: string
  DisplayName: string
  Description: string
}

const form = ref<FormState>({ Slug: '', DisplayName: '', Description: '' })
const dto = ref<ApplicationDto | null>(null)

function fromDto(d: ApplicationDto): { form: FormState; catalog: CatalogRow[] } {
  return {
    form: {
      Slug: d.Slug,
      DisplayName: d.DisplayName,
      Description: d.Description ?? '',
    },
    catalog: (d.Permissions ?? []).map((p) => ({
      _uid: p.Id ?? newCatalogUid(),
      id: p.Id,
      resource: p.Resource,
      action: p.Action,
      description: p.Description ?? '',
      originalKey: `${p.Resource}:${p.Action}`,
    })),
  }
}

function buildPermissionsPayload(): ApplicationPermissionInputDto[] {
  return catalog.value
    .filter((r) => r.resource.trim() && r.action.trim())
    .map((r) => ({
      Id: r.id ?? null,
      Resource: r.resource.trim(),
      Action: r.action.trim(),
      Description: r.description.trim() || null,
    }))
}

function addRow() {
  catalog.value = [...catalog.value, {
    _uid: newCatalogUid(),
    id: null,
    resource: '',
    action: '',
    description: '',
    originalKey: null,
  }]
}

function removeRow(row: CatalogRow) {
  catalog.value = catalog.value.filter((r) => r._uid !== row._uid)
}

function isSegmentValid(value: string): boolean {
  return value === '' || SEGMENT_REGEX.test(value)
}

/** True when the row's resource OR action diverges from the original. */
function rowRenamed(row: CatalogRow): boolean {
  if (!row.originalKey || !row.id) return false
  const currentKey = `${row.resource.trim()}:${row.action.trim()}`
  return currentKey !== row.originalKey
}

const renamedCount = computed(() => catalog.value.filter(rowRenamed).length)

/** Duplicate-key detection — sister rows that match resource:action. */
const duplicateKeys = computed(() => {
  const counts = new Map<string, number>()
  for (const r of catalog.value) {
    const key = `${r.resource.trim()}:${r.action.trim()}`
    if (key === ':') continue
    counts.set(key, (counts.get(key) ?? 0) + 1)
  }
  return new Set([...counts.entries()].filter(([, c]) => c > 1).map(([k]) => k))
})

const hasInvalidSegments = computed(() => catalog.value.some((r) =>
  (r.resource && !isSegmentValid(r.resource)) ||
  (r.action && !isSegmentValid(r.action))
))

const hasIncompleteRows = computed(() => catalog.value.some((r) =>
  // A row with one segment filled and the other blank is incomplete; both
  // blank we silently drop on save (treated as an empty placeholder).
  (r.resource.trim() === '' && r.action.trim() !== '') ||
  (r.resource.trim() !== '' && r.action.trim() === '')
))

const isSystem = computed(() => dto.value?.IsSystem === true)
const activeTab = ref<'general' | 'catalog' | 'settings'>('general')
const settingsRef = ref<InstanceType<typeof AppSettingsSections> | null>(null)

/**
 * Per-cell visual cue for the resource/action validation surface. The
 * editor doesn't block invalid input — the save button is the gate
 * (footerButton.disabled covers hasInvalidSegments / hasIncompleteRows /
 * duplicateKeys). The CSS class just paints the broken cells red so the
 * admin sees where the problem is without scrolling to the footer hint.
 */
function cellClassFor(field: 'resource' | 'action') {
  return (params: any): string => {
    const row = params.data as CatalogRow
    const value = (row[field] ?? '').trim()
    if (value && !isSegmentValid(value)) return 'catalog-cell--invalid'
    const key = `${row.resource.trim()}:${row.action.trim()}`
    if (key !== ':' && duplicateKeys.value.has(key)) return 'catalog-cell--duplicate'
    return ''
  }
}

const catalogBuilder = computed(() =>
  CoarGridBuilder.create<CatalogRow>()
    .rowDataRef(catalog)
    .option('getRowId', (p: any) => p.data._uid)
    .stopEditingWhenCellsLoseFocus(true)
    .columns([
      (col) =>
        col
          .text('resource', (c) => c.placeholder('user'))
          .editable(() => !isSystem.value)
          .header(t('admin.apps.cat.resource', {}, 'Resource'))
          .flex(1)
          .cellClass(cellClassFor('resource')),
      (col) =>
        col
          .text('action', (c) => c.placeholder('read'))
          .editable(() => !isSystem.value)
          .header(t('admin.apps.cat.action', {}, 'Action'))
          .flex(1)
          .cellClass(cellClassFor('action')),
      (col) =>
        col
          .wrap(
            col
              .text('description', (c) =>
                c.placeholder(t('admin.apps.cat.descriptionPlaceholder', {}, 'optional')),
              )
              .editable(() => !isSystem.value)
              .header(t('admin.apps.cat.description', {}, 'Beschreibung'))
              .flex(2),
          )
          .right([
            // Renamed-Indicator: existing row whose resource/action diverges
            // from its server-issued key. IdP keeps Id stable; we just
            // surface that the wire-string changed so admins know consumers
            // doing `.includes("user:write")` will see a different value.
            {
              icon: 'pencil',
              size: 's',
              color: 'var(--coar-text-semantic-warning, #b45309)',
              tooltip: t(
                'admin.apps.cat.renamedTitle',
                {},
                'Umbenannt — Id bleibt stabil',
              ),
              show: (row) => rowRenamed(row),
            },
            {
              icon: 'trash-2',
              size: 's',
              color: 'var(--coar-text-neutral-secondary, #9ca3af)',
              tooltip: t('admin.apps.cat.removeTitle', {}, 'Eintrag entfernen'),
              show: () => !isSystem.value,
              onClick: (row) => removeRow(row),
            },
          ]),
    ]),
)

const modalTitle = computed(() =>
  isCreate.value
    ? t('admin.apps.createTitle', {}, 'Application erstellen')
    : (form.value.DisplayName || form.value.Slug),
)
const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.Slug)

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Erstellen') : t('common.save', {}, 'Speichern'),
  disabled: loading.value
    || !form.value.DisplayName.trim()
    || (isCreate.value && !form.value.Slug.trim())
    || hasInvalidSegments.value
    || hasIncompleteRows.value
    || duplicateKeys.value.size > 0,
  loading: loading.value,
  onClick: save,
}))

onMounted(async () => {
  if (isCreate.value) {
    // Start with one empty row so a new App has somewhere to type into.
    addRow()
    return
  }
  loading.value = true
  try {
    const loaded = await store.loadOne(id.value)
    if (!loaded) {
      error.value = t('admin.apps.loadFailed', {}, 'Application konnte nicht geladen werden.')
      return
    }
    dto.value = loaded
    const parsed = fromDto(loaded)
    form.value = parsed.form
    catalog.value = parsed.catalog
  } finally {
    loading.value = false
  }
})

async function save() {
  loading.value = true
  error.value = null
  catalogBlockers.value = []
  try {
    // An App is one resource: its ADR-0011 settings override is part of the same
    // create/update payload (the backend writes it in one tenant transaction). System
    // apps carry no per-App settings, so omit it for them.
    const settings = isSystem.value ? undefined : settingsRef.value?.build()
    if (isCreate.value) {
      await store.create({
        Slug: form.value.Slug.trim(),
        DisplayName: form.value.DisplayName.trim(),
        Description: form.value.Description.trim() || null,
        Permissions: buildPermissionsPayload(),
        Settings: settings,
      })
    } else {
      await store.update(id.value, {
        DisplayName: form.value.DisplayName.trim(),
        Description: form.value.Description.trim() || null,
        Permissions: buildPermissionsPayload(),
        Settings: settings,
      })
    }
    props.close()
  } catch (e: any) {
    // 409 catalog-block: surface the blocking references so the admin can
    // detach them before retrying.
    const body = e?.body
    if (body?.Error === 'App.CatalogEntriesReferenced' && Array.isArray(body.Blockers)) {
      catalogBlockers.value = body.Blockers as CatalogBlocker[]
      error.value = body.Message ?? t('admin.apps.catalogBlocked', {}, 'Some catalog entries are still in use.')
    } else {
      error.value = body?.Message ?? e?.message ?? String(e)
    }
  } finally {
    loading.value = false
  }
}

</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="layout-grid"
    :footer-button="footerButton" :readonly="isSystem" width="56rem">
    <div v-if="loading && !dto && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Laden...') }}</span>
    </div>
    <div v-else class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="general">{{ t('admin.apps.tabs.general', {}, 'Allgemein') }}</CoarTab>
        <CoarTab id="catalog">{{ t('admin.apps.tabs.catalog', {}, 'Permission-Catalog') }}</CoarTab>
        <CoarTab v-if="!isSystem" id="settings">{{ t('admin.apps.tabs.settings', {}, 'Einstellungen') }}</CoarTab>
      </CoarTabGroup>

      <CoarNote v-if="isCreate" variant="info">
        {{ t('admin.apps.createHint', {}, 'Eine neue App registriert sich für Permission-Resolution. Slug ist nach dem Erstellen unveränderbar.') }}
      </CoarNote>
      <CoarNote v-else-if="isSystem" variant="warning">
        {{ t('admin.apps.systemHint', {}, 'Dies ist eine System-App des IdP. Slug, Display Name und Permission-Catalog sind im Backend hartkodiert — der Catalog hier ist read-only und nur zur Inspektion. Änderungen an den Strings würden die RequiresPermission-Aufrufe im Backend brechen.') }}
      </CoarNote>

      <!-- Tab: General -->
      <div v-show="activeTab === 'general'" class="tab-content">
        <div class="grid grid-cols-2 gap-3">
          <CoarFormField :label="t('admin.apps.slug', {}, 'Slug (immutable)')">
            <CoarTextInput v-model="form.Slug" :disabled="!isCreate || isSystem" clearable
              :placeholder="t('admin.apps.slugPlaceholder', {}, 'kebab-case-slug')" />
          </CoarFormField>
          <CoarFormField :label="t('admin.apps.displayName', {}, 'Display Name')">
            <CoarTextInput v-model="form.DisplayName" :disabled="isSystem" clearable />
          </CoarFormField>
        </div>

        <CoarFormField :label="t('common.description', {}, 'Beschreibung')">
          <CoarTextInput v-model="form.Description" :disabled="isSystem" clearable />
        </CoarFormField>
      </div>

      <!-- Tab: Permission Catalog -->
      <div v-show="activeTab === 'catalog'" class="tab-content">
        <p class="catalog-subtitle">
          {{ isSystem
            ? t('admin.apps.permissionsHintSystem', {}, 'Permission-Catalog der System-App — read-only. Diese Einträge entsprechen 1:1 den RequiresPermission-Aufrufen im Backend-Code.')
            : t('admin.apps.permissionsHint', {}, 'Resource und Action je 1+ lowercase-Buchstaben/Ziffern/Bindestriche. Ids bleiben über Renames stabil — Role-Grants und RS-Subsets folgen automatisch.') }}
        </p>

        <CoarNote v-if="renamedCount > 0 && !isSystem" variant="warning">
          {{ t('admin.apps.renamedWarning', { count: renamedCount }, `${renamedCount} Eintrag/Einträge wurden umbenannt. Die String-Form ändert sich (z.B. in UserInfo), aber Role-Grants und RS-Subsets folgen automatisch über die stabile Id.`) }}
        </CoarNote>

        <div class="catalog-grid">
          <CoarDataGrid :builder="catalogBuilder" bordered>
            <template #toolbar-left>
              <CoarButton
                v-if="!isSystem"
                size="s"
                variant="ghost"
                icon-start="plus"
                @click="addRow"
              >
                {{ t('admin.apps.cat.add', {}, 'Eintrag hinzufügen') }}
              </CoarButton>
            </template>
          </CoarDataGrid>
        </div>
        <div v-if="!isSystem" class="catalog-hints">
          <span v-if="hasInvalidSegments" class="hint-error">
            {{ t('admin.apps.cat.invalidSegment', {}, 'Format: ^[a-z0-9-]+$ je Segment.') }}
          </span>
          <span v-else-if="hasIncompleteRows" class="hint-error">
            {{ t('admin.apps.cat.incomplete', {}, 'Resource und Action sind beide erforderlich.') }}
          </span>
          <span v-else-if="duplicateKeys.size > 0" class="hint-error">
            {{ t('admin.apps.cat.duplicate', {}, 'Doppelte Einträge: ') + [...duplicateKeys].join(', ') }}
          </span>
        </div>

        <CoarNote v-if="catalogBlockers.length > 0" variant="error">
          <div class="font-semibold mb-1">
            {{ t('admin.apps.cat.blockedTitle', {}, 'Diese Einträge sind noch in Verwendung:') }}
          </div>
          <ul class="blocker-list">
            <li v-for="b in catalogBlockers" :key="b.PermissionId">
              <code>{{ b.Permission }}</code>
              <span v-if="b.ReferencedByRoles.length > 0">
                · {{ t('admin.apps.cat.refRoles', {}, 'Rollen:') }} {{ b.ReferencedByRoles.join(', ') }}
              </span>
              <span v-if="b.ReferencedByResourceServers.length > 0">
                · {{ t('admin.apps.cat.refRSes', {}, 'Resource Server:') }} {{ b.ReferencedByResourceServers.join(', ') }}
              </span>
            </li>
          </ul>
        </CoarNote>
      </div>

      <!-- Tab: Settings (ADR-0011 per-App override) — one App, one modal -->
      <div v-if="!isSystem" v-show="activeTab === 'settings'" class="tab-content">
        <AppSettingsSections ref="settingsRef" :model-value="dto?.Settings" />
      </div>

      <p v-if="error" class="text-sm text-red-600">{{ error }}</p>
    </div>
  </ModalLayout>
</template>

<style scoped>
.tab-bar {
  margin-bottom: 12px;
}
.tab-content {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
}
.catalog-section {
  border: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  border-radius: var(--coar-radius-m, 4px);
  padding: 8px 10px 10px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.catalog-header {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.catalog-title {
  font-size: 0.82rem;
  font-weight: 600;
}

.catalog-subtitle {
  font-size: 0.74rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
}

/* Catalog-grid wrapper: ensure the data-grid gets a sensible height. AG
   Grid collapses to 0 height inside a flex column without an explicit
   floor; this keeps the empty state and the first few rows visible
   without forcing the whole modal to grow. */
.catalog-grid {
  min-height: 18rem;
  display: flex;
  flex-direction: column;
  flex: 1;
}

/* Per-cell validation styles applied by `cellClassFor()`. Targets the
   AG Grid cell element directly; the cellRenderer inside is unchanged
   so the editor opens identically. Light tint + colored left-edge
   matches the existing red/amber palette used in the form footers. */
:deep(.ag-cell.catalog-cell--invalid) {
  background: #fef2f2;
  box-shadow: inset 2px 0 0 #dc2626;
}

:deep(.ag-cell.catalog-cell--duplicate) {
  background: #fffbeb;
  box-shadow: inset 2px 0 0 #b45309;
}

.catalog-hints {
  display: flex;
  align-items: center;
  gap: 8px;
  padding-top: 4px;
}

.hint-error {
  font-size: 0.74rem;
  color: #dc2626;
}

.blocker-list {
  list-style: disc;
  padding-left: 1.25rem;
  margin: 0;
  font-size: 0.78rem;
}

.blocker-list li {
  margin-top: 2px;
}

.blocker-list code {
  font-weight: 600;
}
</style>
