import type { RefPropertyDto } from './common'

export interface CommentListDto {
  Id: string
  Description: string
  CreatedAt: string
  CreatedBy: RefPropertyDto
  IHaveRead: boolean
}

export interface CommentCreateDto {
  Description: string
}
