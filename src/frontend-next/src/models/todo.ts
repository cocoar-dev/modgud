import type { EntityStatus, RefPropertyDto } from './common'

export type TodoStatus = 'None' | 'New' | 'InProgress' | 'Done' | 'Info'

export interface TodoDto {
  Id: string
  Title: string
  Description?: string
  DueDate?: string
  Status: TodoStatus
  Customer?: RefPropertyDto
  Responsibles: RefPropertyDto[]
  Critical: boolean
  AwaitingFeedback: boolean
  IsArchived: boolean
  Comments: unknown[]
  ParentTodoId?: string
  CreatedBy?: RefPropertyDto
  CreatedAt?: string
  UpdatedBy?: RefPropertyDto
  UpdatedAt?: string
  ChildTodosCount: number
  ChildTodosUnreadCommentsCount: number
  CommentsCount: number
  UnreadComments: number
  LastTouchedAt?: string
  AggregateVersion: number
  EntityStatus: EntityStatus
}

export interface TodoCreateDto {
  Title: string
  Description?: string
  DueDate?: string
  Status: TodoStatus
  Customer?: RefPropertyDto
  Responsibles?: RefPropertyDto[]
  Critical: boolean
  AwaitingFeedback: boolean
}

export interface TodoListDto {
  Id: string
  Title: string
  Description?: string
  DueDate?: string
  Status: TodoStatus
  Customer?: RefPropertyDto
  Responsibles: RefPropertyDto[]
  Critical: boolean
  AwaitingFeedback: boolean
  CommentsCount: number
  UnreadComments: number
  CreatedBy?: RefPropertyDto
  LastTouchedAt?: string
  ParentTodoId?: string
  ChildTodosCount: number
  ChildTodosUnreadCommentsCount: number
  IsArchived: boolean
  AggregateVersion: number
  EntityStatus: EntityStatus
}

export interface TodoDetailsModel {
  Id: string
  Title: string
  Description?: string
  DueDate?: string
  Status: TodoStatus
  Customer?: RefPropertyDto
  Responsibles: RefPropertyDto[]
  Critical: boolean
  AwaitingFeedback: boolean
  IsArchived: boolean
  Comments: unknown[]
  ParentTodoId?: string
  CreatedBy?: RefPropertyDto
  CreatedAt?: string
  UpdatedBy?: RefPropertyDto
  UpdatedAt?: string
  EntityStatus: EntityStatus
}

export interface TodoStatusUpdateRequestDto {
  Ids: string[]
  Status: TodoStatus
}

export interface TodoFlagsUpdateRequestDto {
  Ids: string[]
  AddFlags?: string[]
  RemoveFlags?: string[]
}
