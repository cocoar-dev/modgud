import { defineStore } from 'pinia'
import { useEntityService } from '@/composables/useEntityService'
import type { ServiceAccountDto, ServiceAccountCreateDto } from '@/models/serviceAccount'

export const useServiceAccountStore = defineStore('serviceAccount', () => {
  const service = useEntityService<ServiceAccountDto, ServiceAccountCreateDto>({
    apiPath: '/api/service-account',
    entityName: 'ServiceAccount',
    enableSignalR: true,
    loadOnInit: false,
  })

  return { ...service }
})
