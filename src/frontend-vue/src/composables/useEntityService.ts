/**
 * Base entity service composable providing CRUD operations, reactive store,
 * and SignalR real-time synchronisation.
 *
 * Uses an internal `Map<string, TDto>` for O(1) lookups and exposes derived
 * computed refs for list and record views.
 *
 * @example
 * ```ts
 * const users = useEntityService<UserDto, UserCreateDto>({
 *   apiPath: 'api/user',
 *   entityName: 'User',
 *   enableSignalR: true,
 *   loadOnInit: true,
 * });
 *
 * await users.initialize();
 * const all = users.entities.value;
 * ```
 */

import { ref, computed, type Ref, type ComputedRef } from 'vue';
import { useHttpClient, type HttpClient } from './useHttpClient';
import { useSignalR } from './useSignalR';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export interface EntityServiceConfig {
  /** API path (e.g. 'api/user') */
  apiPath: string;
  /** Entity name for SignalR subscriptions (e.g. 'User') */
  entityName: string;
  /** Enable SignalR real-time updates (default: true) */
  enableSignalR?: boolean;
  /** Load all entities on initialization (default: true) */
  loadOnInit?: boolean;
  /** Handle FullSync events from SignalR */
  handleFullSync?: boolean;
}

interface DataEvent<T = unknown> {
  Subject: string;
  Action: 'Created' | 'Updated' | 'Deleted' | 'Custom' | 'FullSync';
  CustomAction?: string;
  Payload: T[];
  MetaData?: Record<string, unknown>;
}

export interface EntityService<
  TDto extends { Id: string },
  TCreateDto = Partial<TDto>,
> {
  /** Computed list of all entities currently in the store. */
  entities: ComputedRef<TDto[]>;
  /** Computed record of entities keyed by Id. */
  entityMap: ComputedRef<Record<string, TDto>>;
  /** Whether `loadAll` has been called at least once. */
  allLoaded: Ref<boolean>;

  /** Fetch all entities from the REST API and populate the store. */
  loadAll(): Promise<TDto[]>;
  /** Get an entity by ID — returns from store if available, otherwise fetches. */
  getById(id: string): Promise<TDto>;
  /** Get an entity from the store only (no network call). */
  getFromStore(id: string): TDto | undefined;
  /** Create a new entity via POST. */
  createEntity(dto: TCreateDto): Promise<TDto>;
  /** Update an entity via PUT with optimistic update and rollback. */
  updateEntity(id: string, dto: Partial<TDto>): Promise<TDto>;
  /** Delete entities via DELETE. */
  deleteEntities(ids: string[]): Promise<void>;
  /** Archive entities. */
  archive(ids: string[]): Promise<void>;
  /** Restore archived entities. */
  restore(ids: string[]): Promise<void>;

  /** Upsert entities into the store. */
  setStoreEntities(dtos: TDto[]): void;
  /** Replace all entities in the store. */
  setAllStoreEntities(dtos: TDto[]): void;
  /** Remove entities from the store by ID. */
  deleteStoreEntities(ids: string[]): void;

  /** Set up SignalR subscription and perform initial data load. */
  initialize(): Promise<void>;

  /** The underlying HTTP client for custom endpoint calls. */
  httpClient: HttpClient;
}

// ---------------------------------------------------------------------------
// Composable
// ---------------------------------------------------------------------------

export function useEntityService<
  TDto extends { Id: string },
  TCreateDto = Partial<TDto>,
>(config: EntityServiceConfig): EntityService<TDto, TCreateDto> {
  // Resolve defaults
  const resolvedConfig: Required<EntityServiceConfig> = {
    enableSignalR: true,
    loadOnInit: true,
    handleFullSync: false,
    ...config,
  };

  // ---------------------------------------------------------------------------
  // State
  // ---------------------------------------------------------------------------

  const store = ref(new Map<string, TDto>()) as Ref<Map<string, TDto>>;
  const allLoaded = ref(false);

  // ---------------------------------------------------------------------------
  // Derived
  // ---------------------------------------------------------------------------

  const entities: ComputedRef<TDto[]> = computed(() => {
    return Array.from(store.value.values());
  });

  const entityMap: ComputedRef<Record<string, TDto>> = computed(() => {
    const record: Record<string, TDto> = {};
    store.value.forEach((dto, id) => {
      record[id] = dto;
    });
    return record;
  });

  // ---------------------------------------------------------------------------
  // Infrastructure
  // ---------------------------------------------------------------------------

  const httpClient = useHttpClient(resolvedConfig.apiPath);
  const signalr = useSignalR();
  let signalrSubscribed = false;

  // ---------------------------------------------------------------------------
  // Store mutations
  // ---------------------------------------------------------------------------

  function setStoreEntities(dtos: TDto[]): void {
    const next = new Map(store.value);
    for (const dto of dtos) {
      next.set(dto.Id, dto);
    }
    store.value = next;
  }

  function setAllStoreEntities(dtos: TDto[]): void {
    const next = new Map<string, TDto>();
    for (const dto of dtos) {
      next.set(dto.Id, dto);
    }
    store.value = next;
    allLoaded.value = true;
  }

  function deleteStoreEntities(ids: string[]): void {
    const next = new Map(store.value);
    for (const id of ids) {
      next.delete(id);
    }
    store.value = next;
  }

  // ---------------------------------------------------------------------------
  // API methods
  // ---------------------------------------------------------------------------

  async function loadAll(): Promise<TDto[]> {
    const data = await httpClient.get<TDto[]>();
    setAllStoreEntities(data);
    return data;
  }

  async function getById(id: string): Promise<TDto> {
    const existing = store.value.get(id);
    if (existing) return existing;

    const dto = await httpClient.addPath(id).get<TDto>();
    setStoreEntities([dto]);
    return dto;
  }

  function getFromStore(id: string): TDto | undefined {
    return store.value.get(id);
  }

  async function createEntity(dto: TCreateDto): Promise<TDto> {
    const result = await httpClient.post<TDto>(dto);

    // If SignalR already delivered this entity, don't overwrite with pending
    const existing = store.value.get(result.Id);
    if (existing) return existing;

    const pending = { ...dto, Id: result.Id, EntityStatus: 'Pending' } as unknown as TDto;
    setStoreEntities([pending]);
    return pending;
  }

  async function updateEntity(id: string, dto: Partial<TDto>): Promise<TDto> {
    // 1. Snapshot current entity for rollback
    const snapshot = store.value.get(id);

    // 2. Optimistic update with pending status
    const pending = snapshot
      ? ({ ...snapshot, ...dto, EntityStatus: 'Pending' } as TDto)
      : ({ ...dto, Id: id, EntityStatus: 'Pending' } as unknown as TDto);
    setStoreEntities([pending]);

    try {
      // 3. PUT to API
      await httpClient.addPath(id).put<TDto>(dto);
      // 5. SignalR will deliver the confirmed state
      return pending;
    } catch (error) {
      // 4. Rollback on error
      if (snapshot) {
        setStoreEntities([snapshot]);
      } else {
        deleteStoreEntities([id]);
      }
      throw error;
    }
  }

  async function deleteEntities(ids: string[]): Promise<void> {
    await httpClient.delete<void>(ids);
    // Store update will come via SignalR
  }

  async function archive(ids: string[]): Promise<void> {
    await httpClient.addPath('archive').put<void>(ids);
  }

  async function restore(ids: string[]): Promise<void> {
    await httpClient
      .addPath('archive')
      .setQueryParameter('restore', 'true')
      .put<void>(ids);
  }

  // ---------------------------------------------------------------------------
  // SignalR integration
  // ---------------------------------------------------------------------------

  function subscribeToSignalR(): void {
    signalr.stream<DataEvent>(`${resolvedConfig.entityName}Actions.Subscribe`).subscribe({
      next: (ev) => {
        switch (ev.Action) {
          case 'Created':
          case 'Updated': {
            const dtos = ev.Payload as unknown as TDto[];
            setStoreEntities(dtos);
            break;
          }
          case 'Deleted': {
            const ids = ev.Payload as unknown as string[];
            deleteStoreEntities(ids);
            break;
          }
          case 'FullSync': {
            if (resolvedConfig.handleFullSync) {
              const dtos = ev.Payload as unknown as TDto[];
              setAllStoreEntities(dtos);
            }
            break;
          }
        }
      },
      error: (err) => {
        console.error(`[EntityService:${resolvedConfig.entityName}] SignalR stream error:`, err);
      },
    });
  }

  // ---------------------------------------------------------------------------
  // Initialization
  // ---------------------------------------------------------------------------

  async function initialize(): Promise<void> {
    if (resolvedConfig.enableSignalR && !signalrSubscribed) {
      signalrSubscribed = true;
      subscribeToSignalR();

      // useSignalR restores active streams itself. Only the REST drift correction
      // belongs in the reconnect callback; subscribing here again would multiply
      // identical streams after every transient disconnect.
      if (!resolvedConfig.handleFullSync) {
        signalr.runOnReconnect(
          () => void loadAll(),
          `${resolvedConfig.entityName}Actions.Reload`,
        );
      }
    }

    if (resolvedConfig.loadOnInit) {
      await loadAll();
    }
  }

  // ---------------------------------------------------------------------------
  // Return
  // ---------------------------------------------------------------------------

  return {
    entities,
    entityMap,
    allLoaded,

    loadAll,
    getById,
    getFromStore,
    createEntity,
    updateEntity,
    deleteEntities,
    archive,
    restore,

    setStoreEntities,
    setAllStoreEntities,
    deleteStoreEntities,

    initialize,

    httpClient,
  };
}
