import { defineStore } from 'pinia'
import { useEntityService } from '@/composables/useEntityService'
import type { ServiceAccountDto, ServiceAccountCreateDto } from '@/models/serviceAccount'

export const useServiceAccountStore = defineStore('serviceAccount', () => {
  const service = useEntityService<ServiceAccountDto, ServiceAccountCreateDto>({
    apiPath: '/api/service-account',
    entityName: 'ServiceAccount',
    enableSignalR: true,
    // The list needs an initial REST snapshot. SignalR only carries changes
    // after that point and reconnects trigger a separate drift correction.
    loadOnInit: true,
  })

  return { ...service }
})
