// F7-04 — reading a staged permission catalog back into the editor. A staged entry carries
// its catalog Id (ADR 0024); a staged RENAME has a new resource:action but the old Id. Looking
// the live id up by the (already renamed) resource:action alone lost the Id on reopen, and the
// next save turned the rename into remove + add.

export interface LivePermission { Id: string; Resource: string; Action: string }
export interface StagedCatalogRow {
  id: string | null
  resource: string
  action: string
  description: string
  /** The live resource:action the row started from (null for a new entry). */
  originalKey: string | null
}

const str = (v: unknown) => (typeof v === 'string' ? v : '')

export function readStagedCatalog(staged: unknown, live: LivePermission[]): StagedCatalogRow[] {
  const keyById = new Map(live.map((p) => [p.Id, `${p.Resource}:${p.Action}`]))
  const idByKey = new Map(live.map((p) => [`${p.Resource}:${p.Action}`, p.Id]))
  const entries = Array.isArray(staged) ? (staged as Record<string, unknown>[]) : []
  return entries.map((p) => {
    const key = `${str(p.Resource)}:${str(p.Action)}`
    const stagedId = str(p.Id) || null
    // The staged Id wins when it names a live entry (rename); otherwise match by key.
    const id = stagedId && keyById.has(stagedId) ? stagedId : (idByKey.get(key) ?? null)
    return {
      id,
      resource: str(p.Resource),
      action: str(p.Action),
      description: str(p.Description),
      originalKey: id ? (keyById.get(id) ?? key) : null,
    }
  })
}
