import { defineStore } from 'pinia'
import { useEntityService } from '@/composables/useEntityService'
import type { PositionPrincipalDto, PositionCreateDto } from '@/models/position'

export const usePositionStore = defineStore('position', () => {
  const service = useEntityService<PositionPrincipalDto, PositionCreateDto>({
    apiPath: '/api/position',
    entityName: 'Position',
    enableSignalR: true,
    loadOnInit: false,
  })

  return { ...service }
})
