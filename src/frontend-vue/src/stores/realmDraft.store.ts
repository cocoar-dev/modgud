import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

/**
 * ADR-0017 Phase 1: named server-side configuration drafts. The store owns the
 * open draft + its plan and funnels EVERY draft mutation through the same path:
 * PUT (optimistic version) → fresh draft DTO → automatic re-plan. Apply is gated
 * server-side (fail-fast plan, 409 with the plan while errors/conflicts exist);
 * the store mirrors that gate for the UI.
 *
 * The manifest is intentionally `Record<string, unknown>`-shaped: the workspace
 * edits manifest JSON generically (cards/modals per section), and the backend
 * schema is the source of truth.
 */

export type ManifestEntity = Record<string, unknown>

export interface DraftManifest extends ManifestEntity {
  Realm?: ManifestEntity
  Settings?: ManifestEntity | null
  Apps?: ManifestEntity[]
  Apis?: ManifestEntity[]
  Scopes?: ManifestEntity[]
  Clients?: ManifestEntity[]
  Roles?: ManifestEntity[]
  Users?: ManifestEntity[]
  ServiceAccounts?: ManifestEntity[]
  Groups?: ManifestEntity[]
  LoginProviders?: ManifestEntity[]
  Positions?: ManifestEntity[]
}

export interface DraftSummary {
  Id: string
  Name: string
  Shared: boolean
  Mine: boolean
  CreatedByName: string
  CreatedAt: string
  LastModifiedByName: string
  LastModifiedAt: string
  Version: number
}

export interface DraftDeletion {
  Section: string
  Key: string
}

export interface DraftDto extends DraftSummary {
  Manifest: DraftManifest
  SecretSlots: string[]
  Deletions: DraftDeletion[]
}

export interface PlanChange {
  Field: string
  Current: unknown
  Desired: unknown
}

export type PlanAction = 'create' | 'update' | 'unchanged' | 'delete' | 'protected' | 'error'
export type ConflictKind = 'staleOverwrite' | 'bothChanged' | 'deletedLive' | 'createdLive'

export interface PlanConflict {
  Kind: ConflictKind
  Field: string | null
  Baseline: unknown
  Live: unknown
  Draft: unknown
}

export interface PlanEntry {
  Key: string
  Action: PlanAction
  Changes: PlanChange[]
  Notes: string[]
  Conflicts: PlanConflict[]
}

export interface PlanSection {
  Name: string
  Entries: PlanEntry[]
}

export interface PlanResult {
  Slug: string
  Prune: boolean
  Sections: PlanSection[]
  Warnings: string[]
  HasConflicts: boolean
}

export interface ApplyOutcome {
  Slug: string
  PrimaryDomain: string
  ClientSecrets: Record<string, string>
}

/**
 * A role's manifest key (mirrors `RoleKeys` in the backend): `<app slug>/<name>` for an
 * App role, the bare name for a realm-admin role. Role names are unique per App only —
 * two apps may each have an "Author" — so the name alone never identifies a role.
 */
export function roleManifestKey(appSlug: string | null | undefined, name: string): string {
  return appSlug ? `${appSlug}/${name}` : name
}

/**
 * A manifest cross-reference (group → role / member, position → grant), mirroring the
 * backend's `ManifestRef`: a bare string is ALWAYS a key, never an id; the object form
 * carries the entity `Id` (which wins — rename-proof) plus the readable `Key`.
 */
export type ManifestRef = string | { Key?: string; Id?: string }

export function refKey(ref: ManifestRef): string | null {
  if (typeof ref === 'string') return ref || null
  return typeof ref.Key === 'string' && ref.Key ? ref.Key : null
}

export function refId(ref: ManifestRef): string | null {
  return typeof ref === 'object' && typeof ref.Id === 'string' && ref.Id ? ref.Id : null
}

export function refList(value: unknown): ManifestRef[] {
  if (!Array.isArray(value)) return []
  return value.filter((x): x is ManifestRef =>
    (typeof x === 'string' && x.length > 0) || (!!x && typeof x === 'object'))
}

/** The form the export writes: both halves, so the apply follows the id and a reader
 * still sees the key. */
export function makeRef(key: string, id: string): ManifestRef {
  return { Key: key, Id: id }
}

/** Manifest collection + natural key per plan section (mirrors the backend). */
export const SECTION_META: Record<string, { collection: keyof DraftManifest | null; key: (e: ManifestEntity) => string }> = {
  settings: { collection: null, key: () => 'settings' },
  apps: { collection: 'Apps', key: (e) => String(e.Slug ?? '') },
  apis: { collection: 'Apis', key: (e) => String(e.Name ?? '') },
  scopes: { collection: 'Scopes', key: (e) => String(e.Name ?? '') },
  clients: { collection: 'Clients', key: (e) => String(e.ClientId ?? '') },
  loginProviders: { collection: 'LoginProviders', key: (e) => String(e.Slug ?? '') },
  roles: {
    collection: 'Roles',
    key: (e) => String(e.Key ?? roleManifestKey(typeof e.App === 'string' ? e.App : null, String(e.Name ?? ''))),
  },
  users: { collection: 'Users', key: (e) => String(e.Key ?? e.UserName ?? e.Email ?? '') },
  groups: { collection: 'Groups', key: (e) => String(e.Name ?? '') },
  serviceAccounts: { collection: 'ServiceAccounts', key: (e) => String(e.AccountName ?? '').trim().toLowerCase() },
  positions: { collection: 'Positions', key: (e) => String(e.AccountName ?? '').trim().toLowerCase() },
}

/**
 * Best-effort refresh of every admin entity store after a draft apply. The
 * apply writes through the canonical services (transactional), bypassing the
 * endpoint-layer SignalR dispatches — without this the grids keep showing
 * pre-apply data until a manual reload. Stores that were never loaded are
 * refreshed too (cheap GETs) so a navigation right after the apply is fresh.
 */
async function resyncEntityStores(): Promise<void> {
  const loaders: Promise<unknown>[] = [
    import('./applications.store').then((m) => m.useApplicationsStore().loadAll()),
    import('./oauthClient.store').then((m) => m.useOAuthClientStore().loadAll()),
    import('./oauthScope.store').then((m) => m.useOAuthScopeStore().loadAll()),
    import('./oauthApi.store').then((m) => m.useOAuthApiStore().loadAll()),
    import('./role.store').then((m) => m.useRoleStore().loadAll()),
    import('./group.store').then((m) => m.useGroupStore().loadAll()),
    import('./user.store').then((m) => m.useUserStore().loadAll()),
    import('./loginProvider.store').then((m) => m.useLoginProviderStore().loadAll()),
    import('./position.store').then((m) => m.usePositionStore().loadAll()),
  ]
  await Promise.allSettled(loaders)
}

export function draftErrorMessage(err: unknown): string {
  if (err instanceof HttpClientError) {
    const body = err.body as { Error?: string; Message?: string } | null
    if (body?.Message) return body.Error ? `${body.Error}: ${body.Message}` : body.Message
    return `HTTP ${err.status}`
  }
  return err instanceof Error ? err.message : String(err)
}

export const useRealmDraftStore = defineStore('realmDraft', () => {
  const draftsHttp = useHttpClient('/api/admin/realm-config/drafts')

  const drafts = ref<DraftSummary[]>([])
  const current = ref<DraftDto | null>(null)
  const plan = ref<PlanResult | null>(null)
  /** Draft version the current plan was computed for — stale plans block apply. */
  const plannedVersion = ref<number | null>(null)
  const prune = ref(false)
  const listLoading = ref(false)
  const planning = ref(false)
  const saving = ref(false)
  const applying = ref(false)
  const error = ref<string | null>(null)
  const applyOutcome = ref<ApplyOutcome | null>(null)

  const planIsFresh = computed(() =>
    plan.value !== null && current.value !== null &&
    plannedVersion.value === current.value.Version && plan.value.Prune === prune.value)

  const planHasErrors = computed(() =>
    plan.value?.Sections.some((s) => s.Entries.some((e) => e.Action === 'error')) ?? false)

  const canApply = computed(() =>
    planIsFresh.value && !planHasErrors.value && plan.value !== null && !plan.value.HasConflicts)

  /** Entries the apply would actually touch — the staging bar's badge. */
  const pendingCount = computed(() => {
    let count = 0
    for (const section of plan.value?.Sections ?? [])
      for (const entry of section.Entries)
        if (entry.Action === 'create' || entry.Action === 'update' || entry.Action === 'delete') count++
    return count
  })

  async function loadDrafts() {
    listLoading.value = true
    error.value = null
    try {
      drafts.value = await draftsHttp.get<DraftSummary[]>()
    } catch (e) {
      error.value = draftErrorMessage(e)
    } finally {
      listLoading.value = false
    }
  }

  async function createDraft(name: string, source: 'export' | 'empty' | 'manifest', manifest?: unknown) {
    error.value = null
    const dto = await draftsHttp.post<DraftDto>({ Name: name, Source: source, Manifest: manifest ?? null })
    await loadDrafts()
    await openDraft(dto.Id)
    return dto
  }

  /** Loads the admin's active draft (the checkout) — called once when the admin
   * shell mounts, so the staging bar reflects reality after a reload. */
  async function loadActive() {
    try {
      const dto = await draftsHttp.addPath('active').get<DraftDto | null>()
      if (dto) {
        current.value = dto
        await replan()
      }
    } catch (e) {
      error.value = draftErrorMessage(e)
    }
  }

  /** Checkout: switching sets the server-side active pointer. */
  async function openDraft(id: string) {
    error.value = null
    applyOutcome.value = null
    current.value = await draftsHttp.addPath('active', 'switch', id).post<DraftDto>({})
    plan.value = null
    plannedVersion.value = null
    await replan()
  }

  /** Parking: clears the checkout, keeps the branch. */
  async function closeDraft() {
    try {
      await draftsHttp.addPath('active', 'park').post<void>({})
    } catch (e) {
      error.value = draftErrorMessage(e)
    }
    current.value = null
    plan.value = null
    plannedVersion.value = null
    applyOutcome.value = null
    await loadDrafts()
  }

  async function deleteDraft(id: string) {
    error.value = null
    await draftsHttp.addPath(id).delete<void>()
    // Deleting the active draft already cleared the server-side pointer.
    if (current.value?.Id === id) {
      current.value = null
      plan.value = null
      plannedVersion.value = null
    }
    await loadDrafts()
  }

  async function replan() {
    if (!current.value) return
    planning.value = true
    error.value = null
    const forVersion = current.value.Version
    try {
      const result = await draftsHttp
        .addPath(current.value.Id, 'plan')
        .setQueryParameter('prune', String(prune.value))
        .post<PlanResult>({})
      plan.value = result
      plannedVersion.value = forVersion
    } catch (e) {
      plan.value = null
      plannedVersion.value = null
      error.value = draftErrorMessage(e)
    } finally {
      planning.value = false
    }
  }

  /** Single mutation funnel: PUT with the current version, adopt the returned
   * draft, re-plan. A 409 (someone else edited) reloads the draft instead. */
  async function updateDraft(patch: { Name?: string; Shared?: boolean; Manifest?: DraftManifest }) {
    if (!current.value) return
    saving.value = true
    error.value = null
    try {
      current.value = await draftsHttp.addPath(current.value.Id).put<DraftDto>({
        ExpectedVersion: current.value.Version,
        ...patch,
      })
      await replan()
    } catch (e) {
      error.value = draftErrorMessage(e)
      if (e instanceof HttpClientError && e.status === 409)
        await openDraft(current.value.Id)
    } finally {
      saving.value = false
    }
  }

  /** The "commit": stages one entity into the ACTIVE draft via the server-side
   * seam — implicitly creating an auto-named draft when none is active. The
   * natural key is computed server-side; edits with a renamed key stage the
   * renamed entity alongside the old one (rename = create + prune delete). */
  async function upsertEntity(section: string, _key: string, entity: ManifestEntity) {
    saving.value = true
    error.value = null
    try {
      current.value = await draftsHttp
        .addPath('active', 'entities', section)
        .put<DraftDto>(entity)
      await replan()
    } catch (e) {
      error.value = draftErrorMessage(e)
    } finally {
      saving.value = false
    }
  }

  /** Removes one entity from the active draft (undo of a staged create/edit). */
  async function removeEntity(section: string, key: string) {
    saving.value = true
    error.value = null
    try {
      current.value = await draftsHttp
        .addPath('active', 'entities', section)
        .setQueryParameter('key', key)
        .delete<DraftDto>()
      await replan()
    } catch (e) {
      error.value = draftErrorMessage(e)
    } finally {
      saving.value = false
    }
  }

  /** Stages the DELETION of one live entity (ADR-0017 staged deletes) — the
   * targeted counterpart of prune; implicitly creates a draft when none is
   * active. Applied through the same canonical delete ops on "Draft anwenden". */
  async function stageDelete(section: string, key: string) {
    saving.value = true
    error.value = null
    try {
      current.value = await draftsHttp
        .addPath('active', 'deletions', section)
        .setQueryParameter('key', key)
        .put<DraftDto>({})
      await replan()
    } catch (e) {
      error.value = draftErrorMessage(e)
    } finally {
      saving.value = false
    }
  }

  /** Undoes a staged deletion — the entity is restored from the draft's baseline. */
  async function unstageDelete(section: string, key: string) {
    saving.value = true
    error.value = null
    try {
      current.value = await draftsHttp
        .addPath('active', 'deletions', section)
        .setQueryParameter('key', key)
        .delete<DraftDto>()
      await replan()
    } catch (e) {
      error.value = draftErrorMessage(e)
    } finally {
      saving.value = false
    }
  }

  /** Whether the active draft stages the deletion of (section, key). */
  function isDeleteStaged(section: string, key: string): boolean {
    return current.value?.Deletions?.some((d) => d.Section === section && d.Key === key) === true
  }

  /** All manifest entities of a section in the ACTIVE draft (settings = [Settings]). */
  function sectionEntities(section: string): ManifestEntity[] {
    if (!current.value) return []
    const meta = SECTION_META[section]
    if (!meta) return []
    if (meta.collection === null) {
      const settings = current.value.Manifest.Settings
      return settings ? [settings as ManifestEntity] : []
    }
    return (current.value.Manifest[meta.collection] as ManifestEntity[] | undefined) ?? []
  }

  function findEntity(section: string, key: string): ManifestEntity | null {
    if (!current.value) return null
    const meta = SECTION_META[section]
    if (!meta) return null
    if (meta.collection === null) return (current.value.Manifest.Settings as ManifestEntity) ?? null
    const list = (current.value.Manifest[meta.collection] as ManifestEntity[] | undefined) ?? []
    return list.find((e) => meta.key(e) === key) ?? null
  }

  async function rebase() {
    if (!current.value) return
    saving.value = true
    error.value = null
    try {
      current.value = await draftsHttp.addPath(current.value.Id, 'rebase').post<DraftDto>({})
      await replan()
    } catch (e) {
      error.value = draftErrorMessage(e)
    } finally {
      saving.value = false
    }
  }

  async function clearSecret(slot: string) {
    if (!current.value) return
    error.value = null
    current.value = await draftsHttp
      .addPath(current.value.Id, 'secret')
      .setQueryParameter('slot', slot)
      .delete<DraftDto>()
    await replan()
  }

  async function apply(): Promise<boolean> {
    if (!current.value) return false
    applying.value = true
    error.value = null
    try {
      const result = await draftsHttp
        .addPath(current.value.Id, 'apply')
        .setQueryParameter('prune', String(prune.value))
        .post<ApplyOutcome>({})
      applyOutcome.value = result
      current.value = null
      plan.value = null
      plannedVersion.value = null
      await loadDrafts()
      // The apply runs through the canonical SERVICES, below the endpoint
      // layer that dispatches the per-entity SignalR events — so no grid
      // would hear about the changes. Re-sync every entity store that has
      // been loaded instead (dynamic imports keep module graphs acyclic).
      void resyncEntityStores()
      return true
    } catch (e) {
      // The server gate answers 409 with the offending plan — show it.
      if (e instanceof HttpClientError && e.status === 409) {
        const body = e.body as { Plan?: PlanResult } | null
        if (body?.Plan && current.value) {
          plan.value = body.Plan
          plannedVersion.value = current.value.Version
        }
      }
      error.value = draftErrorMessage(e)
      return false
    } finally {
      applying.value = false
    }
  }

  return {
    drafts, current, plan, prune,
    listLoading, planning, saving, applying, error, applyOutcome,
    planIsFresh, planHasErrors, canApply, pendingCount,
    loadDrafts, loadActive, createDraft, openDraft, closeDraft, deleteDraft,
    replan, updateDraft, upsertEntity, removeEntity, findEntity, sectionEntities,
    stageDelete, unstageDelete, isDeleteStaged,
    rebase, clearSecret, apply,
  }
})
