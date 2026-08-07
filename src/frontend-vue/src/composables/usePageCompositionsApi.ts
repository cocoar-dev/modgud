import type {
  PageCompositionDefinition,
  PageCompositionRepository,
  PageCompositionSummary,
} from '@cocoar/vue-page-builder'

interface CompositionSummaryDto {
  Id: string
  Name: string
  LatestVersion: string
  Versions: string[]
}

interface CompositionDefinitionDto {
  Id: string
  Name: string
  Version: string
  Root: PageCompositionDefinition['root']
}

const base = '/api/admin/customization/compositions'
const enc = encodeURIComponent

async function ok<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { Message?: string } | null
    throw new Error(body?.Message ?? `HTTP ${response.status}`)
  }
  return response.json() as Promise<T>
}

function definition(dto: CompositionDefinitionDto): PageCompositionDefinition {
  return { id: dto.Id, name: dto.Name, version: dto.Version, root: dto.Root }
}

export function usePageCompositionsApi() {
  const repository: PageCompositionRepository = {
    async list(): Promise<readonly PageCompositionSummary[]> {
      const result = await ok<CompositionSummaryDto[]>(await fetch(base, {
        headers: { Accept: 'application/json' },
      }))
      return result.map(item => ({
        id: item.Id,
        name: item.Name,
        latestVersion: item.LatestVersion,
        versions: item.Versions,
      }))
    },
    async get(id, version) {
      const query = version ? `?version=${enc(version)}` : ''
      const response = await fetch(`${base}/${enc(id)}${query}`, {
        headers: { Accept: 'application/json' },
      })
      if (response.status === 404) return null
      return definition(await ok<CompositionDefinitionDto>(response))
    },
    async create(input) {
      return definition(await ok<CompositionDefinitionDto>(await fetch(base, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
        body: JSON.stringify({ Name: input.name, Root: input.root }),
      })))
    },
    async publish(input) {
      return definition(await ok<CompositionDefinitionDto>(await fetch(
        `${base}/${enc(input.id)}/versions`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
          body: JSON.stringify({ BaseVersion: input.baseVersion, Root: input.root }),
        },
      )))
    },
  }

  return { repository }
}
