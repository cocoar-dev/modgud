import type { EntityService } from '../composables/useEntityService'
import type { ServiceAccountDto, ServiceAccountCreateDto } from '../models/serviceAccount'

/** Keep one-time credentials in the creator's response, never in the shared entity store. */
export async function createServiceAccount(
  service: Pick<EntityService<ServiceAccountDto, ServiceAccountCreateDto>, 'httpClient' | 'getFromStore' | 'setStoreEntities'>,
  dto: ServiceAccountCreateDto,
): Promise<ServiceAccountDto> {
  const created = await service.httpClient.post<ServiceAccountDto>(dto)
  // A feed update may have arrived before the HTTP response. Preserve that newer
  // store entry, but do not return it in place of the response-only credential.
  if (!service.getFromStore(created.Id)) {
    const account = { ...created }
    delete account.InitialCredential
    service.setStoreEntities([account])
  }
  return created
}
