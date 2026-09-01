import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'

/** Deep-clones a manifest for mutation. Deliberately the JSON clone —
 * structuredClone throws on Vue's reactive proxies, and the manifest is plain
 * JSON by construction, so the JSON round-trip is exact. */
function cloneManifest(manifest: DraftManifest): DraftManifest {
  return JSON.parse(JSON.stringify(manifest)) as DraftManifest
}

/**
 * ADR-0005 Phase 1: named server-side configuration drafts. The store owns the
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

export interface DraftDto extends DraftSummary {
  Manifest: DraftManifest
  SecretSlots: string[]
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

/** Manifest collection + natural key per plan section (mirrors the backend). */
export const SECTION_META: Record<string, { collection: keyof DraftManifest | null; key: (e: ManifestEntity) => string }> = {
  settings: { collection: null, key: () => 'settings' },
  apps: { collection: 'Apps', key: (e) => String(e.Slug ?? '') },
  apis: { collection: 'Apis', key: (e) => String(e.Name ?? '') },
  scopes: { collection: 'Scopes', key: (e) => String(e.Name ?? '') },
  clients: { collection: 'Clients', key: (e) => String(e.ClientId ?? '') },
  loginProviders: { collection: 'LoginProviders', key: (e) => String(e.Slug ?? '') },
  roles: { collection: 'Roles', key: (e) => String(e.Key ?? e.Name ?? '') },
  users: { collection: 'Users', key: (e) => String(e.Key ?? e.UserName ?? e.Email ?? '') },
  groups: { collection: 'Groups', key: (e) => String(e.Name ?? '') },
  positions: { collection: 'Positions', key: (e) => String(e.AccountName ?? '').trim().toLowerCase() },
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

  async function openDraft(id: string) {
    error.value = null
    applyOutcome.value = null
    current.value = await draftsHttp.addPath(id).get<DraftDto>()
    plan.value = null
    plannedVersion.value = null
    await replan()
  }

  function closeDraft() {
    current.value = null
    plan.value = null
    plannedVersion.value = null
    applyOutcome.value = null
    error.value = null
  }

  async function deleteDraft(id: string) {
    error.value = null
    await draftsHttp.addPath(id).delete<void>()
    if (current.value?.Id === id) closeDraft()
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

  /** Replaces (or inserts) one entity in its manifest collection and saves. */
  async function upsertEntity(section: string, key: string, entity: ManifestEntity) {
    if (!current.value) return
    const meta = SECTION_META[section]
    if (!meta) return
    const manifest = cloneManifest(current.value.Manifest)
    if (meta.collection === null) {
      manifest.Settings = entity
    } else {
      const list = (manifest[meta.collection] as ManifestEntity[] | undefined) ?? []
      const index = list.findIndex((e) => meta.key(e) === key)
      if (index >= 0) list[index] = entity
      else list.push(entity)
      manifest[meta.collection] = list
    }
    await updateDraft({ Manifest: manifest })
  }

  /** Removes one entity from its manifest collection and saves. */
  async function removeEntity(section: string, key: string) {
    if (!current.value) return
    const meta = SECTION_META[section]
    if (!meta || meta.collection === null) return
    const manifest = cloneManifest(current.value.Manifest)
    const list = (manifest[meta.collection] as ManifestEntity[] | undefined) ?? []
    manifest[meta.collection] = list.filter((e) => meta.key(e) !== key)
    await updateDraft({ Manifest: manifest })
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
    planIsFresh, planHasErrors, canApply,
    loadDrafts, createDraft, openDraft, closeDraft, deleteDraft,
    replan, updateDraft, upsertEntity, removeEntity, findEntity,
    rebase, clearSecret, apply,
  }
})
