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
  /** Navigate to MFA setup/step-up when loadAll is refused (default: true).
   * Optional shell widgets can keep background loads local; mutations stay unchanged. */
  redirectOnLoadStepUp?: boolean;
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
  /** Whether a full list was loaded successfully (reset after a failed reload). */
  allLoaded: Ref<boolean>;
  /** The last full-list load failed. Incremental events do not make it complete. */
  loadFailed: Ref<boolean>;

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
    redirectOnLoadStepUp: true,
    handleFullSync: false,
    ...config,
  };

  // ---------------------------------------------------------------------------
  // State
  // ---------------------------------------------------------------------------

  const store = ref(new Map<string, TDto>()) as Ref<Map<string, TDto>>;
  const allLoaded = ref(false);
  const loadFailed = ref(false);
  let snapshotRevision = 0;

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
  let unsubscribeEntityStream: (() => void) | undefined;
  let reconnectRegistered = false;

  // B10-UI02 — ordering between REST and live pushes. Every pushed change gets a
  // sequence number; the entities it touched remember theirs. A snapshot replays the
  // changes pushed after it started, and a failed PUT only rolls back an entity no
  // push has changed since.
  let changeSeq = 0;
  const entityChangeSeq = new Map<string, number>();
  let loadsInFlight = 0;
  let pushedDuringLoads: { seq: number; ev: DataEvent }[] = [];

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
    // An authoritative snapshot (including FullSync) supersedes pending reads.
    snapshotRevision++;
    const next = new Map<string, TDto>();
    for (const dto of dtos) {
      next.set(dto.Id, dto);
    }
    store.value = next;
    allLoaded.value = true;
    loadFailed.value = false;
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
    const revision = ++snapshotRevision;
    const startSeq = changeSeq;
    loadsInFlight++;
    try {
      const data = await httpClient.get<TDto[]>({ redirectOnStepUp: resolvedConfig.redirectOnLoadStepUp });
      if (revision === snapshotRevision) {
        setAllStoreEntities(data);
        // The snapshot was read before these pushes arrived; it must not undo them.
        for (const pushed of pushedDuringLoads)
          if (pushed.seq > startSeq) applyDelta(pushed.ev);
      }
      return data;
    } catch (error) {
      // A refused/failed snapshot must not leave the old list looking current.
      // Keep the failure until a full reload/FullSync succeeds; later deltas
      // alone cannot repair everything missed during the disconnect.
      if (revision === snapshotRevision) {
        store.value = new Map();
        allLoaded.value = false;
        loadFailed.value = true;
      }
      throw error; // explicit callers still own normal HTTP/CRUD error handling
    } finally {
      if (--loadsInFlight === 0) pushedDuringLoads = [];
    }
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
    const seqAtStart = entityChangeSeq.get(id);

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
      // 4. Rollback on error — unless a push delivered a newer server state meanwhile.
      if (entityChangeSeq.get(id) === seqAtStart) {
        if (snapshot) {
          setStoreEntities([snapshot]);
        } else {
          deleteStoreEntities([id]);
        }
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

  function applyDelta(ev: DataEvent): void {
    switch (ev.Action) {
      case 'Created':
      case 'Updated':
        setStoreEntities(ev.Payload as unknown as TDto[]);
        break;
      case 'Deleted':
        deleteStoreEntities(ev.Payload as unknown as string[]);
        break;
    }
  }

  function changedIds(ev: DataEvent): string[] {
    return ev.Action === 'Deleted'
      ? (ev.Payload as unknown as string[])
      : (ev.Payload as unknown as TDto[]).map((dto) => dto.Id);
  }

  function handlePush(ev: DataEvent): void {
    if (ev.Action === 'FullSync') {
      if (resolvedConfig.handleFullSync) {
        // An authoritative snapshot: nothing pushed before it needs replaying.
        pushedDuringLoads = [];
        setAllStoreEntities(ev.Payload as unknown as TDto[]);
      }
      return;
    }
    if (ev.Action !== 'Created' && ev.Action !== 'Updated' && ev.Action !== 'Deleted') return;

    const seq = ++changeSeq;
    for (const id of changedIds(ev)) entityChangeSeq.set(id, seq);
    if (loadsInFlight > 0) pushedDuringLoads.push({ seq, ev });
    applyDelta(ev);
  }

  /** B10-UI03 — a stream the server completed (e.g. refused for a missing read right)
   *  or that failed is released, so the next initialize() subscribes again instead of
   *  leaving the live channel dead while the connection itself stays up. */
  function releaseEntityStream(): void {
    unsubscribeEntityStream?.();
    unsubscribeEntityStream = undefined;
    signalrSubscribed = false;
  }

  function subscribeToSignalR(): void {
    unsubscribeEntityStream = signalr.stream<DataEvent>(`${resolvedConfig.entityName}Actions.Subscribe`).subscribe({
      next: handlePush,
      error: (err) => {
        console.error(`[EntityService:${resolvedConfig.entityName}] SignalR stream error:`, err);
        releaseEntityStream();
      },
      complete: () => releaseEntityStream(),
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
      if (!resolvedConfig.handleFullSync && !reconnectRegistered) {
        reconnectRegistered = true;
        signalr.runOnReconnect(
          // This is a background callback, not an awaited caller. loadAll has
          // already invalidated its snapshot and exposed the failure state.
          () => loadAll().catch(() => {}),
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
    loadFailed,

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
