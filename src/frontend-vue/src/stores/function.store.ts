import { defineStore } from 'pinia'
import { useEntityService } from '@/composables/useEntityService'
import type { FunctionPrincipalDto, FunctionCreateDto } from '@/models/function'

export const useFunctionStore = defineStore('function', () => {
  const service = useEntityService<FunctionPrincipalDto, FunctionCreateDto>({
    apiPath: '/api/function',
    entityName: 'Function',
    enableSignalR: true,
    loadOnInit: false,
  })

  return { ...service }
})
