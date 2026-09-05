import { SECTION_META, refId, refKey, refList, type DraftManifest, type ManifestEntity } from '@/stores/realmDraft.store'

/**
 * Selective ("cart") export — pure selection/closure logic over an exported
 * realm manifest. Everything here is client-side: the full export is filtered
 * down to the checked entities plus the transitive references they need to
 * apply cleanly on a target realm (v2 merge-patch makes the partial manifest
 * safe — absent sections/fields stay untouched over there).
 *
 * Reference model (what an entity NEEDS on the target, per manifest key):
 *   client   → apps (Apps slugs), scopes (Scopes names)
 *   api      → app (App slug), scopes (Scopes names)
 *   scope    → app (App slug), apis (Resources audience names)
 *   role     → app (App slug — its Permissions resolve inside that catalog)
 *   group    → roles (Roles names), apps (BoundTo slugs, '*' excluded),
 *              users (Members keys — only when user references are included)
 *   position → users (Grants keys — only when user references are included)
 *   user / loginProvider / settings → nothing
 *
 * References that don't resolve INSIDE the export (e.g. the standard OIDC
 * scopes, which are never exported) are assumed to already exist on the
 * target and are skipped.
 */

/** Manifest sections that hold selectable entities, in display order. */
export const SELECTABLE_SECTIONS = [
  'apps', 'apis', 'scopes', 'clients', 'roles', 'groups', 'users', 'serviceAccounts', 'loginProviders', 'positions',
] as const

export type SelectableSection = (typeof SELECTABLE_SECTIONS)[number]

/** One entity address: `${section}/${naturalKey}`. */
export type SelectionKey = string

export function selectionKey(section: string, key: string): SelectionKey {
  return `${section}/${key}`
}

export interface EntityRef {
  section: SelectableSection
  /** The reference's readable key (may be stale after a rename — the id wins). */
  key: string
  /** The referenced entity's id when the reference carries one ({ Key, Id } form). */
  id?: string
}

export interface ClosureOptions {
  /** Include user references (group Members, position Grants) and pull the
   * referenced users into the closure. Off by default for cross-instance
   * transport — target realms rarely share the same user base. */
  includeUsers: boolean
}

const str = (v: unknown): string | null => (typeof v === 'string' && v.length > 0 ? v : null)
const strList = (v: unknown): string[] =>
  Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string' && x.length > 0) : []

function sectionEntities(manifest: DraftManifest, section: SelectableSection): ManifestEntity[] {
  const meta = SECTION_META[section]
  if (!meta?.collection) return []
  return (manifest[meta.collection] as ManifestEntity[] | undefined) ?? []
}

function findEntity(manifest: DraftManifest, section: SelectableSection, key: string, id?: string): ManifestEntity | null {
  const meta = SECTION_META[section]
  if (!meta) return null
  const entities = sectionEntities(manifest, section)
  // Id first, like the applier: a reference that carries one names the entity even
  // when its key has since changed.
  if (id) {
    const byId = entities.find((e) => e.Id === id)
    if (byId) return byId
  }
  return entities.find((e) => meta.key(e) === key) ?? null
}

/** Reference entries ({ Key, Id } or a bare key) → forward references. */
function refsOf(section: SelectableSection, value: unknown): EntityRef[] {
  return refList(value)
    .map((ref) => ({ section, key: refKey(ref) ?? refId(ref) ?? '', id: refId(ref) ?? undefined }))
    .filter((ref) => ref.key.length > 0)
}

/** The forward references one entity carries (unresolved — existence in the
 * export is checked by the closure, not here). */
export function referencesOf(section: SelectableSection, e: ManifestEntity, opts: ClosureOptions): EntityRef[] {
  const refs: EntityRef[] = []
  const app = (slug: string | null) => {
    if (slug && slug !== '*') refs.push({ section: 'apps', key: slug })
  }
  switch (section) {
    case 'clients':
      for (const slug of strList(e.Apps)) app(slug)
      for (const name of strList(e.Scopes)) refs.push({ section: 'scopes', key: name })
      break
    case 'apis':
      app(str(e.App))
      for (const name of strList(e.Scopes)) refs.push({ section: 'scopes', key: name })
      break
    case 'scopes':
      app(str(e.App))
      for (const name of strList(e.Resources)) refs.push({ section: 'apis', key: name })
      break
    case 'roles':
      app(str(e.App))
      break
    case 'groups':
      refs.push(...refsOf('roles', e.Roles))
      for (const slug of strList(e.BoundTo)) app(slug)
      if (opts.includeUsers) refs.push(...refsOf('users', e.Members))
      break
    case 'positions':
      if (opts.includeUsers) refs.push(...refsOf('users', e.Grants))
      break
  }
  return refs
}

/**
 * Transitive dependency closure of the selection. Returns the entities that
 * are REQUIRED but not themselves selected, mapped to the selection keys that
 * (directly or transitively) pulled them in. Cycles (scope ↔ api) are handled
 * by the visited set; references that don't exist in the export are skipped.
 */
export function computeClosure(
  manifest: DraftManifest,
  selected: ReadonlySet<SelectionKey>,
  opts: ClosureOptions,
): Map<SelectionKey, Set<SelectionKey>> {
  const required = new Map<SelectionKey, Set<SelectionKey>>()
  const queue: { ref: EntityRef; root: SelectionKey }[] = []

  for (const sel of selected) {
    const slash = sel.indexOf('/')
    const section = sel.slice(0, slash) as SelectableSection
    const key = sel.slice(slash + 1)
    const entity = findEntity(manifest, section, key)
    if (entity) for (const ref of referencesOf(section, entity, opts)) queue.push({ ref, root: sel })
  }

  const visited = new Set<SelectionKey>(selected)
  while (queue.length > 0) {
    const { ref, root } = queue.shift()!
    const entity = findEntity(manifest, ref.section, ref.key, ref.id)
    if (!entity) continue // not in the export (standard scope etc.) — assumed present on the target
    // Address the entity by ITS key (the reference's may be stale after a rename).
    const entityKey = selectionKey(ref.section, SECTION_META[ref.section]?.key(entity) ?? ref.key)
    if (!selected.has(entityKey)) {
      let by = required.get(entityKey)
      if (!by) required.set(entityKey, (by = new Set()))
      by.add(root)
    }
    if (visited.has(entityKey)) continue
    visited.add(entityKey)
    for (const next of referencesOf(ref.section, entity, opts)) queue.push({ ref: next, root })
  }
  return required
}

/** Reverse convenience for "everything belonging to this app": the clients,
 * APIs, scopes and roles that reference the app's slug. */
export function relatedToApp(manifest: DraftManifest, appSlug: string): EntityRef[] {
  const refs: EntityRef[] = []
  const push = (section: SelectableSection, e: ManifestEntity) =>
    refs.push({ section, key: SECTION_META[section]!.key(e) })
  for (const e of sectionEntities(manifest, 'clients'))
    if (strList(e.Apps).includes(appSlug)) push('clients', e)
  for (const e of sectionEntities(manifest, 'apis'))
    if (str(e.App) === appSlug) push('apis', e)
  for (const e of sectionEntities(manifest, 'scopes'))
    if (str(e.App) === appSlug) push('scopes', e)
  for (const e of sectionEntities(manifest, 'roles'))
    if (str(e.App) === appSlug) push('roles', e)
  return refs
}

export interface BuildOptions extends ClosureOptions {
  /** Realm slug written into the manifest — the apply guard on the target
   * rejects a foreign slug, so this is set to the TARGET realm. */
  targetSlug: string
  /** Include the realm-settings patch. Off by default. */
  includeSettings: boolean
}

/**
 * Builds the partial manifest for the given selection (checked + closure).
 * The Realm shell is reduced to the slug (everything else in it is ignored on
 * apply and would only produce warnings). When user references are excluded,
 * group `Members` and position `Grants` are stripped ENTIRELY — under v2
 * merge-patch an absent list means "unchanged" on the target (empty on
 * create), while `[]` would actively clear it.
 */
export function buildSelectiveManifest(
  manifest: DraftManifest,
  selectedWithClosure: ReadonlySet<SelectionKey>,
  opts: BuildOptions,
): DraftManifest {
  const out: DraftManifest = { Realm: { Slug: opts.targetSlug } }
  if (opts.includeSettings && manifest.Settings) {
    out.Settings = JSON.parse(JSON.stringify(manifest.Settings)) as ManifestEntity
  }
  for (const section of SELECTABLE_SECTIONS) {
    const meta = SECTION_META[section]
    if (!meta?.collection) continue
    const collection = meta.collection
    const picked = sectionEntities(manifest, section)
      .filter((e) => selectedWithClosure.has(selectionKey(section, meta.key(e))))
      .map((e) => JSON.parse(JSON.stringify(e)) as ManifestEntity)
    if (picked.length === 0) continue
    if (!opts.includeUsers) {
      if (section === 'groups') for (const g of picked) delete g.Members
      if (section === 'positions') for (const p of picked) delete p.Grants
    }
    out[collection] = picked
  }
  return out
}
