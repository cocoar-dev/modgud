import { defineStore } from 'pinia'
import { computed } from 'vue'
import { useEntityService } from '@/composables/useEntityService'
import type { CustomerDto, CustomerCreateDto } from '@/models/customer'

export const useCustomerStore = defineStore('customer', () => {
  const service = useEntityService<CustomerDto, CustomerCreateDto>({
    apiPath: '/api/customer',
    entityName: 'Customer',
    enableSignalR: true,
    loadOnInit: true,
    handleFullSync: true,
  })

  const sortByName = (a: CustomerDto, b: CustomerDto) =>
    (a.Name ?? '').localeCompare(b.Name ?? '')

  const allCustomersSorted = computed(() =>
    [...service.entities.value].sort(sortByName)
  )

  const activeCustomers = computed(() =>
    allCustomersSorted.value.filter(c => !c.IsArchived)
  )

  const importantCustomers = computed(() =>
    activeCustomers.value.filter(c => c.Important)
  )

  const normalCustomers = computed(() =>
    activeCustomers.value.filter(c => !c.Important)
  )

  function getCustomerByName(name: string): CustomerDto | undefined {
    return service.entities.value.find(c => c.Name === name)
  }

  return {
    ...service,
    allCustomersSorted,
    activeCustomers,
    importantCustomers,
    normalCustomers,
    getCustomerByName,
  }
})
