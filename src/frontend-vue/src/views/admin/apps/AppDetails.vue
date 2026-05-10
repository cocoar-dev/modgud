<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { CoarTextInput, CoarFormField, CoarNote, CoarTag, CoarButton, CoarIcon, CoarTabGroup, CoarTab } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
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

// Klick-Aktion state — feedback after the default resource-server is
// provisioned, including the one-time secret to copy.
const rsBusy = ref(false)
const rsResult = ref<{ apiId: string; name: string; secret: string | null; alreadyExisted: boolean } | null>(null)
const rsError = ref<string | null>(null)

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
 */
interface CatalogRow {
  id: string | null
  resource: string
  action: string
  description: string
  originalKey: string | null
}

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
    id: null, resource: '', action: '', description: '', originalKey: null,
  }]
}

function removeRow(index: number) {
  catalog.value = catalog.value.filter((_, i) => i !== index)
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
const activeTab = ref<'general' | 'catalog' | 'rs'>('general')

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
    if (isCreate.value) {
      await store.create({
        Slug: form.value.Slug.trim(),
        DisplayName: form.value.DisplayName.trim(),
        Description: form.value.Description.trim() || null,
        Permissions: buildPermissionsPayload(),
      })
    } else {
      await store.update(id.value, {
        DisplayName: form.value.DisplayName.trim(),
        Description: form.value.Description.trim() || null,
        Permissions: buildPermissionsPayload(),
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

async function provisionDefaultResourceServer() {
  rsBusy.value = true
  rsError.value = null
  try {
    const result = await store.createDefaultResourceServer(id.value)
    rsResult.value = {
      apiId: result.ApiId,
      name: result.Name,
      secret: result.ApiSecret,
      alreadyExisted: result.AlreadyExisted,
    }
  } catch (e: any) {
    rsError.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    rsBusy.value = false
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
      <CoarTabGroup v-if="!isCreate" v-model="activeTab" class="tab-bar">
        <CoarTab id="general">{{ t('admin.apps.tabs.general', {}, 'Allgemein') }}</CoarTab>
        <CoarTab id="catalog">{{ t('admin.apps.tabs.catalog', {}, 'Permission-Catalog') }}</CoarTab>
        <CoarTab v-if="!isSystem" id="rs">{{ t('admin.apps.tabs.rs', {}, 'Resource Server') }}</CoarTab>
      </CoarTabGroup>

      <CoarNote v-if="isCreate" variant="info">
        {{ t('admin.apps.createHint', {}, 'Eine neue App registriert sich für Permission-Resolution. Slug ist nach dem Erstellen unveränderbar.') }}
      </CoarNote>
      <CoarNote v-else-if="isSystem" variant="warning">
        {{ t('admin.apps.systemHint', {}, 'Dies ist eine System-App des IdP. Slug, Display Name und Permission-Catalog sind im Backend hartkodiert — der Catalog hier ist read-only und nur zur Inspektion. Änderungen an den Strings würden die RequiresPermission-Aufrufe im Backend brechen.') }}
      </CoarNote>

      <!-- Tab: General -->
      <div v-show="isCreate || activeTab === 'general'" class="tab-content">
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
      <div v-show="isCreate || activeTab === 'catalog'" class="tab-content">
        <p class="catalog-subtitle">
          {{ isSystem
            ? t('admin.apps.permissionsHintSystem', {}, 'Permission-Catalog der System-App — read-only. Diese Einträge entsprechen 1:1 den RequiresPermission-Aufrufen im Backend-Code.')
            : t('admin.apps.permissionsHint', {}, 'Resource und Action je 1+ lowercase-Buchstaben/Ziffern/Bindestriche. Ids bleiben über Renames stabil — Role-Grants und RS-Subsets folgen automatisch.') }}
        </p>

        <CoarNote v-if="renamedCount > 0 && !isSystem" variant="warning">
          {{ t('admin.apps.renamedWarning', { count: renamedCount }, `${renamedCount} Eintrag/Einträge wurden umbenannt. Die String-Form ändert sich (z.B. in UserInfo), aber Role-Grants und RS-Subsets folgen automatisch über die stabile Id.`) }}
        </CoarNote>

        <table class="catalog-table">
          <thead>
            <tr>
              <th class="col-resource">{{ t('admin.apps.cat.resource', {}, 'Resource') }}</th>
              <th class="col-action">{{ t('admin.apps.cat.action', {}, 'Action') }}</th>
              <th class="col-description">{{ t('admin.apps.cat.description', {}, 'Beschreibung') }}</th>
              <th v-if="!isSystem" class="col-actions"></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(row, index) in catalog" :key="index">
              <td>
                <input v-model="row.resource" :disabled="isSystem"
                  :class="{ 'invalid': row.resource && !isSegmentValid(row.resource), 'duplicate': duplicateKeys.has(`${row.resource.trim()}:${row.action.trim()}`) }"
                  class="catalog-input" placeholder="user" />
              </td>
              <td>
                <input v-model="row.action" :disabled="isSystem"
                  :class="{ 'invalid': row.action && !isSegmentValid(row.action), 'duplicate': duplicateKeys.has(`${row.resource.trim()}:${row.action.trim()}`) }"
                  class="catalog-input" placeholder="read" />
              </td>
              <td>
                <input v-model="row.description" :disabled="isSystem" class="catalog-input"
                  :placeholder="t('admin.apps.cat.descriptionPlaceholder', {}, 'optional')" />
              </td>
              <td v-if="!isSystem" class="col-actions-cell">
                <CoarTag v-if="rowRenamed(row)" size="s" variant="warning"
                  :title="t('admin.apps.cat.renamedTitle', {}, 'Umbenannt — Id bleibt stabil')">
                  ✎
                </CoarTag>
                <button type="button" class="row-delete"
                  :title="t('admin.apps.cat.removeTitle', {}, 'Eintrag entfernen')"
                  @click="removeRow(index)">
                  <CoarIcon name="trash-2" size="s" />
                </button>
              </td>
            </tr>
          </tbody>
        </table>
        <div v-if="!isSystem" class="catalog-footer">
          <CoarButton size="s" variant="secondary" icon-start="plus" @click="addRow">
            {{ t('admin.apps.cat.add', {}, 'Eintrag hinzufügen') }}
          </CoarButton>
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

      <!-- Tab: Resource Server (provision default) — only for user apps. -->
      <div v-if="!isCreate && dto && !isSystem" v-show="activeTab === 'rs'" class="tab-content rs-panel">
        <div class="rs-panel-header">
          {{ t('admin.apps.rs.title', {}, 'Resource Server') }}
        </div>

        <div v-if="!rsResult" class="text-xs text-gray-500">
          {{ t('admin.apps.rs.help', {}, 'A resource server identity lets your backend authenticate against /api/v1/distribution/* on behalf of users. The default one matches this app\'s slug; you can add more later in the OAuth APIs admin.') }}
        </div>

        <CoarNote v-if="rsResult?.alreadyExisted" variant="info">
          {{ t('admin.apps.rs.alreadyExists', { name: rsResult.name }, `Default resource server "${rsResult.name}" already exists. Manage its secrets in the OAuth APIs admin.`) }}
        </CoarNote>

        <CoarNote v-else-if="rsResult?.secret" variant="warning">
          <div class="font-semibold mb-1">
            {{ t('admin.apps.rs.created', { name: rsResult.name }, `Default resource server "${rsResult.name}" created.`) }}
          </div>
          <div class="text-xs mb-1">
            {{ t('admin.apps.rs.secretWarning', {}, 'Copy this API secret now — it will never be shown again.') }}
          </div>
          <code class="rs-secret">{{ rsResult.secret }}</code>
        </CoarNote>

        <p v-if="rsError" class="text-sm text-red-600">{{ rsError }}</p>

        <div v-if="!rsResult || rsResult.alreadyExisted" class="mt-2">
          <CoarButton
            size="s"
            icon-start="server"
            :loading="rsBusy"
            :disabled="rsBusy || (rsResult?.alreadyExisted ?? false)"
            @click="provisionDefaultResourceServer">
            {{ t('admin.apps.rs.create', {}, 'Create default resource server') }}
          </CoarButton>
        </div>
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
.rs-panel {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.rs-panel-header {
  font-size: 0.8rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: #525e76;
}

.rs-secret {
  display: block;
  padding: 6px 8px;
  background: var(--coar-background-neutral-tertiary, #f3f4f6);
  border-radius: var(--coar-radius-s, 3px);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.78rem;
  word-break: break-all;
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

.catalog-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.82rem;
}

.catalog-table th {
  text-align: left;
  font-weight: 500;
  font-size: 0.74rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--coar-text-neutral-secondary, #6b7280);
  padding: 4px 6px;
  border-bottom: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
}

.catalog-table td {
  padding: 3px 4px;
  vertical-align: middle;
}

.col-resource { width: 26%; }
.col-action { width: 22%; }
.col-description {  }
.col-actions { width: 4.5rem; }

.col-actions-cell {
  text-align: right;
  white-space: nowrap;
}

.catalog-input {
  width: 100%;
  padding: 4px 6px;
  border: 1px solid var(--coar-border-neutral-secondary, #d1d5db);
  border-radius: var(--coar-radius-s, 3px);
  background: var(--coar-background-neutral-primary, #fff);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.78rem;
}

.catalog-input.invalid {
  border-color: #dc2626;
  background: #fef2f2;
}

.catalog-input.duplicate {
  border-color: #b45309;
  background: #fffbeb;
}

.row-delete {
  background: none;
  border: 0;
  cursor: pointer;
  padding: 4px;
  color: var(--coar-text-neutral-secondary, #6b7280);
  border-radius: 3px;
}

.row-delete:hover {
  color: #dc2626;
  background: #fef2f2;
}

.catalog-footer {
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
