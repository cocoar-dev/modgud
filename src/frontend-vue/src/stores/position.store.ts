import { defineStore } from 'pinia'
import { useEntityService } from '@/composables/useEntityService'
import type { PositionPrincipalDto, PositionCreateDto } from '@/models/position'

export const usePositionStore = defineStore('position', () => {
  const service = useEntityService<PositionPrincipalDto, PositionCreateDto>({
    apiPath: '/api/position',
    entityName: 'Position',
    enableSignalR: true,
    // The list needs an initial REST snapshot. SignalR only carries changes
    // after that point and reconnects trigger a separate drift correction.
    loadOnInit: true,
  })

  return { ...service }
})
