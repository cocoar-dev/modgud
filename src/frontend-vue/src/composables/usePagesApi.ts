// PageBuilder variant + activation API (ADR-0001). Thin fetch wrappers over the
// realm (`/api/admin/customization/pages`) and application
// (`/api/app/{appId}/pages`) endpoints. Realm and app share the variant CRUD
// shape; only the active-selection payload differs (the app can inherit).

export interface PageVariantSummary {
  Id: string
  Name: string
  CreatedAt: string
  UpdatedAt: string | null
}

export interface PageVariantFull {
  Id: string
  Name: string
  Schema: string
}

export interface RealmSlotDto {
  Slug: string
  ActiveVariantId: string | null
  Variants: PageVariantSummary[]
}

export interface AppSlotDto extends RealmSlotDto {
  InheritActive: boolean
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
    getSlot: async (slug: string) => ok<RealmSlotDto>(await fetch(`${base}/${enc(slug)}`, acceptJson)),
    getVariant: async (slug: string, id: string) =>
      ok<PageVariantFull>(await fetch(`${base}/${enc(slug)}/variants/${enc(id)}`, acceptJson)),
    createVariant: async (slug: string, body: VariantPayload) =>
      ok<{ Id: string; Name: string }>(await fetch(`${base}/${enc(slug)}/variants`, jsonInit('POST', body))),
    updateVariant: async (slug: string, id: string, body: VariantPayload) =>
      ok<{ Id: string; Name: string }>(await fetch(`${base}/${enc(slug)}/variants/${enc(id)}`, jsonInit('PUT', body))),
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
    getVariant: async (slug: string, id: string) =>
      ok<PageVariantFull>(await fetch(`${base}/${enc(slug)}/variants/${enc(id)}`, acceptJson)),
    createVariant: async (slug: string, body: VariantPayload) =>
      ok<{ Id: string; Name: string }>(await fetch(`${base}/${enc(slug)}/variants`, jsonInit('POST', body))),
    updateVariant: async (slug: string, id: string, body: VariantPayload) =>
      ok<{ Id: string; Name: string }>(await fetch(`${base}/${enc(slug)}/variants/${enc(id)}`, jsonInit('PUT', body))),
    deleteVariant: async (slug: string, id: string) =>
      okEmpty(await fetch(`${base}/${enc(slug)}/variants/${enc(id)}`, { method: 'DELETE', ...acceptJson })),
    setActive: async (slug: string, inherit: boolean, activeVariantId: string | null) =>
      okEmpty(await fetch(`${base}/${enc(slug)}/active`, jsonInit('PUT', { Inherit: inherit, ActiveVariantId: activeVariantId }))),
  }
}
