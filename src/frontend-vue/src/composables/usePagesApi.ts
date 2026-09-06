// PageBuilder variant + activation API (ADR-0013). The variant library is
// realm-global (`/api/admin/customization/pages`); Applications only *select*
// one of those realm variants per slot (`/api/app/{appId}/pages`).

export interface PageVariantSummary {
  Id: string
  Name: string
  CreatedAt: string
  UpdatedAt: string | null
  PublishedAt: string | null
  PublishedRevision: number
  IsPublished: boolean
  HasUnpublishedChanges: boolean
  RealmActive: boolean
  UsedByApps: string[]
}

export interface PageVariantFull {
  Id: string
  Name: string
  Schema: string
  PublishedRevision: number
  PublishedAt: string | null
  IsPublished: boolean
  HasUnpublishedChanges: boolean
  Revisions: PageVariantRevision[]
}

export interface PageVariantRevision {
  Number: number
  PublishedAt: string
  PublishedBy: string | null
  RollbackOfRevision: number | null
}

export interface RealmSlotDto {
  Slug: string
  ActiveVariantId: string | null
  Variants: PageVariantSummary[]
}

export interface VariantOption { Id: string; Name: string }

export interface AppSlotDto {
  Slug: string
  InheritActive: boolean
  ActiveVariantId: string | null
  AvailableVariants: VariantOption[]
}

export interface VariantPayload { Name: string; Schema: string }

async function ok<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.json().catch(() => null) as { Message?: string } | null
    throw new Error(body?.Message ?? `HTTP ${res.status}`)
  }
  return res.json() as Promise<T>
}

async function okEmpty(res: Response): Promise<void> {
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
}

const acceptJson: RequestInit = { headers: { Accept: 'application/json' } }

function jsonInit(method: string, body: unknown): RequestInit {
  return {
    method,
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify(body),
  }
}

const enc = encodeURIComponent

export function useRealmPagesApi() {
  const base = '/api/admin/customization/pages'
  return {
    listSlots: async () => ok<{ Slots: RealmSlotDto[] }>(await fetch(base, acceptJson)),
    getVariant: async (slug: string, id: string) =>
      ok<PageVariantFull>(await fetch(`${base}/${enc(slug)}/variants/${enc(id)}`, acceptJson)),
    createVariant: async (slug: string, body: VariantPayload) =>
      ok<{ Id: string; Name: string }>(await fetch(`${base}/${enc(slug)}/variants`, jsonInit('POST', body))),
    updateVariant: async (slug: string, id: string, body: VariantPayload) =>
      ok<{ Id: string; Name: string }>(await fetch(`${base}/${enc(slug)}/variants/${enc(id)}`, jsonInit('PUT', body))),
    publishVariant: async (slug: string, id: string) =>
      ok<{ Id: string; PublishedRevision: number; PublishedAt: string }>(
        await fetch(`${base}/${enc(slug)}/variants/${enc(id)}/publish`, { method: 'POST', ...acceptJson }),
      ),
    rollbackVariant: async (slug: string, id: string, revision: number) =>
      ok<{ Id: string; PublishedRevision: number; PublishedAt: string }>(
        await fetch(`${base}/${enc(slug)}/variants/${enc(id)}/rollback/${revision}`, { method: 'POST', ...acceptJson }),
      ),
    deleteVariant: async (slug: string, id: string) =>
      okEmpty(await fetch(`${base}/${enc(slug)}/variants/${enc(id)}`, { method: 'DELETE', ...acceptJson })),
    setActive: async (slug: string, activeVariantId: string | null) =>
      okEmpty(await fetch(`${base}/${enc(slug)}/active`, jsonInit('PUT', { ActiveVariantId: activeVariantId }))),
  }
}

export function useAppPagesApi(applicationId: string) {
  const base = `/api/app/${enc(applicationId)}/pages`
  return {
    listSlots: async () => ok<{ Slots: AppSlotDto[] }>(await fetch(base, acceptJson)),
    setActive: async (slug: string, inherit: boolean, activeVariantId: string | null) =>
      okEmpty(await fetch(`${base}/${enc(slug)}/active`, jsonInit('PUT', { Inherit: inherit, ActiveVariantId: activeVariantId }))),
  }
}
