import { computed, type ComputedRef, type Ref } from 'vue'
import { useAuthStore } from '@/stores/auth.store'
import {
  SECTION_META,
  useRealmDraftStore,
  type DraftDto,
  type ManifestEntity,
  type PlanEntry,
} from '@/stores/realmDraft.store'

/**
 * The staging seam for ONE manifest section (ADR-0005 Increment B/C) — the
 * shared plumbing every admin Details modal and List view uses to participate
 * in the git model:
 *
 * - `stagingActive`: for realm admins the admin UI is always in staging mode.
 * - `isDraftId`/`draftKeyOf`: rows created in a draft carry `draft__<key>` ids
 *   (double underscore — a colon would break fragment routing).
 * - `stage`/`unstage`: the commit — entity upserts into the ACTIVE draft via
 *   the server-side seam, implicitly creating an auto-named draft.
 * - `findStaged`: the manifest entity behind a key (drafts start from the
 *   export, so existing entities are present with their staged state).
 */
/**
 * Draft-created rows carry `draft__<key>` ids that travel through the fragment router
 * (`roles/:id`), whose `:id` stops at a slash — and role keys are `app/name`. The key is
 * therefore percent-encoded inside the id; decoding tolerates an already-decoded id (the
 * router decodes route params) as well as a key that was never encoded.
 */
export function draftRowId(key: string): string {
  return `draft__${encodeURIComponent(key)}`
}

function decodeDraftKey(raw: string): string {
  try {
    return decodeURIComponent(raw)
  } catch {
    return raw
  }
}

export function useDraftStaging(section: string) {
  const draftStore = useRealmDraftStore()
  const authStore = useAuthStore()

  const stagingActive = computed(() => authStore.hasPermission('realm:admin'))

  function isDraftId(id: string): boolean {
    return id.startsWith('draft__')
  }

  function draftKeyOf(id: string): string {
    return decodeDraftKey(id.slice('draft__'.length))
  }

  function findStaged(key: string): ManifestEntity | null {
    return draftStore.findEntity(section, key)
  }

  async function stage(key: string, entity: ManifestEntity): Promise<void> {
    await draftStore.upsertEntity(section, key, entity)
  }

  async function unstage(key: string): Promise<void> {
    await draftStore.removeEntity(section, key)
  }

  /** Stages the deletion of a LIVE entity (targeted prune counterpart). */
  async function stageDelete(key: string): Promise<void> {
    await draftStore.stageDelete(section, key)
  }

  /** Undoes a staged deletion — the entity is restored from the baseline. */
  async function unstageDelete(key: string): Promise<void> {
    await draftStore.unstageDelete(section, key)
  }

  function isDeleteStaged(key: string): boolean {
    return draftStore.isDeleteStaged(section, key)
  }

  return {
    draftStore, stagingActive, isDraftId, draftKeyOf, findStaged,
    stage, unstage, stageDelete, unstageDelete, isDeleteStaged,
  }
}

export interface DraftOverlayOptions<TRow extends { Id: string }> {
  section: string
  rows: Ref<TRow[]> | ComputedRef<TRow[]>
  /** Live-row matcher for a staged (update) entity — usually the natural key. */
  matchLive: (row: TRow, entity: ManifestEntity) => boolean
  /** Merges the staged values over a live row. */
  overlay: (row: TRow, entity: ManifestEntity) => TRow
  /** Builds the synthetic row for an entity created in the draft. */
  synthesize: (key: string, entity: ManifestEntity, draft: DraftDto) => TRow
  /** The live row's natural key — enables marking rows whose DELETION is
   * staged (the entity is gone from the manifest, so only the key matches). */
  liveKey?: (row: TRow) => string
}

export type DraftStagedMark = 'create' | 'update' | 'delete'
export type DraftRow<TRow> = TRow & { DraftStaged?: DraftStagedMark }

/**
 * Draft-merged list rows: while a draft is checked out, the plan's user-visible
 * diff drives the roster — staged (update) entities overlay their live rows,
 * draft-created entities appear as synthetic `draft__<key>` rows. Without an
 * active draft the live rows pass through untouched.
 */
export function useDraftListOverlay<TRow extends { Id: string }>(
  options: DraftOverlayOptions<TRow>,
): ComputedRef<DraftRow<TRow>[]> {
  const draftStore = useRealmDraftStore()

  return computed<DraftRow<TRow>[]>(() => {
    const base = options.rows.value as DraftRow<TRow>[]
    const draft = draftStore.current
    const plan = draftStore.plan
    if (!draft || !plan) return base

    const entries: PlanEntry[] =
      plan.Sections.find((s) => s.Name === options.section)?.Entries ?? []
    const entities = draftStore.sectionEntities(options.section)
    const keyOf = SECTION_META[options.section]?.key ?? (() => '')
    const entityByKey = new Map(entities.map((e) => [keyOf(e), e]))

    const overlays = new Map<string, ManifestEntity>()
    const created: DraftRow<TRow>[] = []

    for (const entry of entries) {
      if (entry.Action !== 'create' && entry.Action !== 'update') continue
      const entity = entityByKey.get(entry.Key)
      if (!entity) continue
      if (entry.Action === 'update') {
        // Id first, exactly like the apply: it identifies the entity even when the
        // staged name differs from the live one (a staged RENAME). Falling back to
        // the natural key covers hand-written entries that carry no id.
        const live = base.find((row) =>
          (typeof entity.Id === 'string' && entity.Id === row.Id) ||
          options.matchLive(row, entity))
        if (live) overlays.set(live.Id, entity)
      } else {
        created.push({
          ...options.synthesize(entry.Key, entity, draft),
          Id: draftRowId(entry.Key),
          DraftStaged: 'create',
        })
      }
    }

    // Staged deletions come from the draft itself (not the plan): the entity is
    // gone from the manifest, so only the natural key can match the live row.
    const deletions = new Set(
      (draft.Deletions ?? [])
        .filter((d) => d.Section === options.section)
        .map((d) => d.Key))

    const rows = base.map((row) => {
      if (options.liveKey && deletions.has(options.liveKey(row)))
        return { ...row, DraftStaged: 'delete' as const }
      const entity = overlays.get(row.Id)
      if (!entity) return row
      return { ...options.overlay(row, entity), DraftStaged: 'update' as const }
    })
    return [...created, ...rows]
  })
}
