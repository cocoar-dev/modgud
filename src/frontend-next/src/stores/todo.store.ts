import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { useEntityService } from '@/composables/useEntityService'
import type { TodoDto, TodoCreateDto, TodoListDto, TodoDetailsModel, TodoStatus, TodoStatusUpdateRequestDto, TodoFlagsUpdateRequestDto } from '@/models/todo'
import type { SelectOption } from '@/models/common'

export const useTodoStore = defineStore('todo', () => {
  const service = useEntityService<TodoDto, TodoCreateDto>({
    apiPath: '/api/todo',
    entityName: 'Todo',
    enableSignalR: true,
    loadOnInit: true,
  })

  // Override setStoreEntities to filter archived
  const originalSetStore = service.setStoreEntities
  service.setStoreEntities = (dtos: TodoDto[]) => {
    originalSetStore(dtos.filter(d => !d.IsArchived))
  }
  const originalSetAll = service.setAllStoreEntities
  service.setAllStoreEntities = (dtos: TodoDto[]) => {
    originalSetAll(dtos.filter(d => !d.IsArchived))
  }

  // UI state (persists across navigation)
  const openRows = ref<string[]>([])


  const statusOptions: SelectOption<TodoStatus>[] = [
    { label: '', value: 'None' },
    { label: 'Neu', value: 'New' },
    { label: 'In Bearbeitung', value: 'InProgress' },
    { label: 'Erledigt', value: 'Done' },
    { label: 'Info', value: 'Info' },
  ]

  // Map TodoDto → TodoListDto
  function mapToListDto(dto: TodoDto): TodoListDto {
    return {
      Id: dto.Id,
      Title: dto.Title,
      Description: dto.Description,
      DueDate: dto.DueDate,
      Status: dto.Status,
      Customer: dto.Customer,
      Responsibles: dto.Responsibles,
      Critical: dto.Critical,
      AwaitingFeedback: dto.AwaitingFeedback,
      CommentsCount: dto.CommentsCount,
      UnreadComments: dto.UnreadComments,
      CreatedBy: dto.CreatedBy,
      LastTouchedAt: dto.LastTouchedAt,
      ParentTodoId: dto.ParentTodoId,
      ChildTodosCount: dto.ChildTodosCount,
      ChildTodosUnreadCommentsCount: dto.ChildTodosUnreadCommentsCount,
      IsArchived: dto.IsArchived,
      AggregateVersion: dto.AggregateVersion,
      EntityStatus: dto.EntityStatus,
    }
  }

  const todoslist = computed(() => service.entities.value.map(mapToListDto))

  // Data operations
  async function getArchived(): Promise<TodoDto[]> {
    return service.httpClient.addPath('archive').get<TodoDto[]>()
  }

  async function updateStatus(request: TodoStatusUpdateRequestDto): Promise<void> {
    // Optimistic update
    const snapshots: TodoDto[] = []
    for (const id of request.Ids) {
      const existing = service.getFromStore(id)
      if (existing) {
        snapshots.push({ ...existing })
        service.setStoreEntities([{ ...existing, Status: request.Status, EntityStatus: 'Pending' as const }])
      }
    }

    try {
      await service.httpClient.addPath('update', 'status').put(request)
    } catch (error) {
      // Rollback
      if (snapshots.length > 0) {
        service.setStoreEntities(snapshots)
      }
      throw error
    }
  }

  async function patchFlags(request: TodoFlagsUpdateRequestDto): Promise<void> {
    // Optimistic update
    const snapshots: TodoDto[] = []
    for (const id of request.Ids) {
      const existing = service.getFromStore(id)
      if (existing) {
        snapshots.push({ ...existing })
        const patched = { ...existing, EntityStatus: 'Pending' as const }
        if (request.AddFlags?.includes('Critical')) patched.Critical = true
        if (request.RemoveFlags?.includes('Critical')) patched.Critical = false
        if (request.AddFlags?.includes('AwaitingFeedback')) patched.AwaitingFeedback = true
        if (request.RemoveFlags?.includes('AwaitingFeedback')) patched.AwaitingFeedback = false
        service.setStoreEntities([patched])
      }
    }

    try {
      await service.httpClient.addPath('update', 'flags').patch(request)
    } catch (error) {
      if (snapshots.length > 0) {
        service.setStoreEntities(snapshots)
      }
      throw error
    }
  }

  async function createNew(dto: TodoCreateDto, parentTodo?: string): Promise<TodoDto> {
    let http = service.httpClient
    if (parentTodo) {
      http = http.setQueryParameter('parentTodo', parentTodo)
    }
    return http.post<TodoDto>(dto)
  }

  async function updateTodo(id: string, dto: Partial<TodoDto>): Promise<void> {
    const existing = service.getFromStore(id)
    if (existing) {
      // Optimistic
      service.setStoreEntities([{ ...existing, ...dto, EntityStatus: 'Pending' as const }])
    }
    try {
      await service.httpClient.addPath(id).put(dto)
    } catch (error) {
      if (existing) {
        service.setStoreEntities([existing])
      }
      throw error
    }
  }

  async function archive(ids: string[]): Promise<void> {
    await service.archive(ids)
    // Remove from store since we filter archived
    service.deleteStoreEntities(ids)
  }

  async function restore(ids: string[]): Promise<void> {
    await service.restore(ids)
  }

  async function deleteTodos(ids: string[]): Promise<void> {
    await service.deleteEntities(ids)
    service.deleteStoreEntities(ids)
  }

  async function convertToSubTodo(subTodoId: string, parentTodoId: string): Promise<void> {
    await service.httpClient.addPath(subTodoId, 'move-into', parentTodoId).post()
  }

  async function convertToParentTodo(ids: string[]): Promise<void> {
    await service.httpClient.addPath('convert-to-parent').post(ids)
  }

  async function getDetailsModel(id: string): Promise<TodoDetailsModel> {
    const dto = await service.getById(id)
    return {
      Id: dto.Id,
      Title: dto.Title,
      Description: dto.Description,
      DueDate: dto.DueDate,
      Status: dto.Status,
      Customer: dto.Customer,
      Responsibles: dto.Responsibles,
      Critical: dto.Critical,
      AwaitingFeedback: dto.AwaitingFeedback,
      IsArchived: dto.IsArchived,
      Comments: dto.Comments,
      ParentTodoId: dto.ParentTodoId,
      CreatedBy: dto.CreatedBy,
      CreatedAt: dto.CreatedAt,
      UpdatedBy: dto.UpdatedBy,
      UpdatedAt: dto.UpdatedAt,
      EntityStatus: dto.EntityStatus,
    }
  }

  return {
    // Entity service
    ...service,
    // UI state
    openRows,
    statusOptions,
    // Computed
    todoslist,
    // Data methods
    getArchived,
    updateStatus,
    patchFlags,
    createNew,
    updateTodo,
    archive,
    restore,
    deleteTodos,
    convertToSubTodo,
    convertToParentTodo,
    getDetailsModel,
  }
})
